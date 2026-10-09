using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenTime.Common;
using ScreenTime.Common.Ipc;
using ScreenTimeAgent.Idle;
using ScreenTimeAgent.Overlay;

namespace ScreenTimeAgent.Ipc;

/// <summary>
/// Connects to the service's named pipe, reports active/idle heartbeats, and reacts to
/// countdown/lock messages. Reconnects with a fixed backoff — the service may not be up yet,
/// or may restart while the agent keeps running.
/// </summary>
public sealed class PipeClientHostedService(
    IIdleTimeProvider idleTimeProvider,
    IOverlayController overlay,
    ILogger<PipeClientHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(60);
    private const int ConnectTimeoutMs = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Pipe connection attempt failed; will retry.");
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        using var client = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ConnectTimeoutMs, stoppingToken).ConfigureAwait(false);

        logger.LogInformation("Connected to ScreenTimeService.");

        await PipeFraming.WriteMessageAsync(client, new HelloMessage
        {
            SessionId = Process.GetCurrentProcess().SessionId,
            UserName = Environment.UserName,
            Sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty,
        }, stoppingToken).ConfigureAwait(false);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = SendHeartbeatsAsync(client, heartbeatCts.Token);

        try
        {
            await ReadMessagesAsync(client, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Connection is already being torn down; nothing more to do with a heartbeat-loop failure.
            }
        }
    }

    private async Task SendHeartbeatsAsync(NamedPipeClientStream client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var isActive = idleTimeProvider.GetIdleTime() < IdleThreshold;
            await PipeFraming.WriteMessageAsync(client, new HeartbeatMessage { IsActive = isActive }, ct).ConfigureAwait(false);
            await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task ReadMessagesAsync(NamedPipeClientStream client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            var message = await PipeFraming.ReadMessageAsync(client, stoppingToken).ConfigureAwait(false);
            if (message is null)
            {
                logger.LogInformation("Service closed the connection.");
                break;
            }

            switch (message)
            {
                case StatusUpdateMessage status:
                    // A fresh (re)connection can observe an already-exhausted quota (e.g. the
                    // supervisor just relaunched a killed agent) — enforce/show it immediately
                    // rather than waiting for the next rate-limited re-exhaustion event.
                    if (status.RemainingSeconds <= 0)
                    {
                        if (status.LockEnabled)
                        {
                            overlay.Lock();
                        }
                        else
                        {
                            overlay.ShowExpired();
                        }
                    }
                    else
                    {
                        overlay.UpdateRemaining(status.RemainingSeconds);
                    }
                    break;

                case ShowCountdownMessage countdown:
                    overlay.ShowCountdown(countdown.RemainingSeconds);
                    break;

                case LockNowMessage lockNow:
                    if (lockNow.LockEnabled)
                    {
                        overlay.Lock();
                    }
                    else
                    {
                        overlay.ShowExpired();
                    }
                    break;
            }
        }
    }
}
