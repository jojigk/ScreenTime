using System.Runtime.InteropServices;

namespace ScreenTimeAgent.Locking;

/// <summary>
/// The primary lock path: a single benign LockWorkStation call from the agent's own process, in
/// its own session, as the standard user it already is. No token duplication, no elevation — the
/// low-risk counterpart to the service's CreateProcessAsUser-based fallback lock.
/// </summary>
public static class AgentSessionLock
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    public static void Lock() => LockWorkStation();
}
