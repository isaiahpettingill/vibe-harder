using System.Runtime.InteropServices;
using System.Text;

namespace CodexManager;

internal static class WindowsClipboard
{
#if BROWSER_CLIENT
    public static Task<bool> SetText(IntPtr owner, string text) => Task.FromResult(false);
#else
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;

    public static async Task<bool> SetText(IntPtr owner, string text)
    {
        if (!OperatingSystem.IsWindows() || owner == IntPtr.Zero) return false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner))
            {
                try
                {
                    var bytes = Encoding.Unicode.GetBytes(text + '\0');
                    var memory = GlobalAlloc(Moveable, (nuint)bytes.Length);
                    if (memory == IntPtr.Zero) return false;
                    try
                    {
                        var address = GlobalLock(memory);
                        if (address == IntPtr.Zero) return false;
                        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
                        finally { GlobalUnlock(memory); }
                        if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == IntPtr.Zero) return false;
                        memory = IntPtr.Zero;
                        return true;
                    }
                    finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
                }
                finally { CloseClipboard(); }
            }
            await Task.Delay(10);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
#endif
}
