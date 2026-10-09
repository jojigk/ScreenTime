using System.Text.Json.Serialization;

namespace ScreenTime.Common.Ipc;

/// <summary>
/// Base type for all messages exchanged over the ScreenTime named pipe. Serialized with a
/// "$type" discriminator so a single framed stream can carry every message shape.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HelloMessage), nameof(HelloMessage))]
[JsonDerivedType(typeof(HeartbeatMessage), nameof(HeartbeatMessage))]
[JsonDerivedType(typeof(StatusUpdateMessage), nameof(StatusUpdateMessage))]
[JsonDerivedType(typeof(ShowCountdownMessage), nameof(ShowCountdownMessage))]
[JsonDerivedType(typeof(LockNowMessage), nameof(LockNowMessage))]
public abstract class PipeMessage
{
}

/// <summary>Sent by the agent immediately after connecting.</summary>
public sealed class HelloMessage : PipeMessage
{
    public int SessionId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>The connecting user's Windows SID — the stable identity quota tracking is keyed by.</summary>
    public string Sid { get; set; } = string.Empty;
}

/// <summary>Sent periodically by the agent to report whether the user is actively using the session.</summary>
public sealed class HeartbeatMessage : PipeMessage
{
    public bool IsActive { get; set; }
}

/// <summary>Sent by the service after each tick so the agent can display remaining time.</summary>
public sealed class StatusUpdateMessage : PipeMessage
{
    public int RemainingSeconds { get; set; }

    public int QuotaSeconds { get; set; }

    /// <summary>Mirrors the server's current QuotaConfig.LockEnabled so the agent never needs its own copy of that policy.</summary>
    public bool LockEnabled { get; set; } = true;
}

/// <summary>Sent once when remaining time first crosses the countdown threshold.</summary>
public sealed class ShowCountdownMessage : PipeMessage
{
    public int RemainingSeconds { get; set; }
}

/// <summary>
/// Notice that the day's quota is exhausted. The service does not wait for an acknowledgement —
/// its own fallback lock (if <see cref="LockEnabled"/>) runs independently of the agent.
/// </summary>
public sealed class LockNowMessage : PipeMessage
{
    /// <summary>
    /// When false, this is testing/preview mode: the agent should show the "time is over" state
    /// but must not actually call LockWorkStation.
    /// </summary>
    public bool LockEnabled { get; set; } = true;
}
