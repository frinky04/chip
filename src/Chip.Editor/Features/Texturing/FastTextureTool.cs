using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Texturing;

/// <summary>
/// Scythe's Fast Texture Tool (Shift+Q): a 2D UV editor panel over the right of the viewport for the selected faces.
/// The face material is the background (tiled with Repeat), face UVs are orange polygons, and the blue rectangle is the
/// footprint the faces would have at 512 px/m. Drag moves UVs, the handles scale them; every gesture is one undo step
/// and writes explicit UVs. While open the tool owns the keyboard (tool keys below); undo, redo and Esc still work, and
/// clicking faces in the view (left of the panel) changes what is being edited.
/// </summary>
public sealed class FastTextureTool : IViewportTool
{
    enum Drag { None, Move, Scale, Pan }

    const uint Blue = 0xFFFF9E4A;           // ideal-density footprint
    const int MaxGridLevel = 6;

    // View: screen = centre + (uv - pan) * aspect * zoom. Aspect keeps texels square for non-square textures.
    Vector2 _pan = new(0.5f);
    float _zoom;
    Vector2 _aspect = Vector2.One;
    Vector2 _c0, _c1;
    bool _frameNext = true;
    int _gridLevel = 2;
    bool _repeat = true, _coords, _original = true, _worldScale, _closeNext;

    // Gesture
    Drag _drag;
    Vector2 _pressMouse;
    bool _moved;
    int _handle = -1;
    (Vector2 Min, Vector2 Max) _startBounds;
    readonly List<(Face Face, Vector2[] Start)> _start = new();

    // Ideal footprint cache (recomputed when the document or the selection changes)
    int _idealVersion = -1, _idealKey;
    Vector2 _idealMetres;

    public string Id => "fastTexture";
    public string Name => "Fast texture";
    public Icon Icon => Icon.Texture;
    public string? Hotkey => "Shift+Q";
    public int Order => 20;

    /// <summary>Always true while open, so global hotkeys wait and the tool's own keys (Q, E, F, 1-5...) reach it.</summary>
    public bool Busy => true;

    public (string Key, string Action)[] Hints => _drag is Drag.Move or Drag.Scale
        ? [("Shift", "uniform scale"), ("Ctrl", "no snap"), ("Esc", "cancel")]
        : [("1-5", "unwrap"), ("Drag", "move UVs"), ("Q/E", "rotate"), ("F", "frame"), ("Click view", "pick faces"), ("Esc", "close")];

    public static void RegisterReferences(CommandRegistry commands)
    {
        commands.AddReference("Fast texture", "drag", "move UVs (drag a handle to scale)");
        commands.AddReference("Fast texture", "Q / E", "rotate UVs");
        commands.AddReference("Fast texture", "Alt+R / Alt+T", "flip UVs horizontally / vertically");
        commands.AddReference("Fast texture", "1 – 5", "unwrap: existing, conforming, square, follow quads, planar");
        commands.AddReference("Fast texture", "Ctrl+1 – 3", "follow-quads spacing");
        commands.AddReference("Fast texture", "[ / ]", "grid finer / coarser");
        commands.AddReference("Fast texture", "F", "frame the selection");
        commands.AddReference("Fast texture", "R", "reset the view");
        commands.AddReference("Fast texture", "B", "repeat the texture behind");
        commands.AddReference("Fast texture", "C", "show coordinates");
        commands.AddReference("Fast texture", "T", "show the original material");
        commands.AddReference("Fast texture", "G", "world-scale grid");
    }

    public void Activate(EditorContext ctx)
    {
        _frameNext = true;
        _closeNext = false;
        if (ctx.Sel.Mode is SelectMode.Vertex or SelectMode.Edge) ctx.Sel.ConvertTo(SelectMode.Face, ctx.Doc);
    }

    public bool Cancel(EditorContext ctx)
    {
        switch (_drag)
        {
            case Drag.Move or Drag.Scale:
                ctx.Session.Cancel();
                EndDrag();
                return true;
            case Drag.Pan:
                EndDrag();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Picks faces in the 3D view (outside the panel) and closes on request. UV editing happens in <see cref="Draw"/>.</summary>
    public void Update(EditorContext ctx)
    {
        if (_closeNext)
        {
            _closeNext = false;
            if (ctx.DefaultTool != null) ctx.SetTool(ctx.DefaultTool);
            return;
        }
        if (!ctx.ViewportHovered || _drag != Drag.None) { ctx.Hover = null; return; }
        ctx.Hover = ctx.PickAtMouse(Theme.Scale);
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        var io = ImGui.GetIO();
        var sel = ctx.Sel;
        if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
        if (ctx.Hover is { } h)
        {
            if (io.KeyCtrl) sel.Toggle(h);
            else sel.Add(h);
        }
        _frameNext = true;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr viewportDrawList, Vector2 origin)
    {
        var vp = ctx.ViewportSize;
        float margin = Theme.Px(10);
        float width = MathF.Min(MathF.Max(vp.X * 0.56f, Theme.Px(420)), vp.X - margin * 2);
        var size = new Vector2(width, vp.Y - margin * 2);
        if (size.X < Theme.Px(220) || size.Y < Theme.Px(220)) return;
        var pos = new Vector2(origin.X + vp.X - width - margin, origin.Y + margin);

        var faces = TextureOps.Faces(ctx);
        var sizeOf = TextureOps.SizeOf(ctx);
        var info = ctx.Materials.Get(faces.Count > 0 ? faces[0].Face.Material : ctx.ActiveMaterial);
        _aspect = new Vector2(info.Width, info.Height) / MathF.Max(info.Width, info.Height);

        ImGui.SetCursorScreenPos(pos);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.Px(4));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Border);
        if (ImGui.BeginChild("##fastTexture", size, ImGuiChildFlags.Border | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawHeader(ctx, faces);
            DrawToolbar(ctx);
            var avail = ImGui.GetContentRegionAvail();
            float hintsH = ImGui.GetFrameHeightWithSpacing();
            DrawCanvas(ctx, faces, sizeOf, info, new Vector2(avail.X, MathF.Max(avail.Y - hintsH, Theme.Px(80))));
            ImGui.Dummy(Theme.Px(0, 2));
            KeyHints([("Drag", "move"), ("Handles", "scale"), ("Q E", "rotate"), ("[ ]", "grid"), ("F", "frame"), ("R", "reset view")]);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

        HandleKeys(ctx);
    }

    // ---------------------------------------------------------------- panel

    void DrawHeader(EditorContext ctx, List<FaceRef> faces)
    {
        int explicitCount = faces.Count(f => UvSpace.IsExplicit(f.Face));
        Heading("Fast texture", faces.Count == 0 ? "no faces selected" : TextureOps.Plural(faces.Count));
        string source = faces.Count == 0 ? "Click faces in the view to edit their UVs. Shift adds, Ctrl toggles."
            : explicitCount == faces.Count ? "Explicit UVs. Editing here keeps them on the geometry."
            : explicitCount == 0 ? "Projected UVs. Editing here makes them explicit."
            : $"{explicitCount} explicit, {faces.Count - explicitCount} projected. Editing makes them all explicit.";
        ImGui.TextColored(Theme.TextDim, source);
        ImGui.Dummy(Theme.Px(0, 2));
    }

    void DrawToolbar(EditorContext ctx)
    {
        var options = TextureOptions.For(ctx);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Unwrap");
        UnwrapMode[] modes = [UnwrapMode.UseExisting, UnwrapMode.Conforming, UnwrapMode.Square, UnwrapMode.FollowQuads, UnwrapMode.Planar];
        for (int i = 0; i < modes.Length; i++)
        {
            string label = modes[i] == UnwrapMode.UseExisting ? "Existing" : TextureOps.Name(modes[i]);
            if (WrappingButton(label, $"Unwrap: {TextureOps.Name(modes[i]).ToLowerInvariant()}  ({i + 1})")) RunUnwrap(ctx, modes[i]);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Spacing");
        ImGui.SameLine(0, Theme.Px(8));
        QuadSpacing[] spacings = [QuadSpacing.Length, QuadSpacing.Even, QuadSpacing.LengthAverage];
        int picked = Widgets.TextSegments("spacing", spacings.Select(s => (TextureOps.Name(s), $"Follow quads spacing: {TextureOps.Name(s).ToLowerInvariant()}  (Ctrl+{Array.IndexOf(spacings, s) + 1})")).ToArray(),
            Array.IndexOf(spacings, options.Spacing));
        if (picked >= 0) SetSpacing(ctx, spacings[picked]);

        ImGui.Dummy(Theme.Px(0, 1));
        if (Toggle("repeat", Icon.Repeat, "Repeat", "Tile the texture behind the UVs  (B)", _repeat)) _repeat = !_repeat;
        ImGui.SameLine(0, Theme.Px(4));
        if (Toggle("coords", Icon.Coordinates, "Coordinates", "Show UV coordinates at the corners  (C)", _coords)) _coords = !_coords;
        ImGui.SameLine(0, Theme.Px(4));
        if (Toggle("original", Icon.Photo, "Material", "Show the face material; off shows a checker  (T)", _original)) _original = !_original;
        ImGui.SameLine(0, Theme.Px(4));
        if (Toggle("world", Icon.Ruler, "World scale", "Grid and coordinates in metres at 512 px/m  (G)", _worldScale)) _worldScale = !_worldScale;
        ImGui.SameLine(0, Theme.Px(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, $"Grid {GridLabel()}");
        Tip("[ finer, ] coarser");
        ImGui.Dummy(Theme.Px(0, 2));
    }

    /// <summary>A button that moves to the next row when it doesn't fit on this one.</summary>
    static bool WrappingButton(string label, string tip)
    {
        float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        float spacing = Theme.Px(4);
        ImGui.SameLine(0, spacing);
        if (ImGui.GetCursorPosX() + w > ImGui.GetWindowContentRegionMax().X) ImGui.NewLine();
        bool clicked = ImGui.Button(label);
        Tip(tip);
        return clicked;
    }


    // ---------------------------------------------------------------- canvas

    Vector2 Center => (_c0 + _c1) * 0.5f;
    Vector2 ToScreen(Vector2 uv) => Center + (uv - _pan) * _aspect * _zoom;
    Vector2 ToUv(Vector2 screen) => _pan + (screen - Center) / (_aspect * _zoom);

    void DrawCanvas(EditorContext ctx, List<FaceRef> faces, TextureSize sizeOf, MaterialInfo info, Vector2 canvasSize)
    {
        _c0 = ImGui.GetCursorScreenPos();
        _c1 = _c0 + canvasSize;
        ImGui.InvisibleButton("##uvcanvas", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();

        (Vector2 Min, Vector2 Max)? bounds = faces.Count > 0 ? UvSpace.Bounds(faces.SelectMany(f => UvSpace.Get(f, sizeOf))) : null;
        if (_frameNext || _zoom <= 0)
        {
            Frame(bounds);
            _frameNext = false;
        }
        Interact(ctx, faces, sizeOf, info, bounds, hovered);
        var uvs = faces.Select(f => UvSpace.Get(f, sizeOf)).ToList();
        bounds = uvs.Count > 0 ? UvSpace.Bounds(uvs.SelectMany(u => u)) : null;

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(_c0, _c1, true);
        dl.AddRectFilled(_c0, _c1, ImGui.ColorConvertFloat4ToU32(Theme.ViewportBg));
        DrawBackground(ctx, dl, info);
        DrawGrid(dl, info);
        if (bounds is { } b && Ideal(ctx, faces) is { } ideal)
        {
            var size = ideal * UvSpace.PerMetre(info.Width, info.Height, info.TexelsPerMeter);
            var a = ToScreen(b.Min);
            dl.AddRect(a, ToScreen(b.Min + size), Blue, 0, ImDrawFlags.None, Theme.Px(1.5f));
            dl.AddText(Fonts.Small, Fonts.SmallSize, a + Theme.Px(4, 3), Blue, $"1 m is {info.TexelsPerMeter:0.##} px");
        }

        if (bounds is { } box && faces.Count > 0) dl.AddRect(ToScreen(box.Min), ToScreen(box.Max), 0x90FFFFFF);
        uint fill = (Theme.AccentU32 & 0x00FFFFFF) | 0x30000000;
        foreach (var poly in uvs)
        {
            var pts = poly.Select(ToScreen).ToArray();
            if (pts.Length >= 3) dl.AddConvexPolyFilled(ref pts[0], pts.Length, fill);
            if (pts.Length >= 2) dl.AddPolyline(ref pts[0], pts.Length, Theme.AccentU32, ImDrawFlags.Closed, Theme.Px(1.5f));
            foreach (var p in pts) dl.AddCircleFilled(p, Theme.Px(2.5f), Theme.AccentU32);
        }
        if (bounds is { } bb && faces.Count > 0) DrawHandles(dl, bb);
        if (_coords) DrawCoordinates(dl, uvs, info);

        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (faces.Count == 0)
        {
            const string msg = "Select faces to edit their UVs";
            var ts = ImGui.CalcTextSize(msg);
            dl.AddText(Center - ts * 0.5f, ImGui.ColorConvertFloat4ToU32(Theme.Text), msg);
        }
        if (hovered)
        {
            var m = ToUv(ImGui.GetIO().MousePos);
            string readout = _worldScale ? $"{m.X * info.Width / info.TexelsPerMeter:0.00} m, {m.Y * info.Height / info.TexelsPerMeter:0.00} m" : $"u {m.X:0.000}  v {m.Y:0.000}";
            dl.AddText(new Vector2(_c0.X + Theme.Px(8), _c1.Y - ImGui.GetTextLineHeight() - Theme.Px(6)), dim, readout);
        }
        dl.PopClipRect();
        dl.AddRect(_c0, _c1, ImGui.ColorConvertFloat4ToU32(Theme.Border));
    }

    void DrawBackground(EditorContext ctx, ImDrawListPtr dl, MaterialInfo info)
    {
        var uvMin = _repeat ? ToUv(_c0) : Vector2.Zero;
        var uvMax = _repeat ? ToUv(_c1) : Vector2.One;
        if (_original)
            dl.AddImage(ctx.TextureId(info.Name), ToScreen(uvMin), ToScreen(uvMax), uvMin, uvMax);
        else
        {
            // Checker with 8 cells per tile, coarsened so it never draws more than a few thousand cells.
            float cell = 1 / 8f;
            while ((uvMax.X - uvMin.X) * (uvMax.Y - uvMin.Y) / (cell * cell) > 4096) cell *= 2;
            int x0 = (int)MathF.Floor(uvMin.X / cell), x1 = (int)MathF.Ceiling(uvMax.X / cell);
            int y0 = (int)MathF.Floor(uvMin.Y / cell), y1 = (int)MathF.Ceiling(uvMax.Y / cell);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    dl.AddRectFilled(ToScreen(new Vector2(x, y) * cell), ToScreen(new Vector2(x + 1, y + 1) * cell),
                        ((x + y) & 1) == 0 ? 0xFF5A5A5Au : 0xFF3C3C3Cu);
        }
        dl.AddRectFilled(ToScreen(uvMin), ToScreen(uvMax), 0x78000000); // keep the overlays readable
        if (!_repeat) dl.AddRect(ToScreen(Vector2.Zero), ToScreen(Vector2.One), 0x80FFFFFF);
    }

    Vector2 GridStep(MaterialInfo info) => _worldScale
        ? UvSpace.PerMetre(info.Width, info.Height, info.TexelsPerMeter) / (1 << _gridLevel)
        : new Vector2(1f / (1 << _gridLevel));

    Vector2 MajorStep(MaterialInfo info) => _worldScale ? UvSpace.PerMetre(info.Width, info.Height, info.TexelsPerMeter) : Vector2.One;

    string GridLabel()
    {
        int n = 1 << _gridLevel;
        return _worldScale ? Labels.Length(1f / n) : n == 1 ? "1 tile" : $"1/{n} tile";
    }

    void DrawGrid(ImDrawListPtr dl, MaterialInfo info)
    {
        var a = ToUv(_c0);
        var b = ToUv(_c1);
        void Lines(Vector2 step, uint col)
        {
            var px = step * _aspect * _zoom;
            if (px.X >= Theme.Px(6))
                for (float k = MathF.Ceiling(a.X / step.X); k * step.X <= b.X; k++)
                {
                    float x = ToScreen(new Vector2(k * step.X, 0)).X;
                    dl.AddLine(new Vector2(x, _c0.Y), new Vector2(x, _c1.Y), col);
                }
            if (px.Y >= Theme.Px(6))
                for (float k = MathF.Ceiling(a.Y / step.Y); k * step.Y <= b.Y; k++)
                {
                    float y = ToScreen(new Vector2(0, k * step.Y)).Y;
                    dl.AddLine(new Vector2(_c0.X, y), new Vector2(_c1.X, y), col);
                }
        }
        Lines(GridStep(info), 0x28FFFFFF);
        Lines(MajorStep(info), 0x70FFFFFF);
    }

    void DrawCoordinates(ImDrawListPtr dl, List<Vector2[]> uvs, MaterialInfo info)
    {
        var seen = new HashSet<(int, int)>();
        uint col = ImGui.ColorConvertFloat4ToU32(Theme.Text);
        foreach (var poly in uvs)
            foreach (var uv in poly)
            {
                var p = ToScreen(uv);
                if (!seen.Add(((int)(p.X / 4), (int)(p.Y / 4)))) continue;
                string text = _worldScale
                    ? $"{uv.X * info.Width / info.TexelsPerMeter:0.##}, {uv.Y * info.Height / info.TexelsPerMeter:0.##}"
                    : $"{uv.X:0.###}, {uv.Y:0.###}";
                var pos = p + Theme.Px(5, 3);
                var ts = ImGui.CalcTextSize(text) * (Fonts.SmallSize / Fonts.BodySize);
                dl.AddRectFilled(pos - Theme.Px(2, 1), pos + ts + Theme.Px(2, 1), 0xB0000000, Theme.Px(2));
                dl.AddText(Fonts.Small, Fonts.SmallSize, pos, col, text);
            }
    }

    // ---------------------------------------------------------------- handles

    /// <summary>0-3 corners (min,min) (max,min) (max,max) (min,max); 4-7 edge midpoints top, right, bottom, left.</summary>
    static Vector2 HandlePoint((Vector2 Min, Vector2 Max) b, int i)
    {
        var mid = (b.Min + b.Max) * 0.5f;
        return i switch
        {
            0 => b.Min,
            1 => new(b.Max.X, b.Min.Y),
            2 => b.Max,
            3 => new(b.Min.X, b.Max.Y),
            4 => new(mid.X, b.Min.Y),
            5 => new(b.Max.X, mid.Y),
            6 => new(mid.X, b.Max.Y),
            _ => new(b.Min.X, mid.Y),
        };
    }

    static int Opposite(int i) => i < 4 ? (i + 2) % 4 : 4 + (i - 4 + 2) % 4;
    static bool MovesX(int i) => i < 4 || i == 5 || i == 7;
    static bool MovesY(int i) => i < 4 || i == 4 || i == 6;

    bool HandlesVisible((Vector2 Min, Vector2 Max) b)
    {
        var s = ToScreen(b.Max) - ToScreen(b.Min);
        return MathF.Abs(s.X) > Theme.Px(16) && MathF.Abs(s.Y) > Theme.Px(16);
    }

    int HandleAt((Vector2 Min, Vector2 Max) b, Vector2 mouse)
    {
        if (!HandlesVisible(b)) return -1;
        for (int i = 0; i < 8; i++)
            if (Vector2.Distance(ToScreen(HandlePoint(b, i)), mouse) <= Theme.Px(7)) return i;
        return -1;
    }

    void DrawHandles(ImDrawListPtr dl, (Vector2 Min, Vector2 Max) b)
    {
        if (!HandlesVisible(b)) return;
        int hot = _drag == Drag.Scale ? _handle : _drag == Drag.None ? HandleAt(b, ImGui.GetIO().MousePos) : -1;
        float r = Theme.Px(4);
        for (int i = 0; i < 8; i++)
        {
            var p = ToScreen(HandlePoint(b, i));
            dl.AddRectFilled(p - new Vector2(r), p + new Vector2(r), i == hot ? Theme.HoverU32 : 0xFFF0F0F0);
            dl.AddRect(p - new Vector2(r), p + new Vector2(r), 0xFF202020);
        }
    }

    // ---------------------------------------------------------------- input

    void Interact(EditorContext ctx, List<FaceRef> faces, TextureSize sizeOf, MaterialInfo info, (Vector2 Min, Vector2 Max)? bounds, bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;

        if (hovered && io.MouseWheel != 0)
        {
            var before = ToUv(mouse);
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.2f, io.MouseWheel), 8, 200000);
            _pan += before - ToUv(mouse);
        }

        if (_drag == Drag.None && hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsMouseClicked(ImGuiMouseButton.Middle)))
            _drag = Drag.Pan;
        if (_drag == Drag.Pan)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _pan -= io.MouseDelta / (_aspect * _zoom);
            else EndDrag();
            return;
        }

        if (_drag == Drag.None && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && faces.Count > 0 && bounds is { } b)
        {
            _handle = HandleAt(b, mouse);
            _drag = _handle >= 0 ? Drag.Scale : Drag.Move;
            _pressMouse = mouse;
            _moved = false;
            _startBounds = b;
            ctx.Session.Begin(_drag == Drag.Scale ? "Scale UVs" : "Move UVs", ctx.Sel.MeshIds());
            _start.Clear();
            foreach (var f in faces) _start.Add((f.Face, (Vector2[])UvSpace.Bake(f, sizeOf).Clone()));
        }
        if (_drag is not (Drag.Move or Drag.Scale)) return;

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (_moved) ctx.Session.End();
            else ctx.Session.Cancel();
            EndDrag();
            return;
        }
        if (Vector2.Distance(mouse, _pressMouse) > Theme.Px(2)) _moved = true;
        if (!_moved) return;

        bool snap = ctx.Snap && !io.KeyCtrl;
        var step = GridStep(info);
        Vector2 Snap(Vector2 v) => snap ? new Vector2(MathF.Round(v.X / step.X) * step.X, MathF.Round(v.Y / step.Y) * step.Y) : v;

        if (_drag == Drag.Move)
        {
            var d = Snap(ToUv(mouse) - ToUv(_pressMouse));
            foreach (var (face, start) in _start)
                for (int i = 0; i < start.Length; i++) face.UVs![i] = start[i] + d;
        }
        else
        {
            var anchor = HandlePoint(_startBounds, Opposite(_handle));
            var from = HandlePoint(_startBounds, _handle);
            var to = Snap(ToUv(mouse));
            var s = Vector2.One;
            if (MovesX(_handle) && MathF.Abs(from.X - anchor.X) > 1e-6f) s.X = (to.X - anchor.X) / (from.X - anchor.X);
            if (MovesY(_handle) && MathF.Abs(from.Y - anchor.Y) > 1e-6f) s.Y = (to.Y - anchor.Y) / (from.Y - anchor.Y);
            if (io.KeyShift)
            {
                float k = MovesX(_handle) && MovesY(_handle) ? (MathF.Abs(s.X - 1) > MathF.Abs(s.Y - 1) ? s.X : s.Y) : MovesX(_handle) ? s.X : s.Y;
                s = new Vector2(k);
            }
            foreach (var (face, start) in _start)
                for (int i = 0; i < start.Length; i++) face.UVs![i] = anchor + (start[i] - anchor) * s;
        }
        ctx.Session.Touch();
    }

    void EndDrag()
    {
        _drag = Drag.None;
        _handle = -1;
        _start.Clear();
    }

    void HandleKeys(EditorContext ctx)
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || _drag != Drag.None) return;
        bool K(ImGuiKey key, bool ctrl = false, bool shift = false, bool alt = false) =>
            ImGui.IsKeyPressed(key, false) && io.KeyCtrl == ctrl && io.KeyShift == shift && io.KeyAlt == alt;

        if (K(ImGuiKey.Q, shift: true)) { _closeNext = true; return; } // closes next frame, so the global Shift+Q doesn't reopen it
        UnwrapMode[] modes = [UnwrapMode.UseExisting, UnwrapMode.Conforming, UnwrapMode.Square, UnwrapMode.FollowQuads, UnwrapMode.Planar];
        for (int i = 0; i < modes.Length; i++)
            if (K(ImGuiKey._1 + i)) { RunUnwrap(ctx, modes[i]); return; }
        QuadSpacing[] spacings = [QuadSpacing.Length, QuadSpacing.Even, QuadSpacing.LengthAverage];
        for (int i = 0; i < spacings.Length; i++)
            if (K(ImGuiKey._1 + i, ctrl: true)) { SetSpacing(ctx, spacings[i]); return; }

        var size = TextureOps.SizeOf(ctx);
        if (K(ImGuiKey.LeftBracket)) _gridLevel = Math.Min(_gridLevel + 1, MaxGridLevel);
        else if (K(ImGuiKey.RightBracket)) _gridLevel = Math.Max(_gridLevel - 1, 0);
        else if (K(ImGuiKey.F)) _frameNext = true;
        else if (K(ImGuiKey.R)) ResetView();
        else if (K(ImGuiKey.Q)) EditUvs(ctx, "Rotate UVs", f => Justify.Rotate(f, -90, size, treatAsOne: true));
        else if (K(ImGuiKey.E)) EditUvs(ctx, "Rotate UVs", f => Justify.Rotate(f, 90, size, treatAsOne: true));
        else if (K(ImGuiKey.R, alt: true)) EditUvs(ctx, "Flip UVs", f => Justify.Flip(f, horizontal: true, size, treatAsOne: true));
        else if (K(ImGuiKey.T, alt: true)) EditUvs(ctx, "Flip UVs", f => Justify.Flip(f, horizontal: false, size, treatAsOne: true));
        else if (K(ImGuiKey.B)) _repeat = !_repeat;
        else if (K(ImGuiKey.C)) _coords = !_coords;
        else if (K(ImGuiKey.T)) _original = !_original;
        else if (K(ImGuiKey.G)) _worldScale = !_worldScale;
        else
        {
            // Other texture hotkeys (numpad shift/scale/rotate/justify, apply material...) keep working while the tool is open.
            foreach (var c in ctx.Commands.All)
                if (c.Id.StartsWith("texture.", StringComparison.Ordinal) && c.Hotkeys.Any(h => h.IsPressed()))
                {
                    if (c.Enabled) c.Execute();
                    break;
                }
        }
    }

    /// <summary>One undo step over the selected faces with their UVs made explicit first.</summary>
    static void EditUvs(EditorContext ctx, string label, Action<List<FaceRef>> edit)
    {
        var size = TextureOps.SizeOf(ctx);
        TextureOps.Edit(ctx, label, faces =>
        {
            foreach (var f in faces) UvSpace.Bake(f, size);
            edit(faces);
        });
    }

    void RunUnwrap(EditorContext ctx, UnwrapMode mode)
    {
        if (!TextureOps.HasFaces(ctx)) { ctx.Status("Select faces to unwrap"); return; }
        TextureOps.Unwrap(ctx, mode);
        _frameNext = true;
    }

    void SetSpacing(EditorContext ctx, QuadSpacing spacing)
    {
        var options = TextureOptions.For(ctx);
        options.Spacing = spacing;
        if (options.LastUnwrap == UnwrapMode.FollowQuads && TextureOps.HasFaces(ctx)) RunUnwrap(ctx, UnwrapMode.FollowQuads);
        else ctx.Status($"Follow quads spacing: {TextureOps.Name(spacing).ToLowerInvariant()}");
    }

    // ---------------------------------------------------------------- view

    void ResetView()
    {
        _pan = new Vector2(0.5f);
        var size = _c1 - _c0;
        _zoom = MathF.Max(MathF.Min(size.X / _aspect.X, size.Y / _aspect.Y) * 0.8f, 8);
    }

    void Frame((Vector2 Min, Vector2 Max)? bounds)
    {
        if (bounds is not { } b || _c1.X <= _c0.X) { ResetView(); return; }
        var ext = Vector2.Max((b.Max - b.Min) * _aspect, new Vector2(1e-3f));
        var size = _c1 - _c0;
        _pan = (b.Min + b.Max) * 0.5f;
        _zoom = Math.Clamp(MathF.Min(size.X / ext.X, size.Y / ext.Y) * 0.75f, 8, 200000);
    }

    /// <summary>Size in metres the faces would cover unfolded at true scale (a conforming unwrap of the first mesh's faces).</summary>
    Vector2? Ideal(EditorContext ctx, List<FaceRef> faces)
    {
        if (faces.Count == 0) return null;
        int key = HashCode.Combine(faces.Count, faces[0].Mesh.Id, faces[0].Index, faces[^1].Index);
        if (_idealVersion != ctx.Session.Version || _idealKey != key)
        {
            var mesh = faces[0].Mesh;
            var list = faces.Where(f => f.Mesh == mesh).Select(f => f.Index).ToList();
            var layout = Unwrap.Conforming(mesh, list, list[0]);
            var (min, max) = UvSpace.Bounds(layout.Values.SelectMany(v => v));
            _idealMetres = max - min;
            _idealVersion = ctx.Session.Version;
            _idealKey = key;
        }
        return _idealMetres;
    }
}
