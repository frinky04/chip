using Chip.Core.IO;
using Chip.Editor.Framework;
using Chip.Editor.Settings;

namespace Chip.Editor.Features.Shell;

/// <summary>
/// Application-shell commands: command palette, preferences, panel layout and recent files. The windows themselves are
/// drawn by the host (<c>Shell/</c>), which finds this feature after loading and reads its requests each frame.
/// </summary>
public sealed class ShellFeature : IEditorFeature
{
    EditorContext _ctx = null!;

    /// <summary>Settings this feature reads and updates (recent files). The host swaps in the persisted store.</summary>
    public SettingsStore Settings { get; set; } = SettingsStore.InMemory();

    public bool PaletteRequested { get; set; }
    public bool PreferencesRequested { get; set; }
    /// <summary>Set by the host: restores default panel sizes.</summary>
    public Action ResetLayout { get; set; } = () => { };

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        c.Add("shell.palette", "Command palette", () => PaletteRequested = true, "View", "Ctrl+P", group: "8-window", order: 0);
        c.Add("shell.toggleDock", "Side panel", () => ctx.DockOpen = !ctx.DockOpen, "View", "Ctrl+\\", group: "5-panels", order: -1,
            isChecked: () => ctx.DockOpen);
        c.Add("shell.resetLayout", "Reset panel sizes", () => ResetLayout(), "View", group: "8-window", order: 1);
        c.Add("shell.preferences", "Preferences...", () => PreferencesRequested = true, "Edit", "Ctrl+Shift+P", group: "9-preferences");
        c.Add("file.clearRecent", "Clear recent files", ClearRecent, canExecute: () => Settings.Current.RecentFiles.Count > 0);
    }

    public IReadOnlyList<string> RecentFiles => Settings.Current.RecentFiles;

    /// <summary>Opens a recent file after the unsaved-changes check. A file that has gone is dropped from the list.</summary>
    public void OpenRecent(string path) => _ctx.ConfirmDiscard(() =>
    {
        string name = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            Settings.Current.RecentFiles.Remove(path);
            Settings.Save();
            _ctx.Status($"Couldn't open {name}: the file was moved or deleted. It has been removed from recent files.", error: true);
            return;
        }
        try
        {
            _ctx.CancelInteraction();
            _ctx.Session.Reset(DocumentSerializer.Load(path), path);
            _ctx.FrameAll();
            _ctx.Status($"Opened {name}");
        }
        catch (Exception e) { _ctx.Status($"Couldn't open {name}: {e.Message}", error: true); }
    });

    void ClearRecent()
    {
        Settings.Current.RecentFiles.Clear();
        Settings.Save();
        _ctx.Status("Cleared recent files");
    }
}
