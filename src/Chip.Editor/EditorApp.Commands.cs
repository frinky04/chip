using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Viewport;
using ImGuiNET;
using NativeFileDialogSharp;

namespace Chip.Editor;

/// <summary>Every editor command. Hotkeys, menus and panels all call these; each edit is one Session step.</summary>
public sealed partial class EditorApp
{
    /// <summary>Metric powers of two: 1/32 m (~3 cm) to 16 m.</summary>
    static readonly float[] GridSizes = [1 / 32f, 1 / 16f, 1 / 8f, 1 / 4f, 1 / 2f, 1, 2, 4, 8, 16];

    string _statusMessage = "";
    bool _statusIsError;
    double _statusUntil;
    Action? _afterConfirm;
    bool _openConfirm, _allowClose;

    void Status(string message, bool error = false)
    {
        _statusMessage = message;
        _statusIsError = error;
        _statusUntil = ImGui.GetTime() + (error ? 8 : 4);
    }

    void HandleHotkeys()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || _flying) return;
        bool K(ImGuiKey k, bool ctrl = false, bool shift = false, bool alt = false) =>
            ImGui.IsKeyPressed(k, false) && io.KeyCtrl == ctrl && io.KeyShift == shift && io.KeyAlt == alt;

        if (K(ImGuiKey.N, ctrl: true)) NewDocument();
        else if (K(ImGuiKey.O, ctrl: true)) OpenDocument();
        else if (K(ImGuiKey.S, ctrl: true)) Save(false);
        else if (K(ImGuiKey.S, ctrl: true, shift: true)) Save(true);
        else if (K(ImGuiKey.E, ctrl: true)) Export(false);
        else if (K(ImGuiKey.E, ctrl: true, shift: true)) Export(true);
        else if (K(ImGuiKey.Z, ctrl: true)) Undo();
        else if (K(ImGuiKey.Y, ctrl: true) || K(ImGuiKey.Z, ctrl: true, shift: true)) Redo();

        if (_gesture != null)
        {
            if (K(ImGuiKey.Escape)) EndGesture(commit: false);
            return;
        }

        if (K(ImGuiKey.Escape))
        {
            if (_block.Busy) _block.Cancel();
            else if (_tool == Tool.Block) _tool = Tool.Select;
            else _session.Sel.Clear();
        }
        else if (K(ImGuiKey.B, shift: true)) ToggleBlockTool();
        else if (K(ImGuiKey._1)) SetMode(SelectMode.Vertex);
        else if (K(ImGuiKey._2)) SetMode(SelectMode.Edge);
        else if (K(ImGuiKey._3)) SetMode(SelectMode.Face);
        else if (K(ImGuiKey._4)) SetMode(SelectMode.Object);
        else if (K(ImGuiKey.W)) SetGizmo(GizmoMode.Translate);
        else if (K(ImGuiKey.E)) SetGizmo(GizmoMode.Rotate);
        else if (K(ImGuiKey.R)) SetGizmo(GizmoMode.Scale);
        else if (K(ImGuiKey.LeftBracket)) StepGrid(-1);
        else if (K(ImGuiKey.RightBracket)) StepGrid(1);
        else if (K(ImGuiKey.F)) Focus();
        else if (K(ImGuiKey.A, ctrl: true)) _session.Sel.SelectAll(_session.Doc);
        else if (K(ImGuiKey.I, ctrl: true)) _session.Sel.Invert(_session.Doc);
        else if (K(ImGuiKey.Delete)) DeleteSelection();
        else if (K(ImGuiKey.D, shift: true)) Duplicate();
        else if (K(ImGuiKey.N, shift: true)) Flip();
        else if (K(ImGuiKey.H)) Hide();
        else if (K(ImGuiKey.H, alt: true)) UnhideAll();
        else if (K(ImGuiKey.B, alt: true)) ToggleBackfaces();
        else if (K(ImGuiKey.F1)) _openShortcuts = true;
        else if (K(ImGuiKey.T, shift: true)) ApplyMaterial();
        else if (K(ImGuiKey.G, ctrl: true, shift: true)) AlignToGrid();
        else if (K(ImGuiKey.F, ctrl: true, shift: true)) AlignToFace();
        else if (K(ImGuiKey.Keypad5, ctrl: true, alt: true)) FitTexture();
        else if (K(ImGuiKey.V, ctrl: true, shift: true, alt: true)) VerticesToGrid();
        else if (K(ImGuiKey.LeftArrow, alt: true)) Nudge(-1, 0);
        else if (K(ImGuiKey.RightArrow, alt: true)) Nudge(1, 0);
        else if (K(ImGuiKey.UpArrow, alt: true)) Nudge(0, 1);
        else if (K(ImGuiKey.DownArrow, alt: true)) Nudge(0, -1);

        for (int i = 0; i < 10; i++)
            if (K(ImGuiKey._0 + (i + 1) % 10, shift: true, alt: true)) SetActiveMaterial(i);
    }

    // ---- File ----

    void ConfirmDiscard(Action then)
    {
        if (!_session.Dirty) { then(); return; }
        _afterConfirm = then;
        _openConfirm = true;
    }

    void OnClosing()
    {
        if (_allowClose || !_session.Dirty) return;
        _window.IsClosing = false;
        ConfirmDiscard(() => { _allowClose = true; _window.Close(); });
    }

    void NewDocument() => ConfirmDiscard(() =>
    {
        EndGesture(commit: false);
        _session.Reset(new Document(), null);
        Status("New document");
    });

    void OpenDocument() => ConfirmDiscard(() =>
    {
        var result = Dialog.FileOpen("chip");
        if (!result.IsOk) return;
        try
        {
            EndGesture(commit: false);
            _session.Reset(DocumentSerializer.Load(result.Path), result.Path);
            FrameAll();
            Status($"Opened {Path.GetFileName(result.Path)}");
        }
        catch (Exception e) { Status($"Couldn't open {Path.GetFileName(result.Path)}: {e.Message}", true); }
    });

    bool Save(bool saveAs)
    {
        var path = _session.Path;
        if (saveAs || path == null)
        {
            var result = Dialog.FileSave("chip");
            if (!result.IsOk) return false;
            path = EnsureExtension(result.Path, ".chip");
        }
        try
        {
            DocumentSerializer.Save(_session.Doc, path);
            _session.MarkSaved(path);
            Status($"Saved {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception e) { Status($"Couldn't save {Path.GetFileName(path)}: {e.Message} Choose another folder and try again.", true); return false; }
    }

    void Export(bool exportAs)
    {
        var path = _session.Doc.ExportPath;
        if (exportAs || path == null)
        {
            var result = Dialog.FileSave("glb");
            if (!result.IsOk) return;
            path = EnsureExtension(result.Path, ".glb");
            if (path != _session.Doc.ExportPath) _session.Do("Set Export Path", () => _session.Doc.ExportPath = path);
        }
        try
        {
            GlbExporter.Export(_session.Doc, _lib, path);
            Status($"Exported {_session.Doc.Meshes.Count} meshes to {Path.GetFileName(path)}");
        }
        catch (Exception e) { Status($"Export failed: {e.Message} Choose a writable folder and try again.", true); }
    }

    static string EnsureExtension(string path, string ext) =>
        path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? path : path + ext;

    void Undo()
    {
        EndGesture(commit: false);
        var label = _session.UndoLabel;
        if (_session.Undo()) Status($"Undid {label}");
    }

    void Redo()
    {
        EndGesture(commit: false);
        var label = _session.RedoLabel;
        if (_session.Redo()) Status($"Redid {label}");
    }

    // ---- Modes / view ----

    void SetMode(SelectMode mode)
    {
        _session.Sel.ConvertTo(mode, _session.Doc);
        _tool = Tool.Select;
        _block.Cancel();
    }

    void SetGizmo(GizmoMode mode)
    {
        _gizmo.Mode = mode;
        _tool = Tool.Select;
        _block.Cancel();
    }

    void ToggleBlockTool()
    {
        _block.Cancel();
        _tool = _tool == Tool.Block ? Tool.Select : Tool.Block;
    }

    void StepGrid(int dir)
    {
        int i = Array.IndexOf(GridSizes, _grid);
        _grid = GridSizes[Math.Clamp((i < 0 ? 3 : i) + dir, 0, GridSizes.Length - 1)];
    }

    /// <summary>Grid size for display: centimetres below a metre.</summary>
    static string FormatLength(float metres) => metres < 1 ? $"{metres * 100:0.##} cm" : $"{metres:0.##} m";

    void ToggleBackfaces()
    {
        _backfaces = !_backfaces;
        Status(_backfaces ? "Backface selection on: faces pointing away can be picked" : "Backface selection off");
    }

    void Focus()
    {
        if (SelectionBounds() is { } b) { _cam.Frame(b.Min, b.Max); return; }
        var mouse = ImGui.GetIO().MousePos - _vpOrigin;
        if (_vpHovered && Picker.RaycastFace(_session.Doc, _view.ScreenRay(mouse)) is { } hit)
        {
            _cam.Position = hit.Point - _cam.Forward * 4;
            return;
        }
        FrameAll();
    }

    void FrameAll()
    {
        var visible = _session.Doc.Meshes.Where(m => !m.Hidden && m.Positions.Count > 0).ToList();
        if (visible.Count == 0) { _cam.Frame(new Vector3(-4), new Vector3(4)); return; }
        var min = visible.Select(m => m.Bounds().Min).Aggregate(Vector3.Min);
        var max = visible.Select(m => m.Bounds().Max).Aggregate(Vector3.Max);
        _cam.Frame(min, max);
    }

    // ---- Geometry ----

    void CreateBlock(Vector3 min, Vector3 max) => _session.Do("Create Block", () =>
    {
        var mesh = _session.Doc.Add(MeshOps.CreateBox(min, max, _activeMaterial, TextureMapping.Default));
        var sel = _session.Sel;
        sel.ConvertTo(SelectMode.Object, _session.Doc);
        sel.Clear();
        sel.Add(ElementRef.Object(mesh.Id));
    });

    void DeleteSelection()
    {
        var sel = _session.Sel;
        if (sel.IsEmpty) return;
        _session.Do("Delete", () =>
        {
            var doc = _session.Doc;
            if (sel.Mode == SelectMode.Object)
            {
                var ids = sel.MeshIds();
                doc.Meshes.RemoveAll(m => ids.Contains(m.Id));
            }
            else
            {
                foreach (var g in sel.Items.GroupBy(i => i.Mesh))
                {
                    if (doc.Find(g.Key) is not { } mesh) continue;
                    var items = g.ToList();
                    IEnumerable<int> faces = sel.Mode switch
                    {
                        SelectMode.Face => items.Select(i => i.A),
                        SelectMode.Edge => FacesWhere(mesh, f => f.Edges().Any(e => items.Contains(ElementRef.EdgeOf(mesh.Id, e)))),
                        _ => FacesWhere(mesh, f => f.Verts.Any(v => items.Contains(ElementRef.Vertex(mesh.Id, v)))),
                    };
                    MeshOps.DeleteFaces(mesh, faces.ToList());
                }
                doc.Meshes.RemoveAll(m => m.Faces.Count == 0);
            }
            sel.Clear();
        });
    }

    static IEnumerable<int> FacesWhere(PolyMesh m, Func<Face, bool> pred) =>
        Enumerable.Range(0, m.Faces.Count).Where(f => pred(m.Faces[f]));

    void Duplicate()
    {
        var sel = _session.Sel;
        if (sel.Mode is not (SelectMode.Object or SelectMode.Face) || sel.IsEmpty) return;
        _session.Do("Duplicate", () =>
        {
            var doc = _session.Doc;
            var copies = new List<ElementRef>();
            if (sel.Mode == SelectMode.Object)
                foreach (var id in sel.MeshIds())
                {
                    if (doc.Find(id) is not { } src) continue;
                    copies.Add(ElementRef.Object(doc.Add(src.Clone()).Id));
                }
            else
                foreach (var (mesh, faces) in sel.FaceSets(doc))
                    copies.AddRange(MeshOps.DuplicateFaces(mesh, faces).Select(f => ElementRef.Face(mesh.Id, f)));
            sel.Clear();
            foreach (var c in copies) sel.Add(c);
        });
    }

    void ForFaces(string label, Action<PolyMesh, List<int>> edit)
    {
        var sets = _session.Sel.FaceSets(_session.Doc);
        if (sets.Count == 0) { Status($"Select faces or objects to {label.ToLowerInvariant()}"); return; }
        _session.Do(label, () =>
        {
            foreach (var (mesh, faces) in _session.Sel.FaceSets(_session.Doc)) edit(mesh, faces);
        });
    }

    void Flip() => ForFaces("Flip Normals", MeshOps.FlipFaces);

    void VerticesToGrid()
    {
        if (_session.Sel.IsEmpty) return;
        _session.Do("Vertices to Grid", () =>
        {
            foreach (var (mesh, verts) in _session.Sel.VertexSets(_session.Doc)) MeshOps.SnapToGrid(mesh, verts, _grid);
        });
    }

    void Hide()
    {
        var ids = _session.Sel.MeshIds();
        if (ids.Count == 0) return;
        _session.Do("Hide", () =>
        {
            foreach (var m in _session.Doc.Meshes) if (ids.Contains(m.Id)) m.Hidden = true;
            _session.Sel.Clear();
        });
    }

    void UnhideAll()
    {
        if (!_session.Doc.Meshes.Any(m => m.Hidden)) return;
        _session.Do("Unhide All", () => { foreach (var m in _session.Doc.Meshes) m.Hidden = false; });
    }

    void Nudge(int right, int up)
    {
        var f = _cam.Forward;
        var axisRight = TextureMapping.DominantAxis(_cam.Right with { Y = 0 });
        var axisUp = MathF.Abs(f.Y) > 0.7f ? TextureMapping.DominantAxis(_cam.Up with { Y = 0 }) : Vector3.UnitY;
        var delta = (axisRight * right + axisUp * up) * _grid;
        if (TransformGesture.Start(_session, "Nudge") is not { } g) return;
        g.Apply(Matrix4x4.CreateTranslation(delta), rigid: true);
        g.Commit();
    }

    /// <summary>Numeric entry: moves the selection so its bounds start at <paramref name="min"/> and scales it to <paramref name="size"/>.</summary>
    void SetBounds(Vector3 min, Vector3 size)
    {
        if (SelectionBounds() is not { } b) return;
        var oldSize = b.Max - b.Min;
        float Ratio(float n, float o) => o < 1e-4f || n < 1e-4f ? 1 : n / o;
        var scale = new Vector3(Ratio(size.X, oldSize.X), Ratio(size.Y, oldSize.Y), Ratio(size.Z, oldSize.Z));
        bool rigid = scale == Vector3.One;
        if (TransformGesture.Start(_session, rigid ? "Set Position" : "Set Size") is not { } g) return;
        g.Apply(TransformGesture.About(b.Min, Matrix4x4.CreateScale(scale)) * Matrix4x4.CreateTranslation(min - b.Min), rigid);
        g.Commit();
    }

    // ---- Materials / texturing ----

    void SetActiveMaterial(int slot)
    {
        if (slot >= _lib.All.Count) return;
        _activeMaterial = _lib.All[slot].Name;
        Status($"Active material: {_activeMaterial}");
    }

    void ApplyMaterial() => ForFaces("Apply Material", (mesh, faces) =>
    {
        foreach (var f in faces) mesh.Faces[f].Material = _activeMaterial;
    });

    void LiftMaterial(Vector2 mouse)
    {
        if (Picker.RaycastFace(_session.Doc, _view.ScreenRay(mouse), _backfaces) is not { } hit) return;
        _activeMaterial = hit.Mesh.Faces[hit.Face].Material;
        Status($"Picked up {_activeMaterial}");
    }

    void AlignToGrid() => ForFaces("Align to Grid", (mesh, faces) =>
    {
        foreach (var f in faces) mesh.Faces[f].Map = TextureMapping.AlignToGrid(mesh.FaceNormal(f), mesh.Faces[f].Map);
    });

    void AlignToFace() => ForFaces("Align to Face", (mesh, faces) =>
    {
        foreach (var f in faces) mesh.Faces[f].Map = TextureMapping.AlignToFace(mesh.FaceNormal(f), mesh.Faces[f].Map);
    });

    void FitTexture() => ForFaces("Fit Texture", (mesh, faces) =>
    {
        foreach (var f in faces)
        {
            var info = _lib.Get(mesh.Faces[f].Material);
            mesh.Faces[f].Map = mesh.Faces[f].Map.FitTo(mesh.FacePoints(f), info.Width, info.Height);
        }
    });

    void EditMapping(string label, Func<TextureMapping, TextureMapping> edit) => ForFaces(label, (mesh, faces) =>
    {
        foreach (var f in faces) mesh.Faces[f].Map = edit(mesh.Faces[f].Map);
    });
}
