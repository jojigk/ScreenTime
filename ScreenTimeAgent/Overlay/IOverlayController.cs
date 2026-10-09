namespace ScreenTimeAgent.Overlay;

public interface IOverlayController
{
    void ShowCountdown(int remainingSeconds);

    void UpdateRemaining(int remainingSeconds);

    /// <summary>Locks the session (the agent's primary lock path) and hides the overlay.</summary>
    void Lock();

    /// <summary>Testing-mode path when LockEnabled is false: shows "time is over" but never locks.</summary>
    void ShowExpired();
}
