using System.Runtime.CompilerServices;
using Chip.Core.Hotspots;
using Chip.Core.Scene;
using Chip.Editor.Features.Texturing;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Hotspots;

/// <summary>
/// Hotspot state of one editor: the assets found in the material library's folders (rescanned whenever the library
/// changes), the apply settings, and the asset open in the hotspot editor. Kept beside the context so headless test
/// contexts don't share it.
/// </summary>
public sealed class HotspotState
{
    static readonly ConditionalWeakTable<EditorContext, HotspotState> ByContext = new();

    public static HotspotState For(EditorContext ctx) => ByContext.GetValue(ctx, c => new HotspotState(c));

    readonly EditorContext _ctx;
    readonly HotspotLibrary _library = new();
    int _scannedVersion = -1;
    string _scannedFolders = "";

    HotspotState(EditorContext ctx) => _ctx = ctx;

    public HotspotSettings Settings { get; } = new();

    /// <summary>The asset open in the hotspot editor (a working copy; unsaved edits apply straight away).</summary>
    public HotspotAsset? Editing { get; set; }
    /// <summary>The editor has edits that are not saved to <see cref="HotspotAsset.Path"/>.</summary>
    public bool Dirty { get; set; }

    /// <summary>Assets on disk, rescanned when the material library's folders or contents change.</summary>
    public HotspotLibrary Library
    {
        get
        {
            var folders = string.Join("|", _ctx.Materials.Folders);
            if (_scannedVersion != _ctx.Materials.Version || folders != _scannedFolders) Rescan();
            return _library;
        }
    }

    public void Rescan()
    {
        _library.Scan(_ctx.Materials.Folders);
        _scannedVersion = _ctx.Materials.Version;
        _scannedFolders = string.Join("|", _ctx.Materials.Folders);
    }

    /// <summary>The asset for a material: the editor's working copy when it targets it, else the library's.</summary>
    public HotspotAsset? AssetFor(string? material)
    {
        if (material == null) return null;
        if (Editing != null && Editing.Targets(material)) return Editing;
        return Library.ForMaterial(material);
    }

    /// <summary>Saved assets plus the editor's unsaved one, each once (the working copy replaces its file's entry).</summary>
    public List<HotspotAsset> AllAssets()
    {
        var list = Library.Assets.ToList();
        if (Editing == null) return list;
        int i = list.FindIndex(a => a.Path != null && string.Equals(a.Path, Editing.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list[i] = Editing; else list.Add(Editing);
        return list;
    }

    /// <summary>
    /// The hotspot to apply to the selection and the material that goes with it: the active material's asset, else
    /// the asset of the active (or first) selected face's material. Null when neither has one.
    /// </summary>
    public (HotspotAsset Asset, string Material)? Resolve()
    {
        if (AssetFor(_ctx.ActiveMaterial) is { } a) return (a, _ctx.ActiveMaterial);
        var faces = TextureOps.Faces(_ctx);
        foreach (var f in faces)
            if (AssetFor(f.Face.Material) is { } b) return (b, f.Face.Material);
        return null;
    }

    /// <summary>
    /// Applies a hotspot to the selection as one undo step: coplanar regions (<paramref name="individual"/> false) or
    /// each face. With <paramref name="asset"/> and <paramref name="patch"/> (the editor's apply buttons) that patch is
    /// forced. Reports the result in the status bar.
    /// </summary>
    public void Apply(bool individual, HotspotAsset? asset = null, int? patch = null)
    {
        var ctx = _ctx;
        var sets = ctx.Sel.FaceSets(ctx.Doc);
        if (sets.Count == 0 || sets.All(s => s.Value.Count == 0))
        {
            ctx.Status("Select faces or objects to apply a hotspot");
            return;
        }
        string material;
        if (asset != null)
        {
            if (asset.Materials.Count == 0)
            {
                ctx.Status("This hotspot asset has no target materials. Add one in the hotspot editor (Add active material)", error: true);
                return;
            }
            material = asset.Targets(ctx.ActiveMaterial) ? ctx.ActiveMaterial : asset.Materials[0];
        }
        else if (Resolve() is { } r) (asset, material) = r;
        else
        {
            ctx.Status($"{ctx.ActiveMaterial} has no hotspot asset. Pick a hotspot material, or make an asset for it in the hotspot editor (Ctrl+Shift+H)", error: true);
            return;
        }

        var active = TextureOps.ActiveFace(ctx);
        var results = new List<HotspotRegionResult>();
        string label = individual ? "Hotspot Individual Faces" : "Apply Hotspot";
        ctx.Session.Begin(label);
        try
        {
            foreach (var (mesh, faces) in sets)
            {
                int reference = active is { } a && a.Mesh == mesh ? a.Index : -1;
                results.AddRange(HotspotApply.Apply(ctx.Materials, mesh, faces, asset, material, Settings, individual, reference, patch));
            }
        }
        catch { ctx.Session.Cancel(); throw; }
        int applied = results.Count(r => r.Applied);
        int faceCount = results.Where(r => r.Applied).Sum(r => r.Faces.Count);
        int skipped = results.Count - applied;
        if (applied > 0) ctx.Session.End();
        else
        {
            ctx.Session.Cancel(); // nothing changed: no empty undo step
            ctx.Status(Settings.Tiling == HotspotTiling.Only
                ? $"{asset.Name} has no tiling patches. Set Tiling to Allow, or mark full-width patches as tiling (Alt+T)"
                : $"{asset.Name} has no patches. Draw some in the hotspot editor (Ctrl+Shift+H)", error: true);
            return;
        }
        string what = individual ? TextureOps.Plural(faceCount) : $"{(applied == 1 ? "1 region" : $"{applied} regions")} ({TextureOps.Plural(faceCount)})";
        ctx.Status($"Applied {asset.Name} to {what}" + (skipped > 0 ? $"; {skipped} left as they were (no patch fits)" : ""));
    }
}
