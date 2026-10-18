using System.Numerics;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor;

/// <summary>
/// Panels, each with one job: menu bar (commands + document state), tool strip (what the mouse does),
/// viewport header (how you look and select), inspector (the selection), status bar (reports only).
/// </summary>
public sealed partial class EditorApp
{
    bool HasSelection => !_session.Sel.IsEmpty;
    bool HasFaceTargets => HasSelection && _session.Sel.Mode is SelectMode.Face or SelectMode.Object;
    bool _openShortcuts;

    // ---- Menu bar ----

    void DrawMenuBar()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(7, 5));
        if (!ImGui.BeginMainMenuBar()) { ImGui.PopStyleVar(); return; }
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text("Chip");
        ImGui.PopFont();
        ImGui.SameLine(0, Theme.Px(10));
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New", "Ctrl+N")) NewDocument();
            if (ImGui.MenuItem("Open...", "Ctrl+O")) OpenDocument();
            if (ImGui.MenuItem("Save", "Ctrl+S")) Save(false);
            if (ImGui.MenuItem("Save as...", "Ctrl+Shift+S")) Save(true);
            ImGui.Separator();
            if (ImGui.MenuItem("Export GLB", "Ctrl+E")) Export(false);
            if (ImGui.MenuItem("Export GLB as...", "Ctrl+Shift+E")) Export(true);
            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) _window.Close();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem(_session.UndoLabel is { } u ? $"Undo {u}" : "Undo", "Ctrl+Z", false, _session.UndoLabel != null)) Undo();
            if (ImGui.MenuItem(_session.RedoLabel is { } r ? $"Redo {r}" : "Redo", "Ctrl+Y", false, _session.RedoLabel != null)) Redo();
            ImGui.Separator();
            if (ImGui.MenuItem("Select all", "Ctrl+A")) _session.Sel.SelectAll(_session.Doc);
            if (ImGui.MenuItem("Invert selection", "Ctrl+I")) _session.Sel.Invert(_session.Doc);
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", "Del", false, HasSelection)) DeleteSelection();
            if (ImGui.MenuItem("Duplicate", "Shift+D", false, HasFaceTargets)) Duplicate();
            if (ImGui.MenuItem("Hide selected", "H", false, HasSelection)) Hide();
            if (ImGui.MenuItem("Unhide all", "Alt+H", false, _session.Doc.Meshes.Any(m => m.Hidden))) UnhideAll();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Mesh"))
        {
            if (ImGui.MenuItem("Block tool", "Shift+B", _tool == Tool.Block)) ToggleBlockTool();
            ImGui.Separator();
            if (ImGui.MenuItem("Flip normals", "Shift+N", false, HasFaceTargets)) Flip();
            if (ImGui.MenuItem("Snap vertices to grid", "Shift+Ctrl+Alt+V", false, HasSelection)) VerticesToGrid();
            ImGui.Separator();
            ImGui.TextDisabled("Extrude: hold Shift and drag the move gizmo (faces)");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Texture"))
        {
            if (ImGui.MenuItem("Apply active material", "Shift+T", false, HasFaceTargets)) ApplyMaterial();
            ImGui.TextDisabled("Pick up a material: Shift+right-click a face");
            ImGui.Separator();
            if (ImGui.MenuItem("Align to grid", "Shift+Ctrl+G", false, HasFaceTargets)) AlignToGrid();
            if (ImGui.MenuItem("Align to face", "Shift+Ctrl+F", false, HasFaceTargets)) AlignToFace();
            if (ImGui.MenuItem("Fit to face", "Ctrl+Alt+Num5", false, HasFaceTargets)) FitTexture();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Focus selection", "F")) Focus();
            if (ImGui.MenuItem("Frame everything")) FrameAll();
            ImGui.Separator();
            if (ImGui.MenuItem("Smaller grid", "[")) StepGrid(-1);
            if (ImGui.MenuItem("Larger grid", "]")) StepGrid(1);
            ImGui.MenuItem("Snap to grid", null, ref _snap);
            if (ImGui.MenuItem("Select backfaces", "Alt+B", _backfaces)) ToggleBackfaces();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Keyboard shortcuts", "F1")) _openShortcuts = true;
            ImGui.EndMenu();
        }

        // Document state, right-aligned: name, then whether there is anything to save.
        string name = _session.Path != null ? Path.GetFileName(_session.Path) : "Untitled";
        string state = _session.Dirty ? "unsaved" : "saved";
        float w = ImGui.CalcTextSize(name).X + Theme.Px(10) + ImGui.CalcTextSize(state).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - w - Theme.Px(12));
        ImGui.Text(name);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(_session.Dirty ? Theme.Text : Theme.TextDim, state);
        ImGui.EndMainMenuBar();
        ImGui.PopStyleVar();
    }

    // ---- Tool strip: what the left mouse button does ----

    void DrawToolStrip(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(4, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(0, 2));
        ImGui.Begin("##tools", FixedPanel);
        var btn = new Vector2(size.X - Theme.Px(8), Theme.Px(36));
        bool select = _tool == Tool.Select;
        if (ToolButton(Icon.Select, "", "Select", select, btn)) { _tool = Tool.Select; _block.Cancel(); }
        if (ToolButton(Icon.Block, "", "Block tool  (Shift+B)", !select, btn)) ToggleBlockTool();
        StripGap();
        if (ToolButton(Icon.Move, "W", "Move\nHold Shift on faces to extrude", select && _gizmo.Mode == GizmoMode.Translate, btn)) SetGizmo(GizmoMode.Translate);
        if (ToolButton(Icon.Rotate, "E", "Rotate", select && _gizmo.Mode == GizmoMode.Rotate, btn)) SetGizmo(GizmoMode.Rotate);
        if (ToolButton(Icon.Scale, "R", "Scale", select && _gizmo.Mode == GizmoMode.Scale, btn)) SetGizmo(GizmoMode.Scale);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    // ---- Viewport header: how you look and what you select ----

    static float HeaderHeight => Theme.Px(38);

    static readonly (Icon, string, string)[] ModeItems =
    [
        (Icon.Vertex, "Vertex", "Select vertices  (1)"), (Icon.Edge, "Edge", "Select edges  (2)"),
        (Icon.Face, "Face", "Select faces  (3)"), (Icon.Object, "Object", "Select objects  (4)"),
    ];

    void DrawViewportHeader(float width)
    {
        float h = HeaderHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(8), MathF.Floor((h - ImGui.GetFrameHeight()) * 0.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(2, 0));
        ImGui.BeginChild("##vpheader", new Vector2(width, h), ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        SelectMode[] modes = [SelectMode.Vertex, SelectMode.Edge, SelectMode.Face, SelectMode.Object];
        int picked = SegmentedControl("mode", ModeItems, Array.IndexOf(modes, _session.Sel.Mode));
        if (picked >= 0) SetMode(modes[picked]);
        VSeparator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Perspective");

        // Right group: grid, snap, backfaces. Measured so it right-aligns exactly.
        var style = ImGui.GetStyle();
        string gridText = FormatLength(_grid);
        float slot = ImGui.CalcTextSize("3.13 cm").X;
        float Btn(string t) => ImGui.CalcTextSize(t).X + style.FramePadding.X * 2;
        float ToggleW(string label) => Theme.Px(8) * 2 + Fonts.IconSize + Theme.Px(6) + ImGui.CalcTextSize(label).X;
        float gap = Theme.Px(6);
        float rightW = ImGui.CalcTextSize("Grid").X + gap + Btn("-") + gap + slot + gap + Btn("+") + Theme.Px(12)
            + ToggleW("Snap") + Theme.Px(2) + ToggleW("Backfaces");
        ImGui.SameLine(width - style.WindowPadding.X - rightW);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Grid");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("-")) StepGrid(-1);
        Tip("Smaller grid  ([)");
        ImGui.SameLine(0, gap);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (slot - ImGui.CalcTextSize(gridText).X) * 0.5f);
        ImGui.Text(gridText);
        ImGui.SameLine(0, gap + (slot - ImGui.CalcTextSize(gridText).X) * 0.5f);
        if (ImGui.Button("+")) StepGrid(1);
        Tip("Larger grid  (])");
        ImGui.SameLine(0, Theme.Px(12));
        if (Toggle("snap", Icon.Snap, "Snap", _snap ? "Snap to grid is on" : "Snap to grid is off", _snap)) _snap = !_snap;
        ImGui.SameLine();
        if (Toggle("backfaces", Icon.Backfaces, "Backfaces", "Select faces that point away from you  (Alt+B)", _backfaces)) ToggleBackfaces();

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        // Hairline under the header so the 3D view reads as a separate surface.
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(width, 0), ImGui.ColorConvertFloat4ToU32(Theme.Border));
    }

    // ---- Inspector: the selection ----

    void DrawInspector(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 10));
        ImGui.Begin("##inspector", FixedPanel);
        ImGui.PushItemWidth(-1);

        DrawSelectionSection();
        DrawMaterialSection();
        if (InspectedMappings() is { Count: > 0 } maps) DrawTextureSection(maps);
        DrawExportSection();

        ImGui.PopItemWidth();
        ImGui.End();
        ImGui.PopStyleVar();
    }

    void DrawSelectionSection()
    {
        var sel = _session.Sel;
        Heading("Selection", first: true, trailing: sel.IsEmpty ? null : $"{sel.Count} {ModeName(sel.Mode).ToLowerInvariant()}{(sel.Count == 1 ? "" : "s")}");
        if (sel.IsEmpty)
        {
            ImGui.TextColored(Theme.TextDim, $"Click or drag to select {ModeName(sel.Mode).ToLowerInvariant()}s");
            return;
        }
        if (SelectionBounds() is not { } b) return;
        var min = b.Min;
        var sz = b.Max - b.Min;
        RowLabel("Min", "Bounds minimum corner in metres. Type a value, then Enter.");
        if (TaggedFields("##min", ["X", "Y", "Z"], Theme.AxisU32, [min.X, min.Y, min.Z], [false, false, false], "%.3f", out int ai, out float av))
        {
            if (ai == 0) min.X = av; else if (ai == 1) min.Y = av; else min.Z = av;
            SetBounds(min, b.Max - b.Min);
        }
        RowLabel("Size", "Extent in metres. Type a value, then Enter.");
        if (TaggedFields("##size", ["X", "Y", "Z"], Theme.AxisU32, [sz.X, sz.Y, sz.Z], [false, false, false], "%.3f", out int si, out float sv))
        {
            if (si == 0) sz.X = sv; else if (si == 1) sz.Y = sv; else sz.Z = sv;
            SetBounds(b.Min, sz);
        }
    }

    static readonly uint[] UvTags = [0xFFA8A0A0, 0xFFA8A0A0];

    void DrawMaterialSection()
    {
        var active = _lib.Get(_activeMaterial);
        Heading("Material");
        ImGui.Image(_renderer.TextureId(active.Name), new Vector2(Theme.Px(44)));
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.BeginGroup();
        ImGui.Text(active.Name);
        ImGui.TextColored(Theme.TextDim, $"{active.Width} px, tiles every {FormatLength(active.Width * TextureMapping.DefaultScale)}");
        ImGui.EndGroup();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Active material: used by new blocks and by Apply");
        ImGui.Dummy(Theme.Px(0, 2));
        DrawPalette();

        var used = _session.Sel.FaceSets(_session.Doc).SelectMany(kv => kv.Value.Select(f => kv.Key.Faces[f].Material)).Distinct().ToList();
        ImGui.BeginDisabled(!HasFaceTargets);
        if (ImGui.Button("Apply", new Vector2(Theme.Px(72), 0))) ApplyMaterial();
        ImGui.EndDisabled();
        Tip(HasFaceTargets ? "Apply the active material to the selection  (Shift+T)" : "Select faces or objects to apply  (Shift+T)");
        if (used.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, used.Count == 1 ? $"replaces {used[0]}" : $"replaces {used.Count} materials");
            Tip("The selection's current material");
        }
    }

    void DrawPalette()
    {
        float cell = Theme.Px(48), spacing = Theme.Px(6);
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cell + spacing)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        var dl = ImGui.GetWindowDrawList();
        for (int i = 0; i < _lib.All.Count; i++)
        {
            var m = _lib.All[i];
            if (i % perRow != 0) ImGui.SameLine(0, spacing);
            ImGui.PushID(i);
            if (ImGui.ImageButton("##mat", _renderer.TextureId(m.Name), new Vector2(cell))) _activeMaterial = m.Name;
            ImGui.PopID();
            var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            bool isActive = string.Equals(m.Name, _activeMaterial, StringComparison.OrdinalIgnoreCase);
            if (isActive) dl.AddRect(a - new Vector2(Theme.Px(2)), b + new Vector2(Theme.Px(2)), Theme.AccentU32, Theme.Px(4), ImDrawFlags.None, Theme.Px(2));
            else if (ImGui.IsItemHovered()) dl.AddRect(a - new Vector2(Theme.Px(1)), b + new Vector2(Theme.Px(1)), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), Theme.Px(3));
            if (i < 10)
            {
                string digit = ((i + 1) % 10).ToString();
                var ds = ImGui.CalcTextSize(digit) * (Fonts.SmallSize / Fonts.BodySize);
                var badge = b - ds - Theme.Px(6, 3);
                dl.AddRectFilled(badge, b, ImGui.ColorConvertFloat4ToU32(Theme.Bg with { W = 0.85f }), Theme.Px(3), ImDrawFlags.RoundCornersTopLeft);
                dl.AddText(Fonts.Small, Fonts.SmallSize, badge + Theme.Px(3, 1), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), digit);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{m.Name}\n{m.Width} × {m.Height} px" + (i < 10 ? $"\nShift+Alt+{(i + 1) % 10}" : ""));
        }
        ImGui.PopStyleVar();
    }

    void DrawTextureSection(List<TextureMapping> maps)
    {
        int faceCount = _session.Sel.FaceSets(_session.Doc).Sum(kv => kv.Value.Count);
        Heading("Texture", trailing: faceCount == 1 ? "1 face" : $"{faceCount} faces");

        var m0 = maps[0];
        bool Mixed(Func<TextureMapping, float> f) => maps.Any(m => f(m) != f(m0));

        RowLabel("Shift", "Offset in texels");
        if (TaggedFields("##shift", ["U", "V"], UvTags, [m0.ShiftU, m0.ShiftV], [Mixed(m => m.ShiftU), Mixed(m => m.ShiftV)], "%.1f", out int si, out float sv))
            EditMapping("Texture Shift", si == 0 ? m => m with { ShiftU = sv } : m => m with { ShiftV = sv });
        // Scale is shown as a multiple of the default density (1 = 512 px per metre).
        float k = TextureMapping.DefaultScale;
        RowLabel("Scale", "1 = 512 px per metre");
        if (TaggedFields("##scale", ["U", "V"], UvTags, [m0.ScaleU / k, m0.ScaleV / k], [Mixed(m => m.ScaleU), Mixed(m => m.ScaleV)], "%.3g", out int ci, out float cv))
            EditMapping("Texture Scale", ci == 0 ? m => m with { ScaleU = cv * k } : m => m with { ScaleV = cv * k });
        RowLabel("Rotate", "Degrees");
        if (TaggedFields("##rot", ["°"], UvTags, [m0.Rotation], [Mixed(m => m.Rotation)], "%.1f", out _, out float rv))
            EditMapping("Texture Rotation", m => m with { Rotation = rv });

        ImGui.Dummy(Theme.Px(0, 2));
        float gap = Theme.Px(4);
        float w = (ImGui.GetContentRegionAvail().X - gap * 4) / 5;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));
        if (ImGui.Button("Grid", new Vector2(w, 0))) AlignToGrid();
        Tip("Align to grid: project from the nearest world axis  (Shift+Ctrl+G)");
        ImGui.SameLine();
        if (ImGui.Button("Face", new Vector2(w, 0))) AlignToFace();
        Tip("Align to face: project along the face  (Shift+Ctrl+F)");
        ImGui.SameLine();
        if (ImGui.Button("Fit", new Vector2(w, 0))) FitTexture();
        Tip("Fit: one tile across the face  (Ctrl+Alt+Num5)");
        ImGui.SameLine();
        if (IconButton("flipu", Icon.FlipU, "Flip horizontally (U)", w)) EditMapping("Flip U", m => m with { ScaleU = -m.ScaleU });
        ImGui.SameLine();
        if (IconButton("flipv", Icon.FlipV, "Flip vertically (V)", w)) EditMapping("Flip V", m => m with { ScaleV = -m.ScaleV });
        ImGui.PopStyleVar();
    }

    /// <summary>Mappings of every addressed face, the active face first.</summary>
    List<TextureMapping>? InspectedMappings()
    {
        var sel = _session.Sel;
        var result = new List<TextureMapping>();
        if (sel.Mode == SelectMode.Face && sel.Active is { } a && _session.Doc.Find(a.Mesh) is { } am && a.A < am.Faces.Count)
            result.Add(am.Faces[a.A].Map);
        foreach (var (mesh, faces) in sel.FaceSets(_session.Doc))
            result.AddRange(faces.Select(f => mesh.Faces[f].Map));
        return result.Count > 0 ? result : null;
    }

    void DrawExportSection()
    {
        var doc = _session.Doc;
        Heading("Export", trailing: doc.ExportPath != null ? Path.GetFileName(doc.ExportPath) : null);
        if (ImGui.Button("Export GLB", new Vector2(-1, 0))) Export(false);
        Tip(doc.ExportPath != null ? $"Write {doc.ExportPath}  (Ctrl+E)" : "Choose where to write the .glb  (Ctrl+E)");
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(2, 3));
        bool open = ImGui.TreeNodeEx("Settings", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.PopStyleVar();
        if (open)
        {
            float scale = doc.ExportScale;
            RowLabel("Scale", "Multiplier on export. 1 for Godot (metres), 100 for centimetre engines");
            if (ImGui.InputFloat("##scale", ref scale, 0, 0, "%.3g ×", ImGuiInputTextFlags.EnterReturnsTrue) && scale > 0)
                _session.Do("Export Scale", () => _session.Doc.ExportScale = scale);
            bool col = doc.ExportCollision;
            if (ImGui.Checkbox("Static collision", ref col)) _session.Do("Export Collision", () => _session.Doc.ExportCollision = col);
            Tip("Names nodes with -col so Godot builds a trimesh collider for each mesh");
            ImGui.TreePop();
        }
    }

    // ---- Status bar: reports, never settings ----

    void DrawStatusBar(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(6, 2));
        float rowH = ImGui.GetTextLineHeight() + Theme.Px(2) * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(12), MathF.Floor((size.Y - rowH) * 0.5f)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Bg);
        ImGui.Begin("##status", FixedPanel | ImGuiWindowFlags.NoScrollbar);
        ImGui.AlignTextToFramePadding();

        var sel = _session.Sel;
        ImGui.Text(ModeName(sel.Mode));
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(Theme.TextDim, sel.IsEmpty ? "nothing selected" : $"{sel.Count} selected");

        int faces = _session.Doc.Meshes.Sum(m => m.Faces.Count);
        string counts = $"{_session.Doc.Meshes.Count} meshes   {faces} faces";
        float rightX = size.X - ImGui.GetStyle().WindowPadding.X - ImGui.CalcTextSize(counts).X;

        ImGui.SameLine(Theme.Px(210));
        bool showMessage = _statusMessage.Length > 0 && (_statusIsError || ImGui.GetTime() < _statusUntil);
        ImGui.PushClipRect(ImGui.GetCursorScreenPos(), new Vector2(pos.X + rightX - Theme.Px(16), pos.Y + size.Y), true);
        if (showMessage)
        {
            ImGui.TextColored(_statusIsError ? Theme.Error : Theme.Text, _statusMessage);
            if (_statusIsError)
            {
                ImGui.SameLine();
                if (ImGui.Button("Dismiss")) _statusMessage = "";
            }
        }
        else KeyHints(_tool == Tool.Block ? _block.Hints : _gesture != null ? [("Esc", "cancel")] : DefaultHints);
        ImGui.PopClipRect();

        ImGui.SameLine(rightX);
        ImGui.TextColored(Theme.TextDim, counts);

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    static readonly (string Key, string Action)[] DefaultHints =
        [("LMB", "select"), ("Drag", "box select"), ("RMB", "fly"), ("MMB", "pan"), ("Alt+LMB", "orbit"), ("F1", "shortcuts")];

    // ---- Keyboard shortcuts sheet ----

    static readonly (string Group, (string Key, string Action)[] Rows)[] ShortcutGroups =
    [
        ("Camera", [("RMB + WASD", "fly"), ("Q / E", "down / up while flying"), ("Shift", "fly faster"), ("Scroll", "dolly; fly speed while flying"),
            ("Alt + LMB", "orbit"), ("MMB", "pan"), ("F", "focus selection")]),
        ("Select", [("1 2 3 4", "vertex, edge, face, object"), ("LMB", "select"), ("Shift + LMB", "add"), ("Ctrl + LMB", "toggle"),
            ("Drag", "box select"), ("Ctrl + A / I", "all / invert"), ("Esc", "clear"), ("Alt + B", "select backfaces")]),
        ("Tools", [("Shift + B", "block tool"), ("W E R", "move, rotate, scale"), ("Shift + drag", "extrude faces"),
            ("Alt + arrows", "nudge one grid step"), ("[ ]", "grid size"), ("Enter / Esc", "confirm / cancel")]),
        ("Edit", [("Del", "delete"), ("Shift + D", "duplicate"), ("Shift + N", "flip normals"), ("H / Alt + H", "hide / unhide all"),
            ("Shift+Ctrl+Alt+V", "snap vertices to grid"), ("Ctrl + Z / Y", "undo / redo")]),
        ("Texture", [("Shift + T", "apply material"), ("Shift + RMB", "pick up material"), ("Shift+Alt+1..0", "material slot"),
            ("Shift+Ctrl+G", "align to grid"), ("Shift+Ctrl+F", "align to face"), ("Ctrl+Alt+Num5", "fit")]),
        ("File", [("Ctrl + N / O", "new / open"), ("Ctrl + S", "save"), ("Ctrl+Shift+S", "save as"), ("Ctrl + E", "export GLB"),
            ("Ctrl+Shift+E", "export as"), ("F1", "this sheet")]),
    ];

    void DrawShortcutsSheet()
    {
        if (_openShortcuts) { ImGui.OpenPopup("Keyboard shortcuts"); _openShortcuts = false; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(Theme.Px(1120), vp.WorkSize.X - Theme.Px(80)), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        bool open = true;
        if (ImGui.BeginPopupModal("Keyboard shortcuts", ref open, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (ImGui.BeginTable("##groups", 3, ImGuiTableFlags.SizingStretchSame))
            {
                for (int g = 0; g < ShortcutGroups.Length; g++)
                {
                    if (g % 3 == 0) ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var (group, rows) = ShortcutGroups[g];
                    ImGui.PushFont(Fonts.Strong);
                    ImGui.Text(group);
                    ImGui.PopFont();
                    ImGui.Dummy(Theme.Px(0, 2));
                    float keyW = rows.Max(r => ImGui.CalcTextSize(r.Key).X) + Theme.Px(16); // per group, so actions never clip
                    foreach (var (key, action) in rows)
                    {
                        float x0 = ImGui.GetCursorPosX();
                        ImGui.Text(key);
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(x0 + keyW);
                        ImGui.TextColored(Theme.TextDim, action);
                    }
                    ImGui.Dummy(Theme.Px(0, 12));
                }
                ImGui.EndTable();
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsKeyPressed(ImGuiKey.F1, false)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    // ---- Unsaved changes ----

    void DrawConfirmModal()
    {
        if (_openConfirm) { ImGui.OpenPopup("Unsaved changes"); _openConfirm = false; }
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        if (ImGui.BeginPopupModal("Unsaved changes", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            string name = _session.Path != null ? Path.GetFileName(_session.Path) : "this level";
            ImGui.Text($"Save changes to {name} first?");
            ImGui.TextColored(Theme.TextDim, "Unsaved changes are lost if you don't save.");
            ImGui.Dummy(Theme.Px(0, 8));
            var w = new Vector2(Theme.Px(104), 0);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.ActiveFill);
            bool save = ImGui.Button("Save", w);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            bool discard = ImGui.Button("Don't save", w);
            ImGui.SameLine();
            bool cancel = ImGui.Button("Cancel", w) || ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (save || discard || cancel)
            {
                ImGui.CloseCurrentPopup();
                var then = _afterConfirm;
                _afterConfirm = null;
                if (discard || (save && Save(false))) then?.Invoke();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }
}
