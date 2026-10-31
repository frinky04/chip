using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

/// <summary>
/// Krita-style dark orange: neutral near-black chrome, slightly lighter panels, and one solid orange for whatever is
/// active or selected (tool, mode, rows, primary buttons, value sliders), with dark text on it. Settings that are on
/// use a neutral raised fill with an orange icon. Separation comes from tone and dark hairlines, not light borders.
/// </summary>
public static class Theme
{
    public static readonly Vector4 Accent = Rgb(0xFF8A1F);
    public static readonly Vector4 AccentHover = Rgb(0xFFA04A);
    public static readonly Vector4 AccentDim = Rgb(0xB35E12);
    /// <summary>Text and icons drawn on an orange fill.</summary>
    public static readonly Vector4 OnAccent = Rgb(0x1A1A1A);
    public static readonly Vector4 Hover = Rgb(0xFFD166);
    public static readonly Vector4 Text = Rgb(0xE0E0E0);
    public static readonly Vector4 TextDim = Rgb(0x9C9C9C);      // secondary, ~6:1 on panels
    public static readonly Vector4 TextDisabled = Rgb(0x5E5E5E);
    /// <summary>Chrome: menu bar, tool strip, viewport header, status bar, dock title bars, tracks.</summary>
    public static readonly Vector4 Bg = Rgb(0x1E1E1E);
    /// <summary>Recessed tracks (segmented controls, field tags): darker than the chrome they sit on.</summary>
    public static readonly Vector4 Track = Rgb(0x141414);
    /// <summary>Dock bodies: the inspector, popups, floating panels.</summary>
    public static readonly Vector4 Panel = Rgb(0x262626);
    public static readonly Vector4 Frame = Rgb(0x313131);
    public static readonly Vector4 FrameHover = Rgb(0x3B3B3B);
    public static readonly Vector4 FrameActive = Rgb(0x454545);
    /// <summary>Hairlines are darker than what they separate.</summary>
    public static readonly Vector4 Border = Rgb(0x141414);
    public static readonly Vector4 ViewportBg = Rgb(0x2E2E2E);
    /// <summary>A setting that is on (snap, backfaces): neutral, the icon carries the orange.</summary>
    public static readonly Vector4 Raised = Rgb(0x404040);
    public static readonly Vector4 Error = Rgb(0xFF6B5E);
    /// <summary>Marks prefabs (their icon in the Place tab, the outliner, the prefab bar), as entity classes have colours.</summary>
    public static readonly Vector4 Prefab = Rgb(0xA98BEB);

    /// <summary>Monitor display scale; every hand-placed pixel size goes through <see cref="Px"/>.</summary>
    public static float Scale { get; private set; } = 1;
    public static float Px(float logical) => MathF.Round(logical * Scale);
    public static Vector2 Px(float x, float y) => new(Px(x), Px(y));

    public static uint AccentU32 => ImGui.ColorConvertFloat4ToU32(Accent);
    public static uint OnAccentU32 => ImGui.ColorConvertFloat4ToU32(OnAccent);
    public static uint HoverU32 => ImGui.ColorConvertFloat4ToU32(Hover);
    public static uint PrefabU32 => ImGui.ColorConvertFloat4ToU32(Prefab);
    public static readonly uint[] AxisU32 =
    [
        ImGui.ColorConvertFloat4ToU32(Rgb(0xE5484D)),
        ImGui.ColorConvertFloat4ToU32(Rgb(0x5BB35B)),
        ImGui.ColorConvertFloat4ToU32(Rgb(0x3E8EED)),
    ];

    public static Vector4 Rgb(uint hex, float a = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    /// <summary>Applies the theme at a display scale. Safe to call again (DPI change): it starts from ImGui's default style.</summary>
    public static void Apply(float scale)
    {
        Scale = scale;
        unsafe
        {
            var fresh = ImGuiNative.ImGuiStyle_ImGuiStyle();
            *ImGui.GetStyle().NativePtr = *fresh;
            ImGuiNative.ImGuiStyle_destroy(fresh);
        }
        var s = ImGui.GetStyle();
        s.WindowRounding = 0;
        s.ChildRounding = 0;
        s.FrameRounding = 3;
        s.PopupRounding = 2;
        s.GrabRounding = 2;
        s.TabRounding = 2;
        s.ScrollbarRounding = 2;
        s.WindowBorderSize = 1;
        s.FrameBorderSize = 1;   // fields and buttons sit in a dark hairline, like Krita's
        s.PopupBorderSize = 1;
        s.WindowPadding = new Vector2(8, 7);
        s.FramePadding = new Vector2(6, 3);   // 21 px controls with the 14 px font
        s.ItemSpacing = new Vector2(6, 4);
        s.ItemInnerSpacing = new Vector2(4, 3);
        s.IndentSpacing = 12;
        s.ScrollbarSize = 8;
        s.GrabMinSize = 8;
        s.SeparatorTextBorderSize = 1;
        s.SeparatorTextPadding = new Vector2(0, 2);

        var c = s.Colors;
        c[(int)ImGuiCol.Text] = Text;
        c[(int)ImGuiCol.TextDisabled] = TextDisabled;
        c[(int)ImGuiCol.WindowBg] = Panel;
        c[(int)ImGuiCol.ChildBg] = Panel;
        c[(int)ImGuiCol.PopupBg] = Panel;
        c[(int)ImGuiCol.Border] = Border;
        c[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
        c[(int)ImGuiCol.FrameBg] = Frame;
        c[(int)ImGuiCol.FrameBgHovered] = FrameHover;
        c[(int)ImGuiCol.FrameBgActive] = FrameActive;
        c[(int)ImGuiCol.TitleBg] = Bg;
        c[(int)ImGuiCol.TitleBgActive] = Bg;
        c[(int)ImGuiCol.TitleBgCollapsed] = Bg;
        c[(int)ImGuiCol.MenuBarBg] = Bg;
        c[(int)ImGuiCol.ScrollbarBg] = Vector4.Zero;
        c[(int)ImGuiCol.ScrollbarGrab] = FrameHover;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = FrameActive;
        c[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = Accent;
        c[(int)ImGuiCol.SliderGrabActive] = AccentHover;
        c[(int)ImGuiCol.Button] = Frame;
        c[(int)ImGuiCol.ButtonHovered] = FrameHover;
        c[(int)ImGuiCol.ButtonActive] = FrameActive;
        c[(int)ImGuiCol.Header] = Frame;
        c[(int)ImGuiCol.HeaderHovered] = FrameHover;
        c[(int)ImGuiCol.HeaderActive] = FrameActive;
        c[(int)ImGuiCol.Separator] = Border;
        c[(int)ImGuiCol.SeparatorHovered] = AccentDim;
        c[(int)ImGuiCol.SeparatorActive] = Accent;
        c[(int)ImGuiCol.ResizeGrip] = Vector4.Zero;
        c[(int)ImGuiCol.ResizeGripHovered] = AccentDim;
        c[(int)ImGuiCol.ResizeGripActive] = Accent;
        c[(int)ImGuiCol.Tab] = Bg;
        c[(int)ImGuiCol.TabHovered] = FrameHover;
        c[(int)ImGuiCol.TabActive] = Panel;
        c[(int)ImGuiCol.TextSelectedBg] = Rgb(0xFF8A1F, 0.35f);
        c[(int)ImGuiCol.NavHighlight] = Accent;
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0x000000, 0.55f);
        s.ScaleAllSizes(scale);
    }
}
