using System.Diagnostics;
using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using ImGuiNET;
using NativeFileDialogSharp;

namespace Chip.Editor;

/// <summary>
/// Ctrl+Shift+P: General (scale, autosave, recent files), Viewport (grid, panels), Hotkeys (search, click to rebind)
/// and Paths (texture folders, where settings live). Every change is saved at once.
/// </summary>
public sealed partial class EditorApp
{
    const string PreferencesTitle = "Preferences";
    static readonly string[] PrefSections = ["General", "Viewport", "Hotkeys", "Paths"];

    int _prefSection;
    string _hotkeySearch = "";
    string? _captureId;
    string? _hotkeyMessage;
    bool _hotkeyMessageIsError;

    void DrawPreferences()
    {
        if (_shell.PreferencesRequested)
        {
            _shell.PreferencesRequested = false;
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId))
            {
                ImGui.OpenPopup(PreferencesTitle);
                _captureId = null;
                _hotkeyMessage = null;
                _snapshotAfterPrefsOpen?.Invoke();
            }
        }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(Theme.Px(900), vp.WorkSize.X - Theme.Px(60)), MathF.Min(Theme.Px(620), vp.WorkSize.Y - Theme.Px(60))), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 12));
        bool open = true;
        if (ImGui.BeginPopupModal(PreferencesTitle, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.BeginChild("##nav", new Vector2(Theme.Px(150), 0));
            for (int i = 0; i < PrefSections.Length; i++)
                if (NavItem(PrefSections[i], i == _prefSection)) { _prefSection = i; _captureId = null; }
            ImGui.EndChild();
            ImGui.SameLine(0, Theme.Px(12));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(16, 4));
            ImGui.BeginChild("##page", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
            ImGui.PopStyleVar();
            switch (_prefSection)
            {
                case 0: DrawGeneralPrefs(); break;
                case 1: DrawViewportPrefs(); break;
                case 2: DrawHotkeyPrefs(); break;
                default: DrawPathPrefs(); break;
            }
            ImGui.EndChild();

            if (_captureId != null) CaptureHotkey();
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.GetIO().WantTextInput) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
        if (!open) _captureId = null;
    }

    /// <summary>Section list item: neutral raised fill when current (navigation, not editing, so not orange).</summary>
    static bool NavItem(string label, bool current)
    {
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight() + Theme.Px(6));
        bool clicked = ImGui.InvisibleButton(label, size);
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (current || hovered) dl.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(current ? Theme.Raised : Theme.Frame), Theme.Px(3));
        float ty = MathF.Round(p.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(new Vector2(p.X + Theme.Px(10), ty), ImGui.ColorConvertFloat4ToU32(current || hovered ? Theme.Text : Theme.TextDim), label);
        return clicked;
    }

    static float PrefLabelColumn => Theme.Px(170);

    static void PrefRow(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, label);
        ImGui.SameLine(PrefLabelColumn);
    }

    static void Note(string text)
    {
        ImGui.SetCursorPosX(PrefLabelColumn);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X);
        ImGui.TextColored(Theme.TextDim, Esc(text));
        ImGui.PopTextWrapPos();
    }

    static void PageGap() => ImGui.Dummy(Theme.Px(0, 10));

    /// <summary>ImGui.NET passes Text/TextColored strings as printf formats, so a '%' in user data must be doubled.</summary>
    static string Esc(string text) => text.Replace("%", "%%");

    // ---- General ----

    void DrawGeneralPrefs()
    {
        var s = _settings.Current;
        Widgets.Heading("Interface");
        PrefRow("Scale");
        float detected = Dpi.ScaleFor(_window);
        string Pct(float v) => $"{v * 100:0}%";
        string current = s.UiScale is { } forced ? Pct(forced) : $"Match monitor ({Pct(detected)})";
        ImGui.SetNextItemWidth(Theme.Px(220));
        if (ImGui.BeginCombo("##scale", current))
        {
            if (ImGui.Selectable($"Match monitor ({Pct(detected)})", s.UiScale == null)) SetUiScale(null);
            foreach (var v in AppSettings.UiScaleChoices)
                if (ImGui.Selectable(Pct(v), s.UiScale == v)) SetUiScale(v);
            ImGui.EndCombo();
        }
        if (Dpi.Override != null) Note($"CHIP_UI_SCALE is set to {Pct(Dpi.Override.Value)}, which takes priority over this setting.");
        else Note("Applies right away. Chip also re-scales when you move the window to a monitor with a different scale.");

        PageGap();
        Widgets.Heading("Autosave");
        PrefRow("Save a backup");
        ImGui.SetNextItemWidth(Theme.Px(220));
        if (ImGui.BeginCombo("##autosave", AutosaveLabel(s.AutosaveSeconds)))
        {
            foreach (var v in AppSettings.AutosaveChoices)
                if (ImGui.Selectable(AutosaveLabel(v), s.AutosaveSeconds == v)) { s.AutosaveSeconds = v; SaveSettings(); }
            ImGui.EndCombo();
        }
        Note(s.AutosaveSeconds > 0
            ? "Unsaved changes are copied to the autosave folder so Chip can offer them back after a crash. Saving removes the copy."
            : "Autosave is off: a crash loses everything since your last save.");

        PageGap();
        Widgets.Heading("Recent files");
        PrefRow(s.RecentFiles.Count == 1 ? "1 file" : $"{s.RecentFiles.Count} files");
        ImGui.BeginDisabled(s.RecentFiles.Count == 0);
        if (ImGui.Button("Clear recent files")) Ctx.Commands.Execute("file.clearRecent");
        ImGui.EndDisabled();
    }

    static string AutosaveLabel(int seconds) => seconds switch
    {
        <= 0 => "Off",
        < 60 => $"Every {seconds} seconds",
        60 => "Every minute",
        _ when seconds % 60 == 0 => $"Every {seconds / 60} minutes",
        _ => $"Every {seconds} seconds",
    };

    void SetUiScale(float? scale)
    {
        _settings.Current.UiScale = scale;
        SaveSettings();
        _scaleCheckNow = true;
    }

    // ---- Viewport ----

    void DrawViewportPrefs()
    {
        var s = _settings.Current;
        Widgets.Heading("Grid");
        PrefRow("Starting grid size");
        ImGui.SetNextItemWidth(Theme.Px(220));
        if (ImGui.BeginCombo("##grid", Labels.Length(s.GridDefault)))
        {
            foreach (var g in EditorContext.GridSizes)
                if (ImGui.Selectable(Labels.Length(g), s.GridDefault == g)) { s.GridDefault = g; SaveSettings(); }
            ImGui.EndCombo();
        }
        Note("Used when Chip starts. Press [ and ] to change the grid while you work.");

        PageGap();
        Widgets.Heading("Panels");
        PrefRow("Tool strip");
        ImGui.Text($"{s.ToolStripWidth:0} px");
        PrefRow("Inspector");
        ImGui.Text($"{s.InspectorWidth:0} px");
        Note("Drag the edge of a panel to resize it. Double-click the edge to restore its default width.");
        ImGui.SetCursorPosX(PrefLabelColumn);
        if (ImGui.Button("Reset panel sizes")) ResetLayout();
    }

    // ---- Hotkeys ----

    void DrawHotkeyPrefs()
    {
        var commands = Ctx.Commands;
        ImGui.SetNextItemWidth(Theme.Px(320));
        ImGui.InputTextWithHint("##search", "Search commands or shortcuts", ref _hotkeySearch, 64);
        ImGui.SameLine();
        int changed = commands.All.Count(c => c.IsRebound);
        ImGui.BeginDisabled(changed == 0);
        if (ImGui.Button("Reset all")) ResetAllHotkeys();
        ImGui.EndDisabled();
        Widgets.Tip("Restore every shortcut to its default");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, changed == 0 ? "All defaults" : changed == 1 ? "1 changed" : $"{changed} changed");

        if (_hotkeyMessage != null) ImGui.TextColored(_hotkeyMessageIsError ? Theme.Error : Theme.TextDim, Esc(_hotkeyMessage));
        else ImGui.TextColored(Theme.TextDim, "Click a shortcut, then press the new keys. Esc cancels.");
        ImGui.Dummy(Theme.Px(0, 2));

        string q = _hotkeySearch.Trim();
        var rows = commands.All
            .Where(c => q.Length == 0 || Contains(c.Label, q) || Contains(c.Menu ?? c.Category ?? "", q) || Contains(c.Id, q)
                || c.Hotkeys.Any(h => Contains(h.ToString(), q)))
            .OrderBy(c => c.Menu == null ? CommandRegistry.MenuOrder.Length : Math.Max(0, Array.IndexOf(CommandRegistry.MenuOrder, c.Menu)))
            .ThenBy(c => c.Menu ?? c.Category ?? "").ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit;
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, Theme.Bg with { W = 0.35f });
        if (ImGui.BeginTable("##hotkeys", 4, flags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Menu", ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
            ImGui.TableSetupColumn("Shortcut", ImGuiTableColumnFlags.WidthFixed, Theme.Px(190));
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, Theme.Px(116));
            ImGui.TableHeadersRow();
            foreach (var c in rows) HotkeyRow(c);
            ImGui.EndTable();
        }
        ImGui.PopStyleColor();
        if (rows.Count == 0) ImGui.TextColored(Theme.TextDim, Esc($"No commands match \"{q}\""));
    }

    static bool Contains(string text, string q) => text.Contains(q, StringComparison.OrdinalIgnoreCase);

    void HotkeyRow(Command c)
    {
        ImGui.PushID(c.Id);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(c.Label);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(c.Description != null ? $"{c.Id}\n{c.Description}" : c.Id);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, c.Menu ?? c.Category ?? "");

        ImGui.TableNextColumn();
        bool capturing = _captureId == c.Id;
        string text = capturing ? "Press keys..." : c.Hotkeys.Length > 0 ? string.Join(" / ", c.Hotkeys.Select(h => h.ToString())) : "None";
        // Capturing is the thing being edited: orange outline. A rebound shortcut reads in body text, a default dimmer.
        if (capturing)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.ActiveFill);
            ImGui.PushStyleColor(ImGuiCol.Border, Theme.Accent);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Theme.Px(1));
        }
        ImGui.PushStyleColor(ImGuiCol.Text, capturing || c.IsRebound ? Theme.Text : c.Hotkeys.Length > 0 ? Theme.TextDim : Theme.TextDisabled);
        if (ImGui.Button(text, new Vector2(-1, 0)))
        {
            _captureId = capturing ? null : c.Id;
            _hotkeyMessage = null;
        }
        ImGui.PopStyleColor();
        if (capturing) { ImGui.PopStyleVar(); ImGui.PopStyleColor(2); }
        Widgets.Tip(capturing ? "Press the new shortcut, or Esc to cancel" : "Click to change");

        ImGui.TableNextColumn();
        ImGui.BeginDisabled(c.Hotkeys.Length == 0);
        if (ImGui.SmallButton("Clear")) SetHotkeys(c, [], $"{c.Label} has no shortcut now");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!c.IsRebound);
        if (ImGui.SmallButton("Reset"))
        {
            var error = Ctx.Commands.ResetHotkeys(c.Id);
            ReportHotkeyChange(error, $"{c.Label} is back to {(c.Hotkeys.Length > 0 ? c.HotkeyText : "no shortcut")}");
        }
        ImGui.EndDisabled();
        Widgets.Tip(c.DefaultHotkeys.Length > 0 ? $"Default: {string.Join(" / ", c.DefaultHotkeys.Select(h => h.ToString()))}" : "Default: none");
        ImGui.PopID();
    }

    /// <summary>Keys a shortcut may use: the keyboard, minus bare modifiers and Esc (which cancels capture).</summary>
    static readonly ImGuiKey[] BindableKeys = Enumerable.Range((int)ImGuiKey.Tab, ImGuiKey.GamepadStart - ImGuiKey.Tab)
        .Select(k => (ImGuiKey)k)
        .Where(k => k is not (ImGuiKey.Escape or ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl or ImGuiKey.LeftShift or ImGuiKey.RightShift
            or ImGuiKey.LeftAlt or ImGuiKey.RightAlt or ImGuiKey.LeftSuper or ImGuiKey.RightSuper or ImGuiKey.Menu))
        .ToArray();

    void CaptureHotkey()
    {
        if (Ctx.Commands.Find(_captureId!) is not { } c) { _captureId = null; return; }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { _captureId = null; return; }
        var io = ImGui.GetIO();
        foreach (var key in BindableKeys)
        {
            if (!ImGui.IsKeyPressed(key, false)) continue;
            var hotkey = new Hotkey(key, io.KeyCtrl, io.KeyShift, io.KeyAlt);
            _captureId = null;
            SetHotkeys(c, [hotkey], $"{c.Label} is now {hotkey}");
            return;
        }
    }

    void SetHotkeys(Command c, Hotkey[] keys, string success) => ReportHotkeyChange(Ctx.Commands.Rebind(c.Id, keys), success);

    void ReportHotkeyChange(string? error, string success)
    {
        _hotkeyMessage = error ?? success;
        _hotkeyMessageIsError = error != null;
        if (error == null) SaveHotkeyOverrides();
    }

    void ResetAllHotkeys()
    {
        // Two passes so defaults that were swapped between commands come back cleanly.
        var rebound = Ctx.Commands.All.Where(c => c.IsRebound).ToList();
        foreach (var c in rebound) Ctx.Commands.Rebind(c.Id, []);
        var failed = rebound.Where(c => Ctx.Commands.ResetHotkeys(c.Id) != null).Select(c => c.Label).ToList();
        ReportHotkeyChange(failed.Count > 0 ? $"Couldn't restore {string.Join(", ", failed)}: their default keys are taken." : null, "All shortcuts are back to their defaults");
        if (failed.Count > 0) SaveHotkeyOverrides();
    }

    // ---- Paths ----

    void DrawPathPrefs()
    {
        var s = _settings.Current;
        Widgets.Heading("Texture folders");
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X);
        ImGui.TextColored(Theme.TextDim, "Chip loads textures from its own assets folder plus these folders. Restart Chip after changing the list.");
        ImGui.PopTextWrapPos();
        ImGui.Dummy(Theme.Px(0, 2));
        string? remove = null;
        foreach (var folder in s.TextureFolders)
        {
            ImGui.PushID(folder);
            if (ImGui.SmallButton("Remove")) remove = folder;
            ImGui.SameLine();
            ImGui.TextColored(Directory.Exists(folder) ? Theme.Text : Theme.Error, Esc(folder));
            if (!Directory.Exists(folder)) Widgets.Tip("This folder doesn't exist any more");
            ImGui.PopID();
        }
        if (s.TextureFolders.Count == 0) ImGui.TextColored(Theme.TextDisabled, "No extra folders");
        if (remove != null) { s.TextureFolders.Remove(remove); TextureFoldersChanged(); }
        if (ImGui.Button("Add folder...")) AddTextureFolder();

        PageGap();
        Widgets.Heading("Where Chip keeps its files");
        FolderRow("Settings", _settings.Folder);
        FolderRow("Autosaves", _autosave.Folder);
    }

    void FolderRow(string label, string? folder)
    {
        PrefRow(label);
        if (folder == null) { ImGui.TextColored(Theme.TextDim, "Not saved (headless capture)"); return; }
        ImGui.PushID(label);
        if (ImGui.SmallButton("Open")) OpenFolder(folder);
        ImGui.PopID();
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, Esc(folder));
    }

    void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Ctx.Status($"Couldn't open {folder}: {e.Message}", error: true);
        }
    }

    void AddTextureFolder()
    {
        var result = Dialog.FolderPicker();
        if (!result.IsOk || string.IsNullOrEmpty(result.Path)) return;
        var full = Path.GetFullPath(result.Path);
        if (_settings.Current.TextureFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) { Ctx.Status($"{full} is already in the list"); return; }
        _settings.Current.TextureFolders.Add(full);
        TextureFoldersChanged();
    }

    void TextureFoldersChanged()
    {
        Ctx.TextureFolders = _settings.Current.TextureFolders.ToList();
        SaveSettings();
    }
}
