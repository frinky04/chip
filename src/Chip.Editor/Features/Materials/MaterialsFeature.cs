using Chip.Core.Scene;
using Chip.Editor.Framework;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Materials and assets: library folders (<see cref="MaterialFolders"/>, and folders added this session), the Materials
/// tab of the dock, replace material, and applying per-material texel density.
/// </summary>
public sealed class MaterialsFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var lib = ctx.Materials;
        MaterialFolders.AddTo(lib, ctx.TextureFolders);
        var missing = MaterialFolders.Missing(lib);
        if (missing.Count > 0)
            ctx.Status($"Texture folder not found: {string.Join(", ", missing)}. Fix {MaterialLibrary.EnvVar}, then Texture > Rescan texture folders", error: true);

        var browser = MaterialBrowser.For(ctx);
        var replace = ReplaceMaterialDialog.For(ctx);
        ctx.AddDockPanel(browser);
        ctx.AddPanel(browser.Update);
        ctx.AddPanel(new SurfaceMaterials().Update);
        ctx.AddPanel(new ProjectTextures().Update);
        ctx.AddPanel(replace.Draw);

        var c = ctx.Commands;
        bool HasFaceTargets() => ctx.Sel.FaceSets(ctx.Doc).Count > 0;

        c.Add("materials.browser", "Materials", () => ctx.ToggleDock(MaterialBrowser.PanelId), "View", "T",
            group: "5-panels", isChecked: () => ctx.DockShowing(MaterialBrowser.PanelId));
        c.Add("materials.replace", "Replace material…", () => replace.Show(ctx), "Texture",
            canExecute: () => ctx.Doc.Meshes.Any(m => m.Faces.Count > 0), group: "1-material", order: 5);
        c.Add("materials.applyDensity", "Reset scale to material density", () => ctx.ForFaces("Apply Material Density", (mesh, faces) =>
        {
            foreach (var f in faces) lib.ApplyDensity(mesh.Faces[f]);
        }), "Texture", canExecute: HasFaceTargets, group: "2-align", order: 10);
        c.Add("materials.addFolder", "Add texture folder…", () => AddFolder(ctx), "Texture", group: "8-library", order: 0);
        c.Add("materials.rescan", "Rescan texture folders", () => Rescan(ctx), "Texture", group: "8-library", order: 1);
    }

    static void AddFolder(EditorContext ctx)
    {
        var result = Dialog.FolderPicker();
        if (!result.IsOk) return;
        if (!ctx.Materials.AddFolder(result.Path)) { ctx.Status($"{result.Path} is already in the library"); return; }
        Rescan(ctx);
    }

    /// <summary>After a rescan: new project textures keep the size they arrived at, whatever later happens to the image.</summary>
    public static void PinProjectSizes(EditorContext ctx)
    {
        if (!ctx.Materials.PinProjectSizes())
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {ctx.Materials.ProjectFolder}, so resizing its textures would change how they tile. Check the folder is writable.", error: true);
    }

    /// <summary>After a rescan: redraws what depends on the files.</summary>
    public static void Refreshed(EditorContext ctx)
    {
        MaterialBrowser.For(ctx).Thumbs.Refresh(ctx);
        ctx.Session.Touch(); // sizes may have changed: rebuild the scene's UVs
    }

    /// <summary>Re-reads every library folder and reports what changed.</summary>
    public static void Rescan(EditorContext ctx)
    {
        int before = ctx.Materials.All.Count;
        var r = ctx.Materials.Rescan();
        PinProjectSizes(ctx);
        Refreshed(ctx);
        var notes = new List<string>();
        if (r.Duplicates > 0) notes.Add(r.Duplicates == 1 ? "1 duplicate name skipped" : $"{r.Duplicates} duplicate names skipped");
        if (r.Unreadable > 0) notes.Add(r.Unreadable == 1 ? "1 unreadable image" : $"{r.Unreadable} unreadable images");
        if (r.MissingFolders > 0) notes.Add(r.MissingFolders == 1 ? "1 folder not found" : $"{r.MissingFolders} folders not found");
        if (r.MissingFolders > 0) notes[^1] += $" (check {MaterialLibrary.EnvVar})";
        int delta = r.Materials - before;
        string head = $"{r.Materials} materials" + (delta > 0 ? $" ({delta} new)" : delta < 0 ? $" ({-delta} gone)" : "");
        ctx.Status(notes.Count == 0 ? head : $"{head}; {string.Join(", ", notes)}", error: r.MissingFolders > 0);
    }
}
