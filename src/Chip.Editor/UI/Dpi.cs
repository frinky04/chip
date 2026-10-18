using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Chip.Editor.UI;

/// <summary>Display scale of the monitor the window opened on (1.0 = 96 DPI). ImGui draws in physical pixels, so the UI multiplies by this.</summary>
public static partial class Dpi
{
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>CHIP_UI_SCALE overrides detection (e.g. 1.25), for odd setups and headless captures.</summary>
    public static float? Override =>
        float.TryParse(Environment.GetEnvironmentVariable("CHIP_UI_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public static float ScaleFor(IWindow window)
    {
        if (Override is { } forced) return forced;
        if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32)
        {
            uint dpi = GetDpiForWindow(win32.Hwnd);
            if (dpi > 0) return Math.Clamp(dpi / 96f, 1f, 4f);
        }
        return 1f;
    }
}
