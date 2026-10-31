using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Places linked copies of a prefab file (picked in the Place tab): its origin goes on the surface or grid point under
/// the mouse, snapped, turned by the yaw. The level's definition for the prefab is reused, or read from the file on the
/// first placement. The tool stays armed for the next copy; Esc puts it down.
/// </summary>
public sealed class PrefabTool : IViewportTool
{
    readonly ConstructionPlane _plane = new();
    Vector3? _cursor;
    /// <summary>The prefab's parts as last read, for the preview (keyed by file and its write time).</summary>
    (string File, DateTime Written, IReadOnlyList<DefinitionPart> Parts)? _preview;

    public string Id => "prefab";
    public string Name => "Prefab";
    public Icon Icon => Icon.Link;
    public string? Hotkey => null;
    public int Order => 16;
    public bool InToolStrip => false; // armed by picking a prefab in the Place tab
    public bool Busy => false;
    public bool HasOptions => true;

    /// <summary>The prefab the next click places.</summary>
    public PrefabInfo? Prefab { get; set; }
    /// <summary>Turn of the next copy about the vertical, in degrees.</summary>
    public float Yaw { get; set; }

    public (string Key, string Action)[] Hints => [("LMB", $"place {Prefab?.Name ?? "prefab"}"), ("Ctrl", "no snap"), ("Esc", "exit tool")];

    public bool Cancel(EditorContext ctx)
    {
        _cursor = null;
        return false;
    }

    Quaternion Turn => Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw * MathF.PI / 180);

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        _cursor = ctx.ViewportHovered && Prefab != null ? _plane.Pick(ctx, false, ImGui.GetIO().KeyCtrl) : null;
        if (_cursor is { } p && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Place(ctx, p);
    }

    void Place(EditorContext ctx, Vector3 at)
    {
        if (Prefab is not { } prefab) return;
        if (ctx.Session.PrefabRoot is not { } root) { ctx.Status("Save the level first: prefab paths are relative to its project", error: true); return; }
        // The level's definition for it, else the file, read before the step so a bad file leaves no empty step.
        Document? file = null;
        if (Prefabs.Find(ctx.Doc, prefab.Guid) == null)
        {
            try { file = Prefabs.Read(prefab.FullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                ctx.Status($"Can't place {prefab.Name}: {e.Message}", error: true);
                return;
            }
        }
        ctx.Session.Do($"Place {prefab.Name}", () =>
        {
            var doc = ctx.Doc;
            var def = Prefabs.Find(doc, prefab.Guid);
            if (def == null)
            {
                def = Prefabs.Load(doc, root, prefab.FullPath, file);
                doc.Definitions = [.. doc.Definitions, def];
            }
            var placed = InstanceOps.Place(doc, def, new ObjectOrigin(at, Turn));
            ctx.Sel.ConvertTo(SelectMode.Object, doc);
            ctx.Sel.Clear();
            foreach (var id in placed.Members) ctx.Sel.Add(ElementRef.Object(id));
        });
        ctx.Status($"Placed {prefab.Name} at {at.X:0.##}, {at.Y:0.##}, {at.Z:0.##}. Editing any copy changes the prefab file");
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (_cursor is not { } at || Preview() is not { } parts) return;
        var world = Matrix4x4.CreateFromQuaternion(Turn) * Matrix4x4.CreateTranslation(at);
        uint col = ImGui.ColorConvertFloat4ToU32(Theme.Accent with { W = 0.7f });
        foreach (var part in parts)
            foreach (var e in part.Mesh.Edges())
                if (ctx.View.Project(Vector3.Transform(part.Mesh.Positions[e.A], world), out var a) &&
                    ctx.View.Project(Vector3.Transform(part.Mesh.Positions[e.B], world), out var b))
                    dl.AddLine(origin + a, origin + b, col, Theme.Px(1.5f));
    }

    /// <summary>The prefab's parts for the ghost, read again only when the file changes.</summary>
    IReadOnlyList<DefinitionPart>? Preview()
    {
        if (Prefab is not { } p) return null;
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(p.FullPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (_preview is { } cached && cached.File == p.FullPath && cached.Written == written) return cached.Parts;
        try { _preview = (p.FullPath, written, Prefabs.Parts(Core.IO.DocumentSerializer.Load(p.FullPath))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { _preview = (p.FullPath, written, []); }
        return _preview.Value.Parts;
    }

    public void DrawOptions(EditorContext ctx)
    {
        if (Prefab is { } p)
        {
            RowLabel("Prefab", p.RelativePath);
            ImGui.TextUnformatted(p.Name);
        }
        else FieldNote("Pick a prefab in the Place tab.");
        RowLabel("Yaw", "Turn of new copies about the vertical, in degrees");
        float yaw = Yaw;
        if (ImGui.DragFloat("##prefabYaw", ref yaw, 1, -180, 180, "%.0f°")) Yaw = yaw;
    }
}
