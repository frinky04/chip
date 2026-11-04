using Chip.Editor.Framework;

namespace Chip.Editor.Features.Hotspots;

/// <summary>
/// Hotspot texturing (SPEC §9): Apply Hotspot (Shift+H, coplanar regions), Hotspot Individual Faces (Shift+F), the
/// hotspot editor (Ctrl+Shift+H) and the inspector's apply settings. Assets are <c>*.hotspot.json</c> files found in the
/// material library's folders; the bundled sample lives in <c>assets/hotspots</c>.
/// </summary>
public sealed class HotspotFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        // The bundled sample's folder is in the material library (MaterialFolders); the hotspot library follows that library.
        var state = HotspotState.For(ctx);
        var editor = HotspotEditor.For(ctx);
        ctx.AddPanel(editor.Draw);
        ctx.AddSection(new HotspotSection());

        bool HasFaces() => ctx.Sel.FaceSets(ctx.Doc).Count > 0;
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "hotspot.apply", Label = "Apply hotspot", Menu = "Texture", Group = "5h-hotspot", Order = 0,
            Hotkeys = [Hotkey.Parse("Shift+H")], CanExecute = HasFaces, Execute = () => state.Apply(individual: false),
            Description = "Fit each coplanar region of the selection into the best hotspot patch of the active material",
        });
        c.Add(new Command
        {
            Id = "hotspot.applyFaces", Label = "Hotspot individual faces", Menu = "Texture", Group = "5h-hotspot", Order = 1,
            Hotkeys = [Hotkey.Parse("Shift+F")], CanExecute = HasFaces, Execute = () => state.Apply(individual: true),
            Description = "Fit every selected face into its own hotspot patch",
        });
        c.Add(new Command
        {
            Id = "hotspot.editor", Label = "Hotspot editor", Menu = "View", Group = "5-panels", Order = 1,
            Hotkeys = [Hotkey.Parse("Ctrl+Shift+H")], Execute = () => editor.ToggleOpen(ctx), IsChecked = () => editor.Open,
            Description = "Draw and flag the patches of a hotspot texture",
        });
        c.Add("hotspot.disallowRandom", "Hotspot: disallow random", () => state.Settings.DisallowRandom = !state.Settings.DisallowRandom,
            "Texture", group: "5h-hotspot", order: 2, isChecked: () => state.Settings.DisallowRandom);
        c.Add("hotspot.dontApplyMaterial", "Hotspot: don't apply material", () => state.Settings.DontApplyMaterial = !state.Settings.DontApplyMaterial,
            "Texture", group: "5h-hotspot", order: 3, isChecked: () => state.Settings.DontApplyMaterial);
        HotspotEditor.RegisterReferences(c);
    }
}
