using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

/// <summary>
/// The editor's small widget vocabulary. Every panel is built from these so states read the same everywhere:
/// active tool / active option / selected row / primary button / value fill = solid orange with dark text;
/// setting on = neutral raised fill with an orange icon; hover = frame fill.
/// </summary>
public static class Widgets
{
    public static float LabelColumn => Theme.Px(64);

    static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    enum Kind { Tool, Segment, Toggle }

    /// <summary>Square icon button for the tool strip; <paramref name="key"/> shows in the tooltip.</summary>
    public static bool ToolButton(Icon icon, string key, string tip, bool active, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(tip, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        Background(dl, p, size, active, hovered, Kind.Tool);
        // Centred, bright line icons and no corner letters, like Krita's toolbox; the key lives in the tooltip.
        var center = new Vector2(MathF.Round(p.X + size.X * 0.5f), MathF.Round(p.Y + size.Y * 0.5f));
        Icons.Draw(dl, icon, center, U32(active ? Theme.OnAccent : hovered ? Theme.Text : Theme.Text with { W = 0.78f }));
        TipFor(hovered, key.Length > 0 ? $"{tip}  ({key})" : tip);
        return clicked;
    }

    /// <summary>
    /// Mutually exclusive options in one recessed track; the active option is a solid orange pill.
    /// Returns the clicked index, or -1.
    /// </summary>
    public static int SegmentedControl(string id, (Icon Icon, string Label, string Tip)[] items, int active)
    {
        // Roomy pills with a little air between them, so icon, label and neighbour don't crowd each other.
        float inset = Theme.Px(3), between = Theme.Px(2);
        float h = ImGui.GetFrameHeight();
        float total = items.Sum(i => LabeledWidth(i.Label, Kind.Segment)) + between * (items.Length - 1) + inset * 2;
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(total, h), U32(Theme.Track), Theme.Px(5));

        int clicked = -1;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(between, 0));
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

    /// <summary>Text-only <see cref="SegmentedControl"/>: a recessed track, the active option a solid orange pill. Returns the clicked index or -1.</summary>
    public static int TextSegments(string id, (string Label, string Tip)[] items, int active)
    {
        float inset = Theme.Px(2), pad = Theme.Px(8);
        float h = ImGui.GetFrameHeight();
        var widths = items.Select(i => ImGui.CalcTextSize(i.Label).X + pad * 2).ToArray();
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(widths.Sum() + inset * 2, h), U32(Theme.Track), Theme.Px(4));
        int clicked = -1;
        float x = p.X + inset;
        for (int i = 0; i < items.Length; i++)
        {
            var a = new Vector2(x, p.Y + inset);
            var size = new Vector2(widths[i], h - inset * 2);
            ImGui.SetCursorScreenPos(a);
            if (ImGui.InvisibleButton($"{id}{i}", size)) clicked = i;
            bool hovered = ImGui.IsItemHovered();
            Background(dl, a, size, i == active, hovered, Kind.Segment);
            var ts = ImGui.CalcTextSize(items[i].Label);
            dl.AddText(a + (size - ts) * 0.5f, i == active ? Theme.OnAccentU32 : U32(hovered ? Theme.Text : Theme.TextDim), items[i].Label);
            Tip(items[i].Tip);
            x += widths[i];
        }
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(widths.Sum() + inset * 2, h));
        return clicked;
    }

    /// <summary>The width <see cref="SegmentedControl"/> takes for these items, for toolbars that fit themselves to their room.</summary>
    public static float SegmentedWidth((Icon Icon, string Label, string Tip)[] items) =>
        items.Sum(i => LabeledWidth(i.Label, Kind.Segment)) + Theme.Px(2) * (items.Length - 1) + Theme.Px(3) * 2;

    /// <summary>The width <see cref="Toggle"/> takes with this label ("" for icon only).</summary>
    public static float ToggleWidth(string label) => LabeledWidth(label);

    /// <summary>On/off setting with icon + label; "on" is a neutral raised fill with an orange icon.</summary>
    public static bool Toggle(string id, Icon icon, string label, string tip, bool on) =>
        LabeledButton(id, icon, label, tip, on, Kind.Toggle);

    /// <summary>Segments are roomier than toggles: they're the header's main control.</summary>
    static (float Pad, float Gap) Spacing(Kind kind) => kind == Kind.Segment ? (Theme.Px(12), Theme.Px(8)) : (Theme.Px(8), Theme.Px(6));

    static float LabeledWidth(string label, Kind kind = Kind.Toggle)
    {
        var (pad, gap) = Spacing(kind);
        return pad * 2 + Fonts.IconSize + (label.Length > 0 ? gap + ImGui.CalcTextSize(label).X : 0);
    }

    static bool LabeledButton(string id, Icon icon, string label, string tip, bool active, Kind kind, float height = 0)
    {
        float h = height > 0 ? height : ImGui.GetFrameHeight();
        var (pad, gap) = Spacing(kind);
        var size = new Vector2(LabeledWidth(label, kind), h);
        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        Background(dl, p, size, active, hovered, kind);
        bool filled = active && kind == Kind.Segment;
        var fg = U32(filled ? Theme.OnAccent : active || hovered ? Theme.Text : Theme.TextDim);
        var iconCol = filled ? fg : active ? Theme.AccentU32 : fg;
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
            Kind.Tool or Kind.Segment => active ? (hovered ? Theme.AccentHover : Theme.Accent) : hovered ? Theme.FrameHover : Vector4.Zero,
            _ => active ? (hovered ? Theme.FrameActive : Theme.Raised) : hovered ? Theme.FrameHover : Vector4.Zero,
        };
        if (bg.W > 0) dl.AddRectFilled(p, p + size, U32(bg), r);
    }

    // ---- Accent fills: primary buttons, selected rows, value sliders ----

    /// <summary>Styles the next buttons as the primary action (solid orange, dark text). Pair with <see cref="PopPrimary"/>.</summary>
    public static void PushPrimary()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.AccentDim);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.OnAccent);
    }

    public static void PopPrimary() => ImGui.PopStyleColor(4);

    /// <summary>Fills a selected row (outliner, palette, lists) with the accent; draw its text in <see cref="RowText"/>.</summary>
    public static void SelectedRow(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding = 0) =>
        dl.AddRectFilled(min, max, Theme.AccentU32, rounding);

    /// <summary>Text colour for a row: dark on the accent fill, body otherwise.</summary>
    public static uint RowText(bool selected, bool dim = false) =>
        selected ? Theme.OnAccentU32 : U32(dim ? Theme.TextDim : Theme.Text);

    /// <summary>
    /// Krita-style value slider: the orange fill is the value, the label and value sit centred on it ("Segments: 3").
    /// Drag or click to set; Ctrl+click types a value. Text is dark over the fill and light beyond it.
    /// </summary>
    public static bool ValueSlider(string id, string label, ref int value, int min, int max)
    {
        float v = value;
        bool changed = SliderCore(id, label, ref v, min, max, "%.0f", x => $"{(int)MathF.Round(x)}");
        if (changed) value = (int)MathF.Round(v);
        return changed;
    }

    /// <summary>Float version. <paramref name="format"/> is printf-style for typing ("%.0f°"); <paramref name="show"/> formats the label.</summary>
    public static bool ValueSlider(string id, string label, ref float value, float min, float max, string format, Func<float, string> show)
    {
        float v = value;
        bool changed = SliderCore(id, label, ref v, min, max, format, show);
        if (changed) value = v;
        return changed;
    }

    static uint _typingSlider;

    static bool SliderCore(string id, string label, ref float value, float min, float max, string format, Func<float, string> show)
    {
        // ImGui's slider does the input (drag, click, Ctrl+click to type); its grab and text are hidden and painted
        // over, except while typing into it.
        uint uid = ImGui.GetID(id);
        bool typing = _typingSlider == uid;
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Vector4.Zero);
        if (!typing) ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        bool changed = ImGui.SliderFloat(id, ref value, min, max, format, ImGuiSliderFlags.AlwaysClamp);
        ImGui.PopStyleColor(typing ? 2 : 3);
        bool nowTyping = ImGui.IsItemActive() && ImGui.GetIO().WantTextInput;
        if (nowTyping) _typingSlider = uid;
        else if (typing) _typingSlider = 0;
        if (nowTyping) return changed;
        string shown = show(value);

        var a = ImGui.GetItemRectMin();
        var b = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        float split = MathF.Round(a.X + (b.X - a.X) * t);
        float r = ImGui.GetStyle().FrameRounding;
        if (split > a.X) dl.AddRectFilled(a, new Vector2(split, b.Y), U32(ImGui.IsItemHovered() || ImGui.IsItemActive() ? Theme.AccentHover : Theme.Accent),
            r, split >= b.X - r ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);

        string text = label.Length > 0 ? $"{label}: {shown}" : shown;
        var ts = ImGui.CalcTextSize(text);
        var pos = new Vector2(MathF.Round((a.X + b.X - ts.X) * 0.5f), MathF.Round((a.Y + b.Y - ts.Y) * 0.5f));
        dl.PushClipRect(a, new Vector2(split, b.Y), true);
        dl.AddText(pos, Theme.OnAccentU32, text);
        dl.PopClipRect();
        dl.PushClipRect(new Vector2(split, a.Y), b, true);
        dl.AddText(pos, U32(Theme.Text), text);
        dl.PopClipRect();
        return changed;
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
            dl.AddRectFilled(p, p + new Vector2(tagW, h), U32(Theme.Track), Theme.Px(3), ImDrawFlags.RoundCornersLeft);
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

    static readonly Dictionary<string, float> DensityMetres = new();
    static readonly int[] DensitySteps = [8, 16, 32, 64, 128, 256, 512, 1024, 2048];

    /// <summary>
    /// Scythe's texel density control: "[1] m is [64 ▾] px". Editing the metres keeps the pixels; picking pixels keeps
    /// the metres. The metres typed are remembered per <paramref name="id"/> so "2 m is 64 px" reads back as typed.
    /// Returns true with the new pixels-per-metre in <paramref name="result"/>. <paramref name="reserve"/> leaves room on the right.
    /// </summary>
    public static bool DensityField(string id, float pixelsPerMetre, out float result, float reserve = 0, bool dim = false)
    {
        result = pixelsPerMetre;
        float metres = DensityMetres.TryGetValue(id, out var m) ? m : 1f;
        float px = pixelsPerMetre * metres;
        bool changed = false;

        ImGui.SetNextItemWidth(Theme.Px(52));
        float edit = metres;
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        bool typed = ImGui.InputFloat($"{id}m", ref edit, 0, 0, "%g", ImGuiInputTextFlags.EnterReturnsTrue);
        if (dim) ImGui.PopStyleColor();
        if (typed && edit > 1e-3f && edit != metres)
        {
            DensityMetres[id] = edit;
            result = px / edit;
            changed = true;
        }
        Tip("Metres");
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "m is");
        ImGui.SameLine(0, Theme.Px(6));
        float pxLabel = ImGui.CalcTextSize("px").X + Theme.Px(6);
        ImGui.SetNextItemWidth(MathF.Max(ImGui.GetContentRegionAvail().X - pxLabel - reserve, Theme.Px(64)));
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        bool open = ImGui.BeginCombo($"{id}px", $"{px:0.##}");
        if (dim) ImGui.PopStyleColor();
        if (open)
        {
            foreach (int step in DensitySteps)
                if (ImGui.Selectable(step.ToString(), MathF.Abs(px - step) < 0.01f))
                {
                    result = step / metres;
                    changed = result != pixelsPerMetre;
                }
            ImGui.EndCombo();
        }
        Tip("Texture pixels across that distance");
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(Theme.TextDim, "px");
        return changed;
    }

    /// <summary>Space below a section's body before the next dock title (the host draws these, sections don't).</summary>
    public static void SectionDivider() => ImGui.Dummy(Theme.Px(0, 6));

    static bool _dockTitle;
    static string? _dockId;

    /// <summary>Ids of inspector sections folded to their title bar. The host points this at the persisted list.</summary>
    public static ICollection<string> Folded { get; set; } = new List<string>();
    /// <summary>Set when a title bar is clicked, so the host saves <see cref="Folded"/>; the host clears it.</summary>
    public static bool FoldChanged { get; set; }

    /// <summary>
    /// The inspector host brackets each section with these: the section's first <see cref="Heading"/> becomes a
    /// full-width dock title bar (Krita's docker headers) that folds the section; later headings stay plain group headings.
    /// </summary>
    public static void BeginDockSection(string id)
    {
        _dockTitle = true;
        _dockId = id;
    }

    public static void EndDockSection()
    {
        _dockTitle = false;
        _dockId = null;
    }

    /// <summary>
    /// SemiBold heading with an optional right-aligned detail; a dock title bar at the top of an inspector section.
    /// Returns false when that section is folded: the section returns without drawing its body.
    /// </summary>
    public static bool Heading(string title, string? trailing = null)
    {
        if (_dockTitle)
        {
            _dockTitle = false;
            return DockTitle(title, trailing, _dockId);
        }
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text(title);
        ImGui.PopFont();
        if (trailing != null)
        {
            float titleEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Px(12);
            string shown = Ellipsize(trailing, ImGui.GetWindowContentRegionMax().X - titleEnd);
            float w = ImGui.CalcTextSize(shown).X;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w);
            ImGui.TextColored(Theme.TextDim, shown);
            if (shown != trailing) Tip(trailing);
        }
        return true;
    }

    /// <summary>
    /// Chrome-coloured bar across the whole panel, dark hairline above, fold chevron and title left, detail right.
    /// Clicking it folds or unfolds section <paramref name="id"/>. Returns whether the section is open.
    /// </summary>
    static bool DockTitle(string title, string? trailing, string? id)
    {
        var dl = ImGui.GetWindowDrawList();
        var wp = ImGui.GetWindowPos();
        float x0 = wp.X, x1 = wp.X + ImGui.GetWindowWidth();
        var cur = ImGui.GetCursorScreenPos();
        float h = MathF.Round(ImGui.GetTextLineHeight() + Theme.Px(12));
        bool open = id == null || !Folded.Contains(id);
        bool hovered = false;
        if (id != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(x0, cur.Y));
            if (ImGui.InvisibleButton("##fold", new Vector2(x1 - x0, h)))
            {
                if (open) Folded.Add(id); else Folded.Remove(id);
                open = !open;
                FoldChanged = true;
            }
            hovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(cur);
        }
        dl.PushClipRect(new Vector2(x0, cur.Y), new Vector2(x1, cur.Y + h), false); // past the window padding
        dl.AddRectFilled(new Vector2(x0, cur.Y), new Vector2(x1, cur.Y + h), U32(hovered ? Theme.Panel : Theme.Bg));
        dl.AddLine(new Vector2(x0, cur.Y), new Vector2(x1, cur.Y), U32(Theme.Border), Theme.Px(1));
        float ty = MathF.Round(cur.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        if (id != null)
        {
            // A small fold arrow in the left padding, so titles still line up with the rows below them.
            var c = new Vector2(MathF.Round(x0 + (cur.X - x0) * 0.5f), MathF.Round(cur.Y + h * 0.5f));
            float s = Theme.Px(3.5f);
            uint col = U32(hovered ? Theme.Text : Theme.TextDisabled);
            if (open) dl.AddTriangleFilled(new Vector2(c.X - s, c.Y - s * 0.5f), new Vector2(c.X + s, c.Y - s * 0.5f), new Vector2(c.X, c.Y + s * 0.7f), col);
            else dl.AddTriangleFilled(new Vector2(c.X - s * 0.5f, c.Y - s), new Vector2(c.X + s * 0.7f, c.Y), new Vector2(c.X - s * 0.5f, c.Y + s), col);
        }
        dl.AddText(Fonts.Strong, Fonts.BodySize, new Vector2(cur.X, ty), U32(Theme.Text), title);
        if (trailing != null)
        {
            // The detail gives way to the title: cut to the room left, the whole of it in a tooltip.
            float right = wp.X + ImGui.GetWindowContentRegionMax().X;
            float titleEnd = cur.X + ImGui.CalcTextSize(title).X + Theme.Px(12);
            string shown = Ellipsize(trailing, right - titleEnd);
            float w = ImGui.CalcTextSize(shown).X;
            dl.AddText(new Vector2(MathF.Round(right - w), ty), U32(Theme.TextDim), shown);
            if (shown != trailing && ImGui.IsMouseHoveringRect(new Vector2(right - w, cur.Y), new Vector2(right, cur.Y + h))) ShowTip(trailing);
        }
        dl.PopClipRect();
        ImGui.Dummy(new Vector2(0, h));
        if (open) ImGui.Dummy(Theme.Px(0, 2));
        return open;
    }

    /// <summary>Cuts text with an ellipsis to fit <paramref name="width"/> (nothing at all when not even "…" fits).</summary>
    public static string Ellipsize(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            var t = text[..n] + "…";
            if (ImGui.CalcTextSize(t).X <= width) return t;
        }
        return ImGui.CalcTextSize("…").X <= width ? "…" : "";
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

    /// <summary>Gap between tool groups: a short centred hairline, not a full-width rule.</summary>
    public static void StripGap()
    {
        float gap = Theme.Px(10);
        var p = ImGui.GetCursorScreenPos();
        float x0 = ImGui.GetWindowPos().X, w = ImGui.GetWindowWidth(), inset = Theme.Px(12);
        float y = MathF.Round(p.Y + gap * 0.5f);
        ImGui.GetWindowDrawList().AddLine(new Vector2(x0 + inset, y), new Vector2(x0 + w - inset, y), U32(Theme.FrameHover), Theme.Px(1));
        ImGui.Dummy(new Vector2(0, gap));
    }

    public static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled)) ShowTip(text);
    }

    static void TipFor(bool hovered, string text)
    {
        if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ShowTip(text);
    }

    /// <summary>Tooltips wrap at a readable width (engine docs can be paragraphs) and show text as is, so "50%" survives.</summary>
    static void ShowTip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(Theme.Px(380));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
