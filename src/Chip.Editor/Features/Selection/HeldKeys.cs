using System.Runtime.InteropServices;
using ImGuiNET;

namespace Chip.Editor.Features.SelectionTools;

/// <summary>
/// Held-state queries ImGui may not see. The Silk.NET ImGui backend forwards only three mouse buttons, so the side buttons
/// (and CapsLock, as a held key) are also read from Win32 on Windows.
/// </summary>
static partial class HeldKeys
{
    const int VkXButton1 = 0x05, VkXButton2 = 0x06, VkCapital = 0x14;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);

    /// <summary>CapsLock, Mouse4 or Mouse5 held: double-click selects faces on the plane (Scythe).</summary>
    public static bool PlaneModifier()
    {
        if (ImGui.IsKeyDown(ImGuiKey.CapsLock) || ImGui.IsMouseDown((ImGuiMouseButton)3) || ImGui.IsMouseDown((ImGuiMouseButton)4)) return true;
        return OperatingSystem.IsWindows() && (Down(VkCapital) || Down(VkXButton1) || Down(VkXButton2));
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
