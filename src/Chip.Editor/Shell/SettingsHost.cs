using Chip.Editor.Settings;

namespace Chip.Editor;

/// <summary>Applies persisted settings at startup and keeps them up to date (recent files, hotkey overrides).</summary>
public sealed partial class EditorApp
{
    string? _lastDocPath;

    /// <summary>Runs after every feature registered, so hotkey overrides can target any command.</summary>
    void ApplySettings()
    {
        var s = _settings.Current;
        Ctx.Grid = s.GridDefault;
        Ctx.SnapSpace = Enum.Parse<Viewport.SnapSpace>(s.SnapSpace);
        Ctx.TextureFolders = s.TextureFolders.ToList();
        _shell.Settings = _settings;
        _shell.ResetLayout = ResetLayout;

        var errors = Ctx.Commands.ApplyHotkeyOverrides(s.Hotkeys);
        if (_settings.LoadError != null) Ctx.Status(_settings.LoadError, error: true);
        else if (errors.Count > 0)
            Ctx.Status(errors.Count == 1 ? errors[0] : $"{errors[0]} ({errors.Count - 1} more shortcut problems; see Preferences > Hotkeys.)", error: true);
    }

    /// <summary>Persists the registry's hotkey overrides, keeping saved entries for commands this build doesn't have.</summary>
    void SaveHotkeyOverrides()
    {
        var merged = _settings.Current.Hotkeys.Where(kv => Ctx.Commands.Find(kv.Key) == null).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (id, keys) in Ctx.Commands.HotkeyOverrides()) merged[id] = keys;
        _settings.Current.Hotkeys = merged;
        SaveSettings();
    }

    void SaveSettings()
    {
        if (_settings.Save() is { } error) Ctx.Status(error + " Check that the folder is writable.", error: true);
    }

    /// <summary>Whatever opened or saved a file (menu, recent list, Save As), the path lands in recent files.</summary>
    void TrackRecentFile()
    {
        var path = Ctx.Session.Path;
        if (path == _lastDocPath) return;
        _lastDocPath = path;
        if (path == null) return;
        _settings.Current.AddRecentFile(path);
        SaveSettings();
    }
}
