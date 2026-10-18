using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

/// <summary>
/// The editor's small widget vocabulary. Every panel is built from these so states read the same everywhere:
/// tool = warm fill + orange edge bar; segmented option = raised pill + orange icon; setting on = neutral raised fill; hover = frame fill.
/// </summary>
public static class Widgets
{
    public static float LabelColumn => Theme.Px(64);

    static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    enum Kind { Tool, Segment, Toggle }

    /// <summary>Square icon button for the tool strip, with its hotkey in the corner.</summary>
    public static bool ToolButton(Icon icon, string key, string tip, bool active, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(tip, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        Background(dl, p, size, active, hovered, Kind.Tool);
        var center = p + size * 0.5f - (key.Length > 0 ? Theme.Px(2, 2) : Vector2.Zero);
        Icons.Draw(dl, icon, center, U32(active || hovered ? Theme.Text : Theme.TextDim));
        if (key.Length > 0) SmallText(dl, p + size - SmallSize(key) - Theme.Px(3, 2), key, Theme.TextDim with { W = 0.75f });
        TipFor(hovered, key.Length > 0 ? $"{tip}  ({key})" : tip);
        return clicked;
    }

    /// <summary>
    /// Mutually exclusive options in one recessed track; the active option is a raised pill with an orange icon.
    /// Returns the clicked index, or -1.
    /// </summary>
    public static int SegmentedControl(string id, (Icon Icon, string Label, string Tip)[] items, int active)
    {
        float inset = Theme.Px(2);
        float h = ImGui.GetFrameHeight();
        float total = items.Sum(i => LabeledWidth(i.Label)) + inset * 2;
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(total, h), U32(Theme.Bg), Theme.Px(4));

        int clicked = -1;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.SetCursorScreenPos(p + new Vector2(inset, inset));
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (LabeledButton($"{id}{i}", items[i].Icon, items[i].Label, items[i].Tip, i == active, Kind.Segment, h - inset * 2)) clicked = i;
        }
        ImGui.PopStyleVar();
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(total, h));
        return clicked;
    }

    /// <summary>On/off setting with icon + label; "on" is a neutral raised fill, never orange.</summary>
    public static bool Toggle(string id, Icon icon, string label, string tip, bool on) =>
        LabeledButton(id, icon, label, tip, on, Kind.Toggle);

    static float LabeledWidth(string label) =>
        Theme.Px(8) * 2 + Fonts.IconSize + (label.Length > 0 ? Theme.Px(6) + ImGui.CalcTextSize(label).X : 0);

    static bool LabeledButton(string id, Icon icon, string label, string tip, bool active, Kind kind, float height = 0)
    {
        float h = height > 0 ? height : ImGui.GetFrameHeight();
        float pad = Theme.Px(8), gap = Theme.Px(6);
        var size = new Vector2(LabeledWidth(label), h);
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        Background(dl, p, size, active, hovered, kind);
        var fg = U32(active || hovered ? Theme.Text : Theme.TextDim);
        var iconCol = active && kind == Kind.Segment ? Theme.AccentU32 : fg;
        Icons.Draw(dl, icon, new Vector2(p.X + pad + Fonts.IconSize * 0.5f, p.Y + h * 0.5f), iconCol);
        if (label.Length > 0)
            dl.AddText(new Vector2(p.X + pad + Fonts.IconSize + gap, MathF.Round(p.Y + (h - ImGui.GetTextLineHeight()) * 0.5f)), fg, label);
        TipFor(hovered, tip);
        return clicked;
    }

    static void Background(ImDrawListPtr dl, Vector2 p, Vector2 size, bool active, bool hovered, Kind kind)
    {
        float r = Theme.Px(3);
        var bg = kind switch
        {
            Kind.Tool => active ? Theme.ActiveFill : hovered ? Theme.Frame : Vector4.Zero,
            Kind.Segment => active ? Theme.Raised : hovered ? Theme.Frame : Vector4.Zero, // pill raised out of the track
            _ => active ? Theme.FrameHover : hovered ? Theme.Frame : Vector4.Zero,
        };
        if (bg.W > 0) dl.AddRectFilled(p, p + size, U32(bg), r);
        if (active && kind == Kind.Tool)
        {
            float t = Theme.Px(2), inset = Theme.Px(6);
            dl.AddRectFilled(p + new Vector2(0, inset), p + new Vector2(t, size.Y - inset), Theme.AccentU32);
        }
    }

    /// <summary>A regular button whose label is an icon glyph, at the row's frame height.</summary>
    public static bool IconButton(string id, Icon icon, string tip, float width)
    {
        bool clicked = ImGui.Button($"##{id}", new Vector2(width, ImGui.GetFrameHeight()));
        var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        Icons.Draw(ImGui.GetWindowDrawList(), icon, (a + b) * 0.5f, U32(Theme.Text));
        Tip(tip);
        return clicked;
    }

    /// <summary>
    /// A row of numeric fields, each with a small coloured tag (X/Y/Z, U/V). Commits on Enter and returns which
    /// field changed. Fields whose values differ across the selection show "mixed".
    /// </summary>
    public static bool TaggedFields(string id, string[] tags, uint[] colors, float[] values, bool[] mixed, string fmt, out int index, out float value)
    {
        index = -1;
        value = 0;
        int n = tags.Length;
        float tagW = ImGui.CalcTextSize("X").X + Theme.Px(8);
        float gap = Theme.Px(4);
        float w = (ImGui.GetContentRegionAvail().X - gap * (n - 1)) / n - tagW;
        float h = ImGui.GetFrameHeight();
        var dl = ImGui.GetWindowDrawList();
        for (int a = 0; a < n; a++)
        {
            if (a > 0) ImGui.SameLine(0, gap);
            var p = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(p, p + new Vector2(tagW, h), U32(Theme.Bg), Theme.Px(3), ImDrawFlags.RoundCornersLeft);
            var ts = ImGui.CalcTextSize(tags[a]);
            dl.AddText(p + new Vector2(MathF.Round((tagW - ts.X) * 0.5f), ImGui.GetStyle().FramePadding.Y), colors[a], tags[a]);
            ImGui.Dummy(new Vector2(tagW, h));
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(w);
            float c = values[a];
            if (ImGui.InputFloat($"{id}{a}", ref c, 0, 0, mixed[a] ? "mixed" : fmt, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                index = a;
                value = c;
            }
        }
        return index >= 0;
    }

    /// <summary>SemiBold section heading with an optional right-aligned detail; a hairline separates groups.</summary>
    public static void Heading(string title, bool first = false, string? trailing = null)
    {
        if (!first)
        {
            ImGui.Dummy(Theme.Px(0, 4));
            ImGui.Separator();
            ImGui.Dummy(Theme.Px(0, 2));
        }
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text(title);
        ImGui.PopFont();
        if (trailing != null)
        {
            float w = ImGui.CalcTextSize(trailing).X;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w);
            ImGui.TextColored(Theme.TextDim, trailing);
        }
    }

    /// <summary>Starts an inspector row: dim label in a fixed column; the next widget fills the rest.</summary>
    public static void RowLabel(string text, string? tip = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
        if (tip != null) Tip(tip);
        ImGui.SameLine(LabelColumn);
    }

    /// <summary>Key in body colour, action dimmed; pairs separated by space rather than punctuation.</summary>
    public static void KeyHints((string Key, string Action)[] hints)
    {
        for (int i = 0; i < hints.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Theme.Px(16));
            ImGui.Text(hints[i].Key);
            ImGui.SameLine(0, Theme.Px(5));
            ImGui.TextColored(Theme.TextDim, hints[i].Action);
        }
    }

    /// <summary>Vertical hairline between groups on a toolbar row.</summary>
    public static void VSeparator()
    {
        ImGui.SameLine(0, Theme.Px(8));
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddLine(p + new Vector2(0, Theme.Px(4)), p + new Vector2(0, h - Theme.Px(4)), U32(Theme.Border), Theme.Px(1));
        ImGui.Dummy(new Vector2(Theme.Px(1), h));
        ImGui.SameLine(0, Theme.Px(8));
    }

    public static void StripGap()
    {
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.Separator();
        ImGui.Dummy(Theme.Px(0, 2));
    }

    public static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }

    static void TipFor(bool hovered, string text)
    {
        if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(text);
    }

    static Vector2 SmallSize(string s) => ImGui.CalcTextSize(s) * (Fonts.SmallSize / Fonts.BodySize);

    static void SmallText(ImDrawListPtr dl, Vector2 pos, string s, Vector4 col) =>
        dl.AddText(Fonts.Small, Fonts.SmallSize, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), U32(col), s);
}
