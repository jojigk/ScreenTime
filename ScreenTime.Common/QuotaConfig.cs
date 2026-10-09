namespace ScreenTime.Common;

/// <summary>
/// Admin-editable policy loaded from <see cref="Paths.ConfigFilePath"/>. Distinct from the
/// service host's own appsettings.json, which only configures logging/hosting.
/// </summary>
public sealed class QuotaConfig
{
    /// <summary>
    /// Default daily quota, used on any day of week not covered by <see cref="DailyLimitOverrides"/>.
    /// </summary>
    public int DailyLimitMinutes { get; set; } = 70;

    /// <summary>
    /// Per-day-of-week quota overrides (e.g. a higher weekend limit). See <see cref="DailyLimitOverride"/>.
    /// </summary>
    public List<DailyLimitOverride> DailyLimitOverrides { get; set; } = new();

    public int CountdownSeconds { get; set; } = 300;

    public int TickIntervalSeconds { get; set; } = 1;

    public int PersistIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// How far the wall clock is allowed to move backward before it's treated as a
    /// tamper attempt rather than a normal small NTP correction.
    /// </summary>
    public int ClockRollbackToleranceSeconds { get; set; } = 120;

    /// <summary>
    /// If no heartbeat is received within this window, the session is treated as idle.
    /// </summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Full path to ScreenTimeAgent.exe. Empty means "use the convention default" —
    /// a sibling "Agent" folder next to the service's own install directory.
    /// </summary>
    public string AgentExecutablePath { get; set; } = string.Empty;

    /// <summary>How often the service checks whether the agent is running and relaunches it if not.</summary>
    public int AgentSupervisionIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// When false, quota exhaustion is still fully tracked and surfaced (countdown, overlay,
    /// "time is over" state) but neither the agent nor the service actually locks the session.
    /// Testing-only escape hatch — defaults to true (real enforcement) so a fresh install is
    /// never silently inert; must be explicitly set to false in config.json to disable locking.
    /// </summary>
    public bool LockEnabled { get; set; } = true;

    /// <summary>
    /// Recurring weekly windows (e.g. Thursday 5-8PM) during which usage isn't counted and an
    /// already-exhausted quota won't trigger a new lock. See <see cref="ScreenTimeExceptionWindow"/>.
    /// If the session is already locked when a window starts, it stays locked until unlocked the
    /// normal way (password) — the window only suppresses accrual and future lock attempts.
    /// </summary>
    public List<ScreenTimeExceptionWindow> ExceptionWindows { get; set; } = new();
}
