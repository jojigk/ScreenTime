namespace ScreenTimeService.Locking;

/// <summary>
/// Locks a specific interactive session. LockWorkStation cannot be called cross-session from a
/// SYSTEM service, so this launches "rundll32 user32.dll,LockWorkStation" inside that session via
/// <see cref="InteractiveProcessLauncher"/> instead — works even if the agent has been killed,
/// since it doesn't depend on it. The agent also locks itself directly on receiving a pipe
/// message; this is the unconditional fallback, not the primary path (see plan notes on why).
/// The caller supplies the exact session id whose quota was exhausted — this must not fall back
/// to "whichever session is at the console," since that may belong to a different account than
/// the one that ran out of time.
/// </summary>
public sealed class SessionLockService(InteractiveProcessLauncher launcher)
{
    public async Task LockSessionAsync(int sessionId)
    {
        await launcher.LaunchAsync(sessionId, applicationName: null, "rundll32.exe user32.dll,LockWorkStation", workingDirectory: null)
            .ConfigureAwait(false);
    }
}
