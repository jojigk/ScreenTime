using System.Windows.Threading;
using ScreenTimeAgent.Locking;

namespace ScreenTimeAgent.Overlay;

/// <summary>
/// Called from the pipe client's background thread; marshals every WPF interaction onto the
/// UI-thread dispatcher, since <see cref="CountdownOverlayWindow"/> can only be touched there.
/// </summary>
public sealed class OverlayController(Dispatcher dispatcher) : IOverlayController
{
    private CountdownOverlayWindow? _window;

    public void ShowCountdown(int remainingSeconds)
    {
        dispatcher.Invoke(() =>
        {
            _window ??= new CountdownOverlayWindow();
            _window.UpdateRemaining(remainingSeconds);
            _window.ShowOverlay();
        });
    }

    public void UpdateRemaining(int remainingSeconds)
    {
        dispatcher.Invoke(() => _window?.UpdateRemaining(remainingSeconds));
    }

    public void Lock()
    {
        AgentSessionLock.Lock();
        dispatcher.Invoke(() => _window?.HideOverlay());
    }

    public void ShowExpired()
    {
        dispatcher.Invoke(() =>
        {
            _window ??= new CountdownOverlayWindow();
            _window.ShowExpired();
            _window.ShowOverlay();
        });
    }
}
