using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

/// <summary>
/// Dark, flat, dense. Orange means one thing: what you are editing right now (selection, active tool/mode, focus).
/// Persistent settings use neutral checked states.
/// </summary>
public static class Theme
{
    public static readonly Vector4 Accent = Rgb(0xFF8A1F);
    public static readonly Vector4 AccentDim = Rgb(0xB35E12);
    public static readonly Vector4 Hover = Rgb(0xFFD166);
    public static readonly Vector4 Text = Rgb(0xD4D4D4);
    public static readonly Vector4 TextDim = Rgb(0xA0A0A8);      // secondary, ~6:1 on panels
    public static readonly Vector4 TextDisabled = Rgb(0x66666D);
    public static readonly Vector4 Bg = Rgb(0x1A1A1C);
    public static readonly Vector4 Panel = Rgb(0x212124);
    public static readonly Vector4 Frame = Rgb(0x2A2A2E);
    public static readonly Vector4 FrameHover = Rgb(0x34343A);
    public static readonly Vector4 Border = Rgb(0x313136);
    public static readonly Vector4 ViewportBg = Rgb(0x2B2C30);
    public static readonly Vector4 ActiveFill = Rgb(0x3A3029);  // active tool: warm-tinted, not orange
    public static readonly Vector4 Raised = Rgb(0x3B3B42);      // active option in a segmented track
    public static readonly Vector4 Error = Rgb(0xFF6B5E);

    /// <summary>Monitor display scale; every hand-placed pixel size goes through <see cref="Px"/>.</summary>
    public static float Scale { get; private set; } = 1;
    public static float Px(float logical) => MathF.Round(logical * Scale);
    public static Vector2 Px(float x, float y) => new(Px(x), Px(y));

    public static uint AccentU32 => ImGui.ColorConvertFloat4ToU32(Accent);
    public static uint HoverU32 => ImGui.ColorConvertFloat4ToU32(Hover);
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
        s.FrameBorderSize = 0;
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
        c[(int)ImGuiCol.PopupBg] = Rgb(0x1E1E21);
        c[(int)ImGuiCol.Border] = Border;
        c[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
        c[(int)ImGuiCol.FrameBg] = Frame;
        c[(int)ImGuiCol.FrameBgHovered] = FrameHover;
        c[(int)ImGuiCol.FrameBgActive] = Rgb(0x3A3A40);
        c[(int)ImGuiCol.TitleBg] = Bg;
        c[(int)ImGuiCol.TitleBgActive] = Bg;
        c[(int)ImGuiCol.TitleBgCollapsed] = Bg;
        c[(int)ImGuiCol.MenuBarBg] = Bg;
        c[(int)ImGuiCol.ScrollbarBg] = Panel;
        c[(int)ImGuiCol.ScrollbarGrab] = Frame;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = FrameHover;
        c[(int)ImGuiCol.ScrollbarGrabActive] = AccentDim;
        c[(int)ImGuiCol.CheckMark] = Text;
        c[(int)ImGuiCol.SliderGrab] = AccentDim;
        c[(int)ImGuiCol.SliderGrabActive] = Accent;
        c[(int)ImGuiCol.Button] = Frame;
        c[(int)ImGuiCol.ButtonHovered] = FrameHover;
        c[(int)ImGuiCol.ButtonActive] = AccentDim;
        c[(int)ImGuiCol.Header] = Frame;
        c[(int)ImGuiCol.HeaderHovered] = FrameHover;
        c[(int)ImGuiCol.HeaderActive] = AccentDim;
        c[(int)ImGuiCol.Separator] = Border;
        c[(int)ImGuiCol.SeparatorHovered] = AccentDim;
        c[(int)ImGuiCol.SeparatorActive] = Accent;
        c[(int)ImGuiCol.ResizeGrip] = Vector4.Zero;
        c[(int)ImGuiCol.ResizeGripHovered] = AccentDim;
        c[(int)ImGuiCol.ResizeGripActive] = Accent;
        c[(int)ImGuiCol.Tab] = Panel;
        c[(int)ImGuiCol.TabHovered] = FrameHover;
        c[(int)ImGuiCol.TabActive] = Frame;
        c[(int)ImGuiCol.TextSelectedBg] = Rgb(0xFF8A1F, 0.35f);
        c[(int)ImGuiCol.NavHighlight] = Accent;
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0x000000, 0.55f);
        s.ScaleAllSizes(scale);
    }
}
