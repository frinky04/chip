using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// F1: every hotkeyed command and mouse gesture, searchable, by category. A category list on the left (with counts)
/// filters; groups sit in two balanced columns; each row is the action on the left and its keys as keycaps on the right.
/// "Edit bindings" jumps to Preferences > Hotkeys.
/// </summary>
public sealed partial class EditorApp
{
    const string SheetTitle = "Keyboard shortcuts";
    static readonly string[] SheetOrder = ["Camera", "Select", "Tools", "Edit", "Mesh", "Texture", "View", "File", "Help"];

    string _sheetSearch = "";
    string? _sheetCategory; // null = all
    bool _sheetFocusSearch;

    /// <summary>Every hotkeyed command with a menu or category, plus registered references (mouse gestures), grouped.</summary>
    List<(string Category, List<(string Keys, string Action)> Rows)> ShortcutGroups()
    {
        var rows = new List<(string Category, string Keys, string Action)>();
        foreach (var c in Ctx.Commands.All)
        {
            string? category = c.Category ?? c.Menu;
            if (category == null || c.HotkeyText == null) continue;
            rows.Add((category, string.Join(" / ", c.Hotkeys.Select(h => h.ToString())), c.Label));
        }
        rows.AddRange(Ctx.Commands.References.Select(r => (r.Category, r.Keys, r.Action)));
        return rows.GroupBy(r => r.Category)
            .OrderBy(g => Array.IndexOf(SheetOrder, g.Key) is var i && i >= 0 ? i : SheetOrder.Length).ThenBy(g => g.Key)
            .Select(g => (g.Key, CollapseRuns(g.Select(r => (r.Keys, Capitalize(r.Action))).ToList())))
            .ToList();
    }

    static string Capitalize(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>
    /// Numbered runs read as one row: "Store camera 1" .. "Store camera 9" on Ctrl+Shift+1..9 becomes
    /// "Store camera 1–9" on Ctrl+Shift+1 – 9. Needs three or more consecutive numbers on matching keys.
    /// </summary>
    static List<(string Keys, string Action)> CollapseRuns(List<(string Keys, string Action)> rows)
    {
        static (string Prefix, int N)? Numbered(string s) =>
            s.Length >= 2 && char.IsDigit(s[^1]) && !char.IsDigit(s[^2]) ? (s[..^1], s[^1] - '0') : null;

        var result = new List<(string Keys, string Action)>();
        var used = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (used[i]) continue;
            if (Numbered(rows[i].Action) is not { } a || Numbered(rows[i].Keys) is not { } k || a.N != k.N)
            {
                result.Add(rows[i]);
                continue;
            }
            var run = new List<int> { i };
            for (int j = i + 1; j < rows.Count; j++)
                if (!used[j] && Numbered(rows[j].Action) is { } a2 && Numbered(rows[j].Keys) is { } k2
                    && a2.Prefix == a.Prefix && k2.Prefix == k.Prefix && a2.N == k2.N)
                    run.Add(j);
            var numbers = run.Select(r => Numbered(rows[r].Action)!.Value.N).Order().ToList();
            bool consecutive = numbers.Count >= 3 && numbers.Zip(numbers.Skip(1)).All(p => p.Second == p.First + 1);
            if (!consecutive) { result.Add(rows[i]); continue; }
            foreach (int r in run) used[r] = true;
            result.Add(($"{k.Prefix}{numbers[0]} – {numbers[^1]}", $"{a.Prefix}{numbers[0]}–{numbers[^1]}"));
        }
        return result;
    }

    /// <summary>Rows matching every word of the search, in the action, the keys or the category name.</summary>
    static List<(string Keys, string Action)> Filter(string category, List<(string Keys, string Action)> rows, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return rows;
        return rows.Where(r => words.All(w =>
            r.Action.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Keys.Contains(w, StringComparison.OrdinalIgnoreCase)
            || category.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    void DrawShortcutsSheet()
    {
        if (Ctx.ShowShortcuts)
        {
            ImGui.OpenPopup(SheetTitle);
            Ctx.ShowShortcuts = false;
            _sheetFocusSearch = true;
        }
        var vp = ImGui.GetMainViewport();
        var size = new Vector2(MathF.Min(Theme.Px(1180), vp.WorkSize.X - Theme.Px(80)), MathF.Min(Theme.Px(780), vp.WorkSize.Y - Theme.Px(80)));
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(size); // fixed, so searching doesn't make the window jump
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(16, 14));
        bool open = true;
        if (ImGui.BeginPopupModal(SheetTitle, ref open, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar))
        {
            var groups = ShortcutGroups()
                .Select(g => (g.Category, Rows: Filter(g.Category, g.Rows, _sheetSearch)))
                .ToList();
            int total = groups.Sum(g => g.Rows.Count);

            // Search across the top, "Edit bindings" on the right.
            const string edit = "Edit bindings";
            float editW = ImGui.CalcTextSize(edit).X + ImGui.GetStyle().FramePadding.X * 2;
            if (_sheetFocusSearch) { ImGui.SetKeyboardFocusHere(); _sheetFocusSearch = false; }
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - editW - Theme.Px(8));
            ImGui.InputTextWithHint("##sheetSearch", "Search actions or keys, e.g. bevel or ctrl+z", ref _sheetSearch, 64);
            ImGui.SameLine(0, Theme.Px(8));
            bool editBindings = ImGui.Button(edit);
            Widgets.Tip("Preferences > Hotkeys: rebind any command");
            ImGui.Dummy(Theme.Px(0, 4));

            // Categories on the left, groups on the right.
            float navW = Theme.Px(176);
            ImGui.BeginChild("##sheetNav", new Vector2(navW, 0));
            if (SheetNavRow("All", total, _sheetCategory == null)) _sheetCategory = null;
            foreach (var (category, rows) in groups)
                if (SheetNavRow(category, rows.Count, _sheetCategory == category)) _sheetCategory = _sheetCategory == category ? null : category;
            ImGui.EndChild();
            ImGui.SameLine(0, Theme.Px(12));

            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 10));
            ImGui.BeginChild("##sheetList", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
            var shown = groups.Where(g => g.Rows.Count > 0 && (_sheetCategory == null || g.Category == _sheetCategory)).ToList();
            if (shown.Count == 0)
            {
                ImGui.TextColored(Theme.TextDim, _sheetSearch.Length > 0 ? $"No shortcuts match \"{_sheetSearch}\"." : "Nothing in this category.");
            }
            else if (shown.Count == 1 || ImGui.GetContentRegionAvail().X < Theme.Px(640))
            {
                foreach (var (category, rows) in shown) SheetGroup(category, rows);
            }
            else
            {
                // Two columns, each group going to the shorter one, so neither runs long.
                var columns = new[] { new List<(string, List<(string, string)>)>(), new List<(string, List<(string, string)>)>() };
                var heights = new int[2];
                foreach (var g in shown)
                {
                    int c = heights[0] <= heights[1] ? 0 : 1;
                    columns[c].Add(g);
                    heights[c] += g.Rows.Count + 2;
                }
                if (ImGui.BeginTable("##sheetColumns", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableNextRow();
                    foreach (var column in columns)
                    {
                        ImGui.TableNextColumn();
                        foreach (var (category, rows) in column) SheetGroup(category, rows);
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();

            // F1 toggles the sheet, but not on the frame it opened: that's the same press that opened it.
            bool close = ImGui.IsKeyPressed(ImGuiKey.Escape) || (ImGui.IsKeyPressed(ImGuiKey.F1, false) && !ImGui.IsWindowAppearing());
            if (editBindings)
            {
                _prefSection = 2;
                _hotkeySearch = _sheetSearch;
                _shell.PreferencesRequested = true;
                close = true;
            }
            if (close) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    /// <summary>A category in the left list: selected = solid orange row; the count sits on the right.</summary>
    static bool SheetNavRow(string label, int count, bool current)
    {
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight() + Theme.Px(4));
        bool clicked = ImGui.InvisibleButton($"##nav{label}", size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (current) Widgets.SelectedRow(dl, p, p + size, Theme.Px(3));
        else if (hovered) dl.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(Theme.FrameHover), Theme.Px(3));
        float ty = MathF.Round(p.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        bool empty = count == 0 && !current;
        dl.AddText(new Vector2(p.X + Theme.Px(10), ty), current ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(empty ? Theme.TextDisabled : hovered ? Theme.Text : Theme.TextDim), label);
        string n = count.ToString();
        dl.AddText(new Vector2(p.X + size.X - ImGui.CalcTextSize(n).X - Theme.Px(10), ty),
            current ? Theme.OnAccentU32 & 0xB0FFFFFF : ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled), n);
        return clicked;
    }

    /// <summary>A category: title bar, then zebra rows of action (left) and keycaps (right).</summary>
    static void SheetGroup(string category, List<(string Keys, string Action)> rows)
    {
        var dl = ImGui.GetWindowDrawList();
        float w = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        float titleH = MathF.Round(ImGui.GetTextLineHeight() + Theme.Px(10));
        dl.AddRectFilled(p, p + new Vector2(w, titleH), ImGui.ColorConvertFloat4ToU32(Theme.Panel), Theme.Px(3), ImDrawFlags.RoundCornersTop);
        float ty = MathF.Round(p.Y + (titleH - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(Fonts.Strong, Fonts.BodySize, new Vector2(p.X + Theme.Px(10), ty), ImGui.ColorConvertFloat4ToU32(Theme.Text), category);
        string n = rows.Count.ToString();
        dl.AddText(new Vector2(p.X + w - ImGui.CalcTextSize(n).X - Theme.Px(10), ty), ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled), n);
        ImGui.Dummy(new Vector2(w, titleH));

        float rowH = ImGui.GetFrameHeight() + Theme.Px(2);
        for (int i = 0; i < rows.Count; i++)
        {
            var (keys, action) = rows[i];
            var r = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(w, rowH));
            if (!ImGui.IsRectVisible(r, r + new Vector2(w, rowH))) continue;
            uint zebra = ImGui.ColorConvertFloat4ToU32(i % 2 == 0 ? Theme.Panel with { W = 0.45f } : Theme.Panel with { W = 0.2f });
            dl.AddRectFilled(r, r + new Vector2(w, rowH), zebra, i == rows.Count - 1 ? Theme.Px(3) : 0, ImDrawFlags.RoundCornersBottom);

            float keysW = Keycaps(dl, keys, Vector2.Zero, rowH, measure: true);
            float right = r.X + w - Theme.Px(8);
            Keycaps(dl, keys, new Vector2(right - keysW, r.Y), rowH, measure: false);
            float room = w - keysW - Theme.Px(30);
            float textY = MathF.Round(r.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(new Vector2(r.X + Theme.Px(10), textY), ImGui.ColorConvertFloat4ToU32(Theme.Text), Ellipsize(action, room));
            if (ImGui.IsMouseHoveringRect(r, r + new Vector2(w, rowH)) && ImGui.CalcTextSize(action).X > room)
                ImGui.SetTooltip($"{action}  ({keys})");
        }
        ImGui.Dummy(Theme.Px(0, 12));
    }

    static string Ellipsize(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string s = text[..n].TrimEnd() + "…";
            if (ImGui.CalcTextSize(s).X <= width) return s;
        }
        return "…";
    }

    /// <summary>
    /// Draws (or with <paramref name="measure"/>, just sizes) a binding as keycaps: "Ctrl+Shift+Z" is three caps joined by
    /// "+", alternatives ("Ctrl+Y / Ctrl+Shift+Z") are separated by "or", and words like "drag" or "(2D views)" are plain
    /// dim text. Returns the total width.
    /// </summary>
    static float Keycaps(ImDrawListPtr dl, string keys, Vector2 pos, float rowH, bool measure)
    {
        float x = pos.X, pad = Theme.Px(6), gap = Theme.Px(4);
        float capH = MathF.Round(ImGui.GetTextLineHeight() + Theme.Px(4));
        float capY = MathF.Round(pos.Y + (rowH - capH) * 0.5f);
        float textY = MathF.Round(pos.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim), faint = ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled);

        void Plain(string s, uint col)
        {
            if (!measure) dl.AddText(new Vector2(x, textY), col, s);
            x += ImGui.CalcTextSize(s).X;
        }
        void Cap(string s)
        {
            float tw = ImGui.CalcTextSize(s).X, cw = tw + pad * 2;
            if (!measure)
            {
                var a = new Vector2(x, capY);
                dl.AddRectFilled(a + new Vector2(0, Theme.Px(1)), a + new Vector2(cw, capH + Theme.Px(1)), ImGui.ColorConvertFloat4ToU32(Theme.Track), Theme.Px(3)); // key edge
                dl.AddRectFilled(a, a + new Vector2(cw, capH), ImGui.ColorConvertFloat4ToU32(Theme.Frame), Theme.Px(3));
                dl.AddRect(a, a + new Vector2(cw, capH), ImGui.ColorConvertFloat4ToU32(Theme.FrameActive), Theme.Px(3));
                dl.AddText(new Vector2(x + pad, textY), ImGui.ColorConvertFloat4ToU32(Theme.Text), s);
            }
            x += cw;
        }

        var alternatives = keys.Split(" / ", StringSplitOptions.TrimEntries);
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) { x += gap; Plain("or", faint); x += gap; }
            var parts = alternatives[a].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) { x += Theme.Px(2); Plain("+", faint); x += Theme.Px(2); }
                string part = parts[i];
                string? note = null;
                int paren = part.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) { note = part[(paren + 1)..]; part = part[..paren]; }
                // Lower-case words are gestures ("drag", "double-click"), not keys.
                if (part.Length > 0 && char.IsLower(part[0])) Plain(part, dim);
                else Cap(part);
                if (note != null) { x += gap; Plain(note, dim); }
            }
        }
        return x - pos.X;
    }
}
