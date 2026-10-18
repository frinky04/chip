using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Chip.Editor.UI;

/// <summary>Asks Windows 10/11 for a dark caption to match the editor; no-op elsewhere.</summary>
static partial class DarkTitleBar
{
    const int DwmwaUseImmersiveDarkMode = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    public static void Apply(IWindow window)
    {
        if (!OperatingSystem.IsWindows() || window.Native?.Win32 is not { } win32) return;
        int on = 1;
        DwmSetWindowAttribute(win32.Hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }
}
