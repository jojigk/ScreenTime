using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ScreenTime.Common;
using ScreenTime.Common.Ipc;
using ScreenTimeService.Locking;
using ScreenTimeService.Quota;

namespace ScreenTimeService.Ipc;

/// <summary>
/// Named-pipe server the agent (standard user, different session) talks to. The ACL explicitly
/// grants Authenticated Users read/write, since the default pipe ACL would otherwise only allow
/// same-user access and the service runs as SYSTEM while the agent runs as the logged-in user.
/// Each connection is resolved to a specific account's <see cref="UsageTracker"/> (via the
/// connecting agent's SID) and only ever talks to that one connection — status/lock messages are
/// no longer broadcast to every connected agent, since that would leak one account's lock across
/// to every other logged-in account.
/// </summary>
public sealed class PipeServerHostedService(
    UsageTrackerRegistry registry,
    QuotaConfig config,
    SessionLockService lockService,
    ILogger<PipeServerHostedService> logger) : BackgroundService
{
    private const int MaxServerInstances = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServerStream();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create named pipe server; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipe connection wait failed.");
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleConnectionAsync(server, stoppingToken);
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            Paths.PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        UsageTracker? tracker = null;
        Action<int>? onRemainingChanged = null;
        Action<int>? onCountdownStarted = null;
        Action? onQuotaExhausted = null;

        try
        {
            // The agent always sends Hello immediately after connecting, before anything else —
            // we need it first to know which account's tracker this connection belongs to.
            var first = await PipeFraming.ReadMessageAsync(server, stoppingToken).ConfigureAwait(false);
            if (first is not HelloMessage hello || string.IsNullOrEmpty(hello.Sid))
            {
                logger.LogWarning("Pipe connection did not send a valid Hello first; closing it.");
                return;
            }

            logger.LogInformation("Agent connected from session {SessionId} ({UserName}).", hello.SessionId, hello.UserName);

            registry.NoteSession(hello.Sid, hello.SessionId);
            tracker = registry.GetOrCreate(hello.Sid);

            var remaining = tracker.RemainingSeconds;
            await PipeFraming.WriteMessageAsync(server, new StatusUpdateMessage
            {
                RemainingSeconds = remaining,
                QuotaSeconds = tracker.DailyLimitSeconds,
                LockEnabled = config.LockEnabled,
            }, stoppingToken).ConfigureAwait(false);

            // CountdownStarted is a one-time-per-day event on UsageTracker — a client that
            // connects (or reconnects) after that instant would otherwise never learn it should
            // show the countdown. Catch it up from current state instead of only the historical event.
            if (remaining > 0 && remaining <= config.CountdownSeconds)
            {
                await PipeFraming.WriteMessageAsync(server, new ShowCountdownMessage { RemainingSeconds = remaining }, stoppingToken)
                    .ConfigureAwait(false);
            }

            // A fresh connection can observe an already-exhausted quota (e.g. the supervisor
            // just relaunched a killed agent) — enforce it immediately on the service side too,
            // rather than waiting for the next rate-limited re-exhaustion event.
            if (remaining <= 0)
            {
                logger.LogInformation(
                    "Connection from {Sid} (session {SessionId}) is already exhausted; enforcing immediately (LockEnabled={LockEnabled}).",
                    hello.Sid, hello.SessionId, config.LockEnabled);
                if (config.LockEnabled)
                {
                    _ = lockService.LockSessionAsync(hello.SessionId);
                }
            }

            onRemainingChanged = r => _ = SendAsync(server, new StatusUpdateMessage
            {
                RemainingSeconds = r,
                QuotaSeconds = tracker.DailyLimitSeconds,
                LockEnabled = config.LockEnabled,
            });
            onCountdownStarted = r => _ = SendAsync(server, new ShowCountdownMessage { RemainingSeconds = r });
            onQuotaExhausted = () =>
            {
                logger.LogInformation(
                    "QuotaExhausted fired for {Sid} (session {SessionId}); notifying agent to lock (LockEnabled={LockEnabled}).",
                    hello.Sid, hello.SessionId, config.LockEnabled);
                _ = SendAsync(server, new LockNowMessage { LockEnabled = config.LockEnabled });
            };

            tracker.RemainingChanged += onRemainingChanged;
            tracker.CountdownStarted += onCountdownStarted;
            tracker.QuotaExhausted += onQuotaExhausted;

            while (!stoppingToken.IsCancellationRequested && server.IsConnected)
            {
                var message = await PipeFraming.ReadMessageAsync(server, stoppingToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message is HeartbeatMessage heartbeat)
                {
                    tracker.ReportActivity(heartbeat.IsActive);
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected abruptly (killed process, session logoff) — not an error.
        }
        catch (OperationCanceledException)
        {
            // Service shutting down.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pipe connection handler faulted.");
        }
        finally
        {
            if (tracker is not null)
            {
                if (onRemainingChanged is not null) tracker.RemainingChanged -= onRemainingChanged;
                if (onCountdownStarted is not null) tracker.CountdownStarted -= onCountdownStarted;
                if (onQuotaExhausted is not null) tracker.QuotaExhausted -= onQuotaExhausted;

                // Losing the connection is treated as idle immediately rather than waiting out the
                // heartbeat timeout, so usage doesn't keep accruing after the agent disappears.
                tracker.ReportActivity(false);
            }

            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync(NamedPipeServerStream stream, PipeMessage message)
    {
        try
        {
            await PipeFraming.WriteMessageAsync(stream, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send a pipe message to a client (likely disconnected).");
        }
    }
}
