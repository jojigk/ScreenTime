using System.Runtime.InteropServices;

namespace ScreenTimeAgent.Idle;

/// <summary>Wraps GetLastInputInfo to compute how long it's been since any keyboard/mouse input.</summary>
public sealed class Win32IdleTimeProvider : IIdleTimeProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values are GetTickCount()-style millisecond counters that wrap around every ~49.7
        // days; unsigned subtraction handles that wraparound correctly via modular arithmetic.
        var currentTicks = unchecked((uint)Environment.TickCount);
        var idleMs = unchecked(currentTicks - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
