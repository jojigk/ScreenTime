using System.Diagnostics;
using Microsoft.Win32;
using ScreenTime.Common;
using ScreenTimeService.Locking;

namespace ScreenTimeService.Agent;

/// <summary>
/// Keeps ScreenTimeAgent.exe running in whichever session is at the console. Covers both
/// "start it at logon" and "restart it if the user kills it" with one mechanism, reusing
/// <see cref="InteractiveProcessLauncher"/> — a standard user has no way to stop a SYSTEM
/// service from relaunching it.
///
/// Checks run two ways: a <see cref="SystemEvents.SessionSwitch"/> logon event triggers an
/// immediate check (closes the reboot race where an already-exhausted account could be used
/// unlocked until the next poll), and a periodic timer remains as a fallback in case the event
/// is ever missed.
/// </summary>
public sealed class AgentSupervisorHostedService(
    InteractiveProcessLauncher launcher,
    QuotaConfig config,
    ILogger<AgentSupervisorHostedService> logger) : BackgroundService
{
    private const string AgentProcessName = "ScreenTimeAgent";
    private const int NoActiveSession = -1;

    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.AgentSupervisionIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await EnsureAgentRunningGatedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent supervision check failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLogon)
        {
            return;
        }

        logger.LogInformation("Session logon detected; triggering an immediate agent check.");
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureAgentRunningGatedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session-logon-triggered agent check failed.");
            }
        });
    }

    private async Task EnsureAgentRunningGatedAsync()
    {
        if (!await _checkGate.WaitAsync(0).ConfigureAwait(false))
        {
            // A check is already in flight; safe to skip — it's idempotent and another
            // trigger (the next poll, or this same event) will follow shortly regardless.
            return;
        }

        try
        {
            await EnsureAgentRunningAsync().ConfigureAwait(false);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task EnsureAgentRunningAsync()
    {
        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession)
        {
            return;
        }

        if (IsAgentRunningInSession(sessionId))
        {
            return;
        }

        var agentPath = ResolveAgentExecutablePath();
        if (!File.Exists(agentPath))
        {
            logger.LogWarning("Agent executable not found at {Path}; cannot launch it.", agentPath);
            return;
        }

        logger.LogInformation("Agent not running in session {SessionId}; launching it.", sessionId);
        await launcher.LaunchAsync(sessionId, agentPath, $"\"{agentPath}\"", Path.GetDirectoryName(agentPath))
            .ConfigureAwait(false);
    }

    private static bool IsAgentRunningInSession(int sessionId)
    {
        var processes = Process.GetProcessesByName(AgentProcessName);
        try
        {
            foreach (var process in processes)
            {
                if (TryGetSessionId(process) == sessionId)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static int? TryGetSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (InvalidOperationException)
        {
            // Process exited between enumeration and this check.
            return null;
        }
    }

    private string ResolveAgentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(config.AgentExecutablePath))
        {
            return config.AgentExecutablePath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Agent", "ScreenTimeAgent.exe"));
    }
}
