using System.Numerics;
using Chip.Editor.Features.Shell;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// Ctrl+P: type to fuzzy-search every registered command (label, menu, id). Up/Down pick, Enter runs, Esc closes.
/// Unavailable commands are listed greyed out. Commands run from here are remembered and listed first next time.
/// </summary>
public sealed partial class EditorApp
{
    const string PaletteId = "##commandPalette";
    string _paletteQuery = "";
    string? _paletteQueryFor;
    List<Command> _paletteResults = new();
    int _paletteIndex;
    bool _paletteFocus, _paletteScroll;

    void DrawCommandPalette()
    {
        if (_shell.PaletteRequested)
        {
            _shell.PaletteRequested = false;
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            {
                ImGui.OpenPopup(PaletteId);
                _paletteQuery = _snapshotPaletteQuery ?? "";
                _paletteQueryFor = null;
                _paletteFocus = true;
            }
        }
        var vp = ImGui.GetMainViewport();
        float width = MathF.Min(Theme.Px(600), vp.WorkSize.X - Theme.Px(80));
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f, vp.WorkPos.Y + Theme.Px(72)), ImGuiCond.Always, new Vector2(0.5f, 0));
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(8, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(8, 6));
        Command? run = null;
        if (ImGui.BeginPopup(PaletteId, ImGuiWindowFlags.NoMove))
        {
            bool justOpened = _paletteFocus;
            if (_paletteFocus) { ImGui.SetKeyboardFocusHere(); _paletteFocus = false; }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##query", "Search commands", ref _paletteQuery, 128);
            ImGui.PopStyleVar(); // frame padding back to normal for the list
            RefreshPaletteResults();

            int count = _paletteResults.Count;
            if (count > 0)
            {
                int step = (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? 1 : 0)
                    + (ImGui.IsKeyPressed(ImGuiKey.PageDown) ? 8 : 0) - (ImGui.IsKeyPressed(ImGuiKey.PageUp) ? 8 : 0);
                if (step != 0) { _paletteIndex = Math.Clamp(_paletteIndex + step, 0, count - 1); _paletteScroll = true; }
                if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) run = _paletteResults[_paletteIndex];
            }

            ImGui.Dummy(Theme.Px(0, 2));
            float rowH = ImGui.GetTextLineHeight() + Theme.Px(10);
            float listH = rowH * Math.Clamp(count, 1, 12);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero); // rows butt together so the list shows whole rows
            ImGui.BeginChild("##results", new Vector2(0, listH), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
            if (count == 0) ImGui.TextColored(Theme.TextDim, Esc($"No commands match \"{_paletteQuery.Trim()}\""));
            for (int i = 0; i < count; i++)
                if (PaletteRow(_paletteResults[i], i, rowH)) run = _paletteResults[i];
            ImGui.EndChild();
            ImGui.PopStyleVar();

            bool toggle = !justOpened && Ctx.Commands.Find("shell.palette") is { } self && self.Hotkeys.Any(h => h.IsPressed());
            if (run != null || toggle || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        else ImGui.PopStyleVar();
        ImGui.PopStyleVar();

        if (run == null) return;
        if (!run.Enabled) { Ctx.Status($"{run.Label} isn't available right now"); return; }
        _settings.Current.AddRecentCommand(run.Id);
        SaveSettings();
        run.Execute();
    }

    void RefreshPaletteResults()
    {
        if (_paletteQueryFor == _paletteQuery) return;
        _paletteQueryFor = _paletteQuery;
        _paletteResults = PaletteSearch.Rank(_paletteQuery, Ctx.Commands.All.Where(c => c.Id != "shell.palette"), _settings.Current.RecentCommands);
        _paletteIndex = 0;
        _paletteScroll = true;
    }

    /// <summary>Label, then its menu (dim), hotkey right-aligned. The highlighted row is the one Enter runs.</summary>
    bool PaletteRow(Command c, int index, float rowH)
    {
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        bool clicked = ImGui.InvisibleButton($"##row{index}", new Vector2(w, rowH));
        bool hovered = ImGui.IsItemHovered();
        if (hovered && ImGui.GetIO().MouseDelta != Vector2.Zero) _paletteIndex = index;
        bool selected = index == _paletteIndex;
        if (selected && _paletteScroll) { ImGui.SetScrollHereY(); _paletteScroll = false; }

        var dl = ImGui.GetWindowDrawList();
        uint U32(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
        if (selected) Widgets.SelectedRow(dl, p, p + new Vector2(w, rowH), Theme.Px(3));
        else if (hovered) dl.AddRectFilled(p, p + new Vector2(w, rowH), U32(Theme.Frame), Theme.Px(3));

        bool enabled = c.Enabled;
        float ty = MathF.Round(p.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f);
        float x = p.X + Theme.Px(10);
        string label = c.CurrentLabel;
        dl.AddText(new Vector2(x, ty), selected ? Theme.OnAccentU32 : U32(enabled ? Theme.Text : Theme.TextDisabled), label);
        x += ImGui.CalcTextSize(label).X + Theme.Px(10);
        string where = c.Menu ?? c.Category ?? "";
        if (where.Length > 0) dl.AddText(new Vector2(x, ty), selected ? Theme.OnAccentU32 & 0xB0FFFFFF : U32(enabled ? Theme.TextDim : Theme.TextDisabled), where);

        if (c.Hotkeys.Length > 0)
        {
            string keys = string.Join(" / ", c.Hotkeys.Select(h => h.ToString()));
            float kw = ImGui.CalcTextSize(keys).X;
            dl.AddText(new Vector2(p.X + w - kw - Theme.Px(10), ty), selected ? Theme.OnAccentU32 & 0xB0FFFFFF : U32(enabled ? Theme.TextDim : Theme.TextDisabled), keys);
        }
        if (!enabled && hovered) ImGui.SetTooltip("Not available right now");
        return clicked;
    }
}
