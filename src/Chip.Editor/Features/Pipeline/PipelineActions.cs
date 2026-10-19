using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Pipeline;

/// <summary>Export, import and watch-mode logic behind the pipeline commands, free of dialogs so tests can drive it.</summary>
public static class PipelineActions
{
    /// <summary>The document's export path, with a relative path taken from the <c>.chip</c> file's folder. Null when unset.</summary>
    public static string? ResolveExportPath(EditorContext ctx)
    {
        var path = ctx.Doc.ExportPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return path;
        var baseDir = ctx.Session.Path != null ? Path.GetDirectoryName(Path.GetFullPath(ctx.Session.Path)) : null;
        return Path.GetFullPath(baseDir != null ? Path.Combine(baseDir, path) : path);
    }

    /// <summary>What the export did beyond writing meshes: baked collision and tessellation, for the status bar.</summary>
    public static string Report(ExportResult r)
    {
        var parts = new List<string>();
        if (r.Collision is { } c)
            parts.Add($"Collision {c.InputTriangles:N0} → {c.OutputTriangles:N0} triangles" + (c.ConvexPieces > 0 ? $", {c.ConvexPieces} hulls" : ""));
        if (r.Tessellated is { } t) parts.Add($"render {t.Before:N0} → {t.After:N0} triangles");
        return parts.Count == 0 ? "" : $". {string.Join("; ", parts)}";
    }

    public static string Describe(int meshes, int entities)
    {
        string m = meshes == 1 ? "1 mesh" : $"{meshes} meshes";
        return entities == 0 ? m : $"{m} and {(entities == 1 ? "1 entity" : $"{entities} entities")}";
    }

    /// <summary>Watch mode: called after every successful save. Re-exports when the document asks for it.</summary>
    public static void ExportAfterSave(EditorContext ctx, string savedPath)
    {
        if (!ctx.Doc.Export.ExportOnSave) return;
        string saved = Path.GetFileName(savedPath);
        if (ResolveExportPath(ctx) is not { } glb)
        {
            ctx.Status($"Saved {saved}, but export on save has nowhere to write. Use File > Export GLB as… once to choose a file.", error: true);
            return;
        }
        try
        {
            var r = GlbExporter.Export(ctx.ExportDocument(), ctx.Materials, glb);
            ctx.Status($"Saved {saved} and exported {Describe(r.Meshes, r.Entities)} to {Path.GetFileName(glb)}{Report(r)}");
        }
        catch (Exception e)
        {
            ctx.Status($"Saved {saved}, but the export to {Path.GetFileName(glb)} failed: {e.Message} Close it in other programs or pick another folder.", error: true);
        }
    }

    public static void ToggleExportOnSave(EditorContext ctx)
    {
        bool on = !ctx.Doc.Export.ExportOnSave;
        ctx.Session.Do(on ? "Turn On Export on Save" : "Turn Off Export on Save", () => ctx.Doc.Export.ExportOnSave = on);
        if (!on) ctx.Status("Export on save is off");
        else if (ResolveExportPath(ctx) is { } glb) ctx.Status($"Export on save is on: every save also writes {Path.GetFileName(glb)}");
        else ctx.Status("Export on save is on. Choose a file with File > Export GLB as… so saves have somewhere to write.");
    }

    public static bool ExportObj(EditorContext ctx, string path)
    {
        try
        {
            var r = ObjExporter.Export(ctx.ExportDocument(), ctx.Materials, path);
            ctx.Status($"Exported {Describe(r.Meshes, 0)} to {Path.GetFileName(path)} and {Path.GetFileName(Path.ChangeExtension(path, ".mtl"))}");
            return true;
        }
        catch (Exception e)
        {
            ctx.Status($"OBJ export failed: {e.Message} Choose a writable folder and try again.", error: true);
            return false;
        }
    }

    /// <summary>Reads a .glb, .gltf or .obj, adds its meshes and entities as one undo step, and selects the new meshes.</summary>
    public static ImportResult? Import(EditorContext ctx, string path)
    {
        string file = Path.GetFileName(path);
        ImportResult result;
        try
        {
            float scale = 1f / (ctx.Doc.ExportScale > 0 ? ctx.Doc.ExportScale : 1f);
            result = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".obj" => ObjImporter.Load(path, ctx.Materials, ctx.ActiveMaterial, scale),
                ".glb" or ".gltf" => GltfImporter.Load(path, ctx.Materials, ctx.ActiveMaterial, scale),
                var ext => throw new NotSupportedException($"{ext} files can't be imported; use .glb, .gltf or .obj."),
            };
        }
        catch (Exception e)
        {
            ctx.Status($"Couldn't import {file}: {e.Message}", error: true);
            return null;
        }
        if (result.Meshes.Count == 0 && result.Entities.Count == 0)
        {
            ctx.Status($"{file} has no meshes to import" + (result.Warnings.Count > 0 ? $" ({result.Warnings[0]})" : ""), error: true);
            return result;
        }

        ctx.CancelInteraction();
        ctx.SetMode(SelectMode.Object);
        ctx.Session.Do("Import", () =>
        {
            ctx.Sel.Clear();
            foreach (var m in result.Meshes) ctx.Sel.Add(ElementRef.Object(ctx.Doc.Add(m).Id));
            foreach (var e in result.Entities) ctx.Doc.Add(e);
        });
        if (result.Meshes.Count > 0) ctx.Focus();

        int faces = result.Meshes.Sum(m => m.Faces.Count);
        var missing = result.Meshes.SelectMany(m => m.Faces.Select(f => f.Material)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !ctx.Materials.Contains(n)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var notes = new List<string>(result.Warnings.Take(1));
        if (missing.Count > 0)
            notes.Add($"{(missing.Count == 1 ? "1 material isn't" : $"{missing.Count} materials aren't")} in the library ({string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? ", …" : "")}); use Texture > Add texture folder to bring the textures in");
        string head = $"Imported {Describe(result.Meshes.Count, result.Entities.Count)} ({faces} faces) from {file}";
        ctx.Status(notes.Count == 0 ? head : $"{head}. {string.Join(". ", notes)}");
        return result;
    }

    /// <summary>Sets one collision mode on every selected mesh as one undo step.</summary>
    public static void SetCollision(EditorContext ctx, CollisionMode mode)
    {
        var meshes = ctx.Sel.MeshIds().Select(ctx.Doc.Find).OfType<PolyMesh>().ToList();
        if (meshes.Count == 0) { ctx.Status("Select meshes to set their collision"); return; }
        ctx.Session.Do("Set Collision", () =>
        {
            foreach (var id in meshes.Select(m => m.Id)) ctx.Doc.Find(id)!.Collision = mode;
        });
    }
}
