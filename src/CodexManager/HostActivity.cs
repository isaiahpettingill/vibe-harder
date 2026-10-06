using System.Runtime.InteropServices;

namespace CodexManager;

// Whether someone is at this computer: keyboard or mouse input anywhere on the desktop within the
// last few minutes. Phones are not pushed to while the user is here to see the desktop.
public static class HostActivity
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);
    private static long lastAppInput = long.MinValue;
    // Tests replace the system idle time.
    public static Func<TimeSpan?>? IdleOverride { get; set; }

    // Input in our own window counts where the system idle time is unavailable, such as Wayland.
    public static void Touched() => Interlocked.Exchange(ref lastAppInput, Environment.TickCount64);

    public static bool UserActive() => Idle() is { } idle && idle < ActiveWindow;

    public static TimeSpan? Idle()
    {
        if (IdleOverride is { } idleOverride) return idleOverride();
        var app = Interlocked.Read(ref lastAppInput) is var touched and > long.MinValue ? TimeSpan.FromMilliseconds(Environment.TickCount64 - touched) : (TimeSpan?)null;
        TimeSpan? system;
        try { system = SystemIdle(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { system = null; }
        return system is null ? app : app is null ? system : system < app ? system : app;
    }

    private static TimeSpan? SystemIdle()
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            return GetLastInputInfo(ref info) != 0 ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : null;
        }
        if (OperatingSystem.IsMacOS())
            return TimeSpan.FromSeconds(CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue));
        if (OperatingSystem.IsLinux()) return X11Idle();
        return null;
    }

    private static nint display;
    private static TimeSpan? X11Idle()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        lock (typeof(HostActivity))
        {
            if (display == 0 && (display = XOpenDisplay(0)) == 0) return null;
            var info = XScreenSaverAllocInfo();
            if (info == 0) return null;
            try { return XScreenSaverQueryInfo(display, XDefaultRootWindow(display), info) != 0 ? TimeSpan.FromMilliseconds(Marshal.PtrToStructure<XScreenSaverInfo>(info).Idle) : null; }
            finally { XFree(info); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] private struct XScreenSaverInfo { public nint Window; public int State; public int Kind; public nuint TilOrSince; public nuint Idle; public nuint EventMask; }
    [DllImport("user32.dll")] private static extern int GetLastInputInfo(ref LastInputInfo info);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern double CGEventSourceSecondsSinceLastEventType(int source, uint eventType);
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libXss.so.1")] private static extern nint XScreenSaverAllocInfo();
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(nint display, nint drawable, nint info);
}
