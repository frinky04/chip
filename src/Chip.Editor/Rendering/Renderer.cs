using System.Numerics;
using System.Runtime.InteropServices;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Chip.Editor.Rendering;

/// <summary>A plane to draw a construction grid on (the hovered face while a creation tool is active).</summary>
public readonly record struct GridPlane(Vector3 Origin, Vector3 Normal, Vector3 Focus);

/// <summary>Per-pane drawing settings.</summary>
public readonly record struct RenderSettings(ViewMode Mode, float Grid, bool BoundaryEdges = true, GridPlane? Construction = null,
    bool TranslucentTools = true);

/// <summary>
/// Draws the document into a pane's offscreen MSAA target. Read-only over the document.
/// Each mesh has its own GPU buffers, rebuilt only when its session mesh revision changes (checked when <see cref="Session.Version"/>
/// moves); overlays (selection, hover) are built once per frame and shared by every pane.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    static readonly Vector4 WireColor = new(0.06f, 0.06f, 0.07f, 0.55f);
    static readonly Vector4 WireframeColor = new(0.78f, 0.80f, 0.84f, 0.85f);
    /// <summary>Open (boundary) edges: dashed cyan, distinct from selection orange and hover yellow.</summary>
    static readonly Vector4 BoundaryColor = new(0.30f, 0.85f, 1.0f, 0.95f);
    /// <summary>Soft (smoothed) edges: faint outside edge mode so curved shapes read as surfaces (stream L).</summary>
    static readonly Vector4 SoftWireColor = WireColor with { W = 0.1f };
    /// <summary>Soft edges in edge mode: light and dotted, so smoothing is visible while picking edges (stream L).</summary>
    static readonly Vector4 SoftEdgeModeColor = new(0.86f, 0.88f, 0.92f, 0.75f);
    /// <summary>"Show normals" overlay lines (stream L): lavender, apart from selection orange, hover yellow and boundary cyan.</summary>
    static readonly Vector4 NormalColor = new(0.72f, 0.56f, 1.0f, 0.95f);
    static readonly Vector4 Accent = Theme.Accent, HoverCol = Theme.Hover;
    static readonly Vector4 Backing = new(0.05f, 0.05f, 0.06f, 0.9f);
    static readonly Vector4 White = Vector4.One;

    sealed class MeshGpu
    {
        public int Revision;
        public float[] Vertices = [], Wires = [];
        public uint Vao, Vbo, WireVao, WireVbo;
        /// <summary>One draw per material; the centre sorts see-through batches back to front.</summary>
        public readonly List<(string Material, int First, int Count, Vector3 Center)> Batches = new();
        /// <summary>Wire buffer layout: [hard interior edges][soft interior edges][boundary edges].</summary>
        public int EdgeVerts, SoftVerts, BoundaryVerts;
    }

    readonly GL _gl;
    readonly MaterialLibrary _lib;
    readonly uint _meshProg, _colorProg, _lineProg, _gridProg;
    readonly uint _overlayVao, _overlayVbo, _gridVao, _gridVbo;
    readonly Dictionary<string, (string? Path, uint Tex, AlphaMode Alpha)> _textures = new(StringComparer.OrdinalIgnoreCase); // loaded on first use
    readonly List<uint> _uploads = new();
    readonly uint _missingTex;
    readonly Dictionary<int, MeshGpu> _meshes = new();
    readonly Dictionary<uint, Dictionary<string, int>> _uniforms = new();

    int _builtVersion = -1, _builtLibVersion = -1;
    Document? _builtDoc;
    Session? _builtSession;
    /// <summary>The dressed level drawn as solid geometry this frame (null: the blockout). Wires and overlays stay on the blockout.</summary>
    IMeshSet? _scene, _builtScene;
    Overlay _overlay = new();
    bool _edgeMode;
    readonly FrameCache<Overlay> _overlayCache = new();
    ElementRef? _overlayHover;
    bool _overlayNormals;
    IReadOnlyList<FaceTint>? _overlayTints;

    /// <summary>Reference models (pos + colour, lighting baked in) and their hard edges, rebuilt when entities or files change.</summary>
    readonly uint _refVao, _refVbo, _refWireVao, _refWireVbo;
    int _refTriVerts, _refWireVerts, _refSelectedWireFirst, _refRevision = -1;
    readonly FrameCache<int> _refCache = new();
    /// <summary>Reference models read as clay, apart from every dev texture and from the orange selection.</summary>
    static readonly Vector4 Clay = Theme.Rgb(0xA3B1C2);
    static readonly Vector3 ClayLight = Vector3.Normalize(new Vector3(0.35f, 0.9f, 0.25f));

    /// <summary>Meshes re-uploaded by the last scene sync (diagnostics and tests of the dirty tracking).</summary>
    public int LastRebuildCount { get; private set; }

    public Renderer(GL gl, MaterialLibrary lib)
    {
        _gl = gl;
        _lib = lib;
        _meshProg = Program(Shaders.MeshVs, null, Shaders.MeshFs);
        _colorProg = Program(Shaders.ColorVs, null, Shaders.ColorFs);
        _lineProg = Program(Shaders.ColorVs, Shaders.LineGs, Shaders.LineFs);
        _gridProg = Program(Shaders.GridVs, null, Shaders.GridFs);

        (_overlayVao, _overlayVbo) = VertexArray(7, [3, 4]);
        (_gridVao, _gridVbo) = VertexArray(3, [3]);
        (_refVao, _refVbo) = VertexArray(7, [3, 4]);
        (_refWireVao, _refWireVbo) = VertexArray(7, [3, 4]);
        Upload(_gridVbo, [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, 1, 1, 0, -1, 1, 0], BufferUsageARB.StaticDraw);

        _missingTex = Checker();
    }

    public nint TextureId(string material) => (nint)EnsureTexture(material);

    /// <summary>
    /// The material's GL texture, loaded on first use (large libraries never load unused textures) and reloaded when a
    /// rescan points the name at another file. Unreadable or unknown materials get the missing checker.
    /// </summary>
    public uint EnsureTexture(string material)
    {
        var path = _lib.Get(material).Path;
        // A surface without an image file gets a tile drawn from its look (the browser shows it; the viewport draws the
        // pattern in world space instead).
        var look = path == null ? Surfaces.Of(material)?.Look : null;
        string? key = path ?? (look != null ? $"look:{look}" : null);
        if (_textures.TryGetValue(material, out var t) && t.Path == key) return t.Tex;
        if (t.Tex != 0 && t.Tex != _missingTex) _gl.DeleteTexture(t.Tex);
        uint tex = _missingTex;
        var alpha = AlphaMode.Opaque;
        if (path != null)
            try { (tex, alpha) = LoadTexture(path); }
            catch (Exception) { } // corrupt or unsupported image: keep the checker so the scene still draws
        else if (look != null) tex = SurfaceTile(look);
        _textures[material] = (key, tex, alpha);
        return tex;
    }

    /// <summary>How the material's alpha draws: its override, else what its texture's pixels showed when loaded.</summary>
    AlphaMode AlphaOf(string material)
    {
        EnsureTexture(material);
        return _lib.Get(material).Alpha ?? _textures[material].Alpha;
    }

    enum MeshPass { Opaque, Cutout, Translucent }

    /// <summary>
    /// Which pass draws a material. Textures with alpha are cut out or see-through (the UV checker draws them solid);
    /// tool surfaces (they don't render in the game) are see-through with <paramref name="translucentTools"/> on.
    /// </summary>
    MeshPass PassOf(string mat, Surface? surface, ViewMode mode, bool translucentTools)
    {
        if (translucentTools && surface is { IsTool: true }) return MeshPass.Translucent;
        if (mode == ViewMode.UvChecker || surface?.Look is { Image: null }) return MeshPass.Opaque;
        return AlphaOf(mat) switch
        {
            AlphaMode.Mask => MeshPass.Cutout,
            AlphaMode.Blend => MeshPass.Translucent,
            _ => MeshPass.Opaque,
        };
    }

    /// <summary>
    /// One pass over the scene's mesh batches, in scene order. Ordinary materials sample their texture; surfaces draw
    /// their look (a world-space pattern, or their image through the UVs).
    /// </summary>
    void DrawMeshes(Document doc, MeshPass pass, RenderSettings s)
    {
        string? bound = null;
        foreach (var mesh in _scene?.Meshes ?? doc.Meshes)
        {
            if (mesh.Hidden || !_meshes.TryGetValue(mesh.Id, out var gpu) || gpu.Batches.Count == 0) continue;
            _gl.BindVertexArray(gpu.Vao);
            foreach (var (mat, first, count, _) in gpu.Batches)
            {
                var surface = Surfaces.Of(mat);
                if (PassOf(mat, surface, s.Mode, s.TranslucentTools) != pass) continue;
                if (!string.Equals(bound, mat, StringComparison.OrdinalIgnoreCase)) BindMaterial(mat, surface, pass);
                bound = mat;
                _gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
            }
        }
    }

    readonly List<(float Distance, uint Vao, string Material, Surface? Surface, int First, int Count)> _translucent = new();

    /// <summary>See-through batches, farthest first so nearer ones blend over them (per material batch, not per face).</summary>
    void DrawTranslucent(Document doc, RenderSettings s, Vector3 eye)
    {
        _translucent.Clear();
        foreach (var mesh in _scene?.Meshes ?? doc.Meshes)
        {
            if (mesh.Hidden || !_meshes.TryGetValue(mesh.Id, out var gpu)) continue;
            foreach (var (mat, first, count, center) in gpu.Batches)
            {
                var surface = Surfaces.Of(mat);
                if (PassOf(mat, surface, s.Mode, s.TranslucentTools) == MeshPass.Translucent)
                    _translucent.Add((Vector3.DistanceSquared(center, eye), gpu.Vao, mat, surface, first, count));
            }
        }
        if (_translucent.Count == 0) return;
        _translucent.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false); // depth-tested, not depth-written, so they never hide what's behind them
        foreach (var (_, vao, mat, surface, first, count) in _translucent)
        {
            _gl.BindVertexArray(vao);
            BindMaterial(mat, surface, MeshPass.Translucent);
            _gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
        }
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    void BindMaterial(string mat, Surface? surface, MeshPass pass)
    {
        var info = _lib.Get(mat);
        _gl.Uniform2(Loc(_meshProg, "uTexSize"), new Vector2(info.Width, info.Height));
        _gl.Uniform1(Loc(_meshProg, "uDensity"), info.TexelsPerMeter); // UV checker: the target this texture should hit
        _gl.BindTexture(TextureTarget.Texture2D, EnsureTexture(mat));
        var look = surface?.Look;
        bool tool = surface is { IsTool: true } && pass == MeshPass.Translucent;
        _gl.Uniform1(Loc(_meshProg, "uSurface"), look is { Image: null } ? 1 : 0);
        _gl.Uniform1(Loc(_meshProg, "uPattern"), (int)(look?.Pattern ?? SurfacePattern.Solid));
        _gl.Uniform3(Loc(_meshProg, "uSurfColor"), look?.Color ?? Vector3.One);
        _gl.Uniform1(Loc(_meshProg, "uAlpha"), tool ? look!.Opacity : 1f);
        // Translucent tools fade as a whole; a see-through texture also multiplies in its own alpha.
        int alphaMode = pass switch { MeshPass.Cutout => 1, MeshPass.Translucent when !tool => 2, _ => 0 };
        _gl.Uniform1(Loc(_meshProg, "uAlphaMode"), alphaMode);
    }

    /// <summary>A 64 px tile of a surface's pattern, for the material browser.</summary>
    uint SurfaceTile(SurfaceLook look)
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool dark = look.Pattern switch
                {
                    SurfacePattern.Stripes => (x + y) % 32 >= 16,
                    SurfacePattern.Checker => (x / 16 + y / 16) % 2 == 1,
                    _ => false,
                };
                var c = dark ? look.Color * 0.55f : look.Color;
                int i = (y * n + x) * 4;
                px[i] = (byte)(c.X * 255); px[i + 1] = (byte)(c.Y * 255); px[i + 2] = (byte)(c.Z * 255); px[i + 3] = 255;
            }
        return CreateTexture(n, n, px);
    }

    /// <summary>Uploads top-down RGBA8 pixels as a mipmapped texture owned by the renderer (thumbnails).</summary>
    public nint UploadTexture(int width, int height, byte[] rgba)
    {
        uint tex = CreateTexture(width, height, rgba);
        _uploads.Add(tex);
        return (nint)tex;
    }

    readonly List<RenderTarget> _targets = new();

    /// <summary>The offscreen target for pane <paramref name="index"/>, created on first use and owned by the renderer.</summary>
    public RenderTarget Target(int index)
    {
        while (_targets.Count <= index) _targets.Add(new RenderTarget(_gl));
        return _targets[index];
    }

    /// <summary>
    /// Once per frame, before any pane renders: syncs mesh buffers with the document and builds the shared overlay.
    /// <paramref name="showNormals"/> adds short corner-normal lines for the selection (View > Show normals).
    /// </summary>
    /// <param name="tints">
    /// Blockout faces to tint (dressing: proxy stripes, the rule under the mouse, tag colours). Pass the same list object
    /// while nothing changed: a new list rebuilds the overlay.
    /// </param>
    public void BeginFrame(Session session, ElementRef? hover, bool showNormals = false, IMeshSet? scene = null,
        IReadOnlyList<FaceTint>? tints = null)
    {
        LastRebuildCount = 0;
        _scene = scene;
        if (!ReferenceEquals(session, _builtSession) || session.Version != _builtVersion || !ReferenceEquals(session.Doc, _builtDoc)
            || !ReferenceEquals(scene, _builtScene) || _lib.Version != _builtLibVersion)
            SyncScene(session, scene);
        _edgeMode = session.Sel.Mode == SelectMode.Edge;
        if (!_refCache.IsCurrent(session) || _refRevision != ReferenceModels.DrawRevision) SyncReferences(session);
        if (_overlayCache.IsCurrent(session) && hover == _overlayHover && showNormals == _overlayNormals && ReferenceEquals(tints, _overlayTints)) return;
        _overlay = BuildOverlay(session, hover, showNormals, tints, _overlay);
        _overlayCache.Store(session, _overlay);
        _overlayHover = hover;
        _overlayNormals = showNormals;
        _overlayTints = tints;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _overlayVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(_overlay.Data), BufferUsageARB.StreamDraw);
    }

    /// <summary>Renders one pane and returns its resolved colour texture (GL origin, flip V when displaying).</summary>
    public uint Render(RenderTarget target, ViewInfo view, Camera camera, Document doc, RenderSettings s)
    {
        var size = view.Size;
        if (size.X < 1 || size.Y < 1) return target.Texture;
        target.Ensure(size);
        bool ortho = view.IsOrthographic;
        // Overlay geometry is pulled toward the eye to win depth ties; ortho depth is linear, so pull a fixed few cm.
        float Pull(float perspective) => ortho ? perspective * 10f / Camera.OrthoDepth : perspective;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.MsFbo);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        var bg = Theme.ViewportBg;
        _gl.ClearColor(bg.X, bg.Y, bg.Z, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        var vp = view.ViewProj;

        // 2D views: the grid is the background, in the view plane, behind everything.
        if (ortho)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            var (u, v, cu, cv) = OrthoGridAxes(camera.Projection);
            float upp = camera.OrthoHeight / size.Y;
            float minor = s.Grid;
            while (minor / upp < 6 && minor < 4096) minor = Framework.EditorContext.MajorGrid(minor);
            DrawGrid(view, camera.Position, u, v, cu, cv, minor, camera.OrthoHeight * 4 + 50, fade: 1, Vector3.Zero, 0, strength: 1.5f);
        }

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        // Always cull: backface mode only changes what picking can hit, never what's drawn.
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        if (s.Mode != ViewMode.Wireframe)
        {
            _gl.UseProgram(_meshProg);
            SetMatrix(_meshProg, "uViewProj", vp);
            _gl.Uniform1(Loc(_meshProg, "uTex"), 0);
            _gl.Uniform1(Loc(_meshProg, "uMode"), (int)s.Mode);
            _gl.ActiveTexture(TextureUnit.Texture0);
            // The pane's alpha must stay 1 (the UI composites it with blending), whatever the faces write.
            _gl.ColorMask(true, true, true, false);
            // Opaque faces first; then cut-outs, whose alpha becomes MSAA coverage for smooth edges without sorting;
            // then see-through faces (textures with soft alpha, and tool surfaces with View > Translucent tool surfaces).
            DrawMeshes(doc, MeshPass.Opaque, s);
            _gl.Enable(EnableCap.SampleAlphaToCoverage);
            DrawMeshes(doc, MeshPass.Cutout, s);
            _gl.Disable(EnableCap.SampleAlphaToCoverage);
            DrawTranslucent(doc, s, view.Eye);
            _gl.ColorMask(true, true, true, true);
            if (_refTriVerts > 0)
            {
                // Imported files don't promise consistent winding; reference models draw from both sides.
                _gl.Disable(EnableCap.CullFace);
                SetColorUniforms(_colorProg, view, 0);
                _gl.BindVertexArray(_refVao);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_refTriVerts);
            }
        }

        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        if (!ortho)
        {
            var eye = view.Eye;
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(2, 2); // coplanar floors win over the grid
            DrawGrid(view, new Vector3(eye.X, 0, eye.Z), Vector3.UnitX, Vector3.UnitZ, Theme.Rgb(0xE5484D), Theme.Rgb(0x3E8EED), s.Grid, 1000, fade: 0, Vector3.Zero, 0, strength: 1);
            _gl.Disable(EnableCap.PolygonOffsetFill);
        }
        if (s.Construction is { } cp) DrawConstructionGrid(view, cp, s.Grid);

        // Scene edges: interior edges solid, soft edges faint (dotted in edge mode), open (boundary) edges dashed.
        SetColorUniforms(_lineProg, view, Pull(0.0015f));
        _gl.Uniform2(Loc(_lineProg, "uViewport"), size);
        _lineState = null;
        bool wire = s.Mode == ViewMode.Wireframe;
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden || !_meshes.TryGetValue(mesh.Id, out var gpu)) continue;
            DrawLines(gpu.WireVao, 0, gpu.EdgeVerts, wire ? 1.25f : 1.0f, 1, wire ? WireframeColor : WireColor);
            if (_edgeMode) DrawLines(gpu.WireVao, gpu.EdgeVerts, gpu.SoftVerts, 1.25f, 1, SoftEdgeModeColor, dash: 3 * Theme.Scale);
            else DrawLines(gpu.WireVao, gpu.EdgeVerts, gpu.SoftVerts, 1.0f, 1, wire ? WireframeColor with { W = 0.35f } : SoftWireColor);
            int boundaryFirst = gpu.EdgeVerts + gpu.SoftVerts;
            if (s.BoundaryEdges) DrawLines(gpu.WireVao, boundaryFirst, gpu.BoundaryVerts, 1.75f, 1, BoundaryColor, dash: 7 * Theme.Scale);
            else DrawLines(gpu.WireVao, boundaryFirst, gpu.BoundaryVerts, wire ? 1.25f : 1.0f, 1, wire ? WireframeColor : WireColor);
        }
        DrawLines(_refWireVao, 0, _refWireVerts, wire ? 1.25f : 1.0f, 1, wire ? Clay : WireColor);
        if (_refWireVerts > _refSelectedWireFirst)
        {
            // A selected reference's hidden edges, dashed: a door set into a wall still shows the outline of its hole.
            _gl.DepthFunc(DepthFunction.Greater);
            DrawLines(_refWireVao, _refSelectedWireFirst, _refWireVerts - _refSelectedWireFirst, 1.25f, 0.8f, Accent, dash: 4 * Theme.Scale);
            _gl.DepthFunc(DepthFunction.Lequal);
        }

        // Overlay: tinted faces, then lines/points twice: depth-tested, then a faint x-ray pass.
        var o = _overlay;
        SetColorUniforms(_colorProg, view, Pull(0.002f));
        _gl.BindVertexArray(_overlayVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)o.TriCount);

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1) _gl.Disable(EnableCap.DepthTest);
            float alpha = pass == 0 ? 1f : 0.22f;
            SetColorUniforms(_lineProg, view, Pull(0.002f));
            _gl.Uniform2(Loc(_lineProg, "uViewport"), size);
            foreach (var (first, count, width) in o.Lines) DrawLines(_overlayVao, first, count, width, alpha, White);
            SetColorUniforms(_colorProg, view, Pull(0.002f));
            _gl.Uniform1(Loc(_colorProg, "uAlpha"), alpha);
            _gl.Enable(EnableCap.ProgramPointSize);
            _gl.BindVertexArray(_overlayVao);
            foreach (var (first, count, px) in o.Points)
            {
                _gl.Uniform1(Loc(_colorProg, "uPointSize"), px * Theme.Scale);
                _gl.DrawArrays(PrimitiveType.Points, first, (uint)count);
            }
        }

        target.Resolve();
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.BindVertexArray(0);
        return target.Texture;
    }

    /// <summary>Grid axes for a 2D view and the colours of the world axes they show (X red, Y green, Z blue).</summary>
    static (Vector3 U, Vector3 V, Vector4 ColU, Vector4 ColV) OrthoGridAxes(Projection p)
    {
        Vector4 x = Theme.Rgb(0xE5484D), y = Theme.Rgb(0x5BB35B), z = Theme.Rgb(0x3E8EED);
        return p switch
        {
            Projection.Top => (Vector3.UnitX, Vector3.UnitZ, x, z),
            Projection.Front => (Vector3.UnitX, Vector3.UnitY, x, y),
            _ => (Vector3.UnitZ, Vector3.UnitY, z, y),
        };
    }

    /// <summary>A local grid on the hovered face's axis-aligned plane, drawn over the surface and fading out around the cursor.</summary>
    void DrawConstructionGrid(ViewInfo view, GridPlane cp, float grid)
    {
        var n = TextureMapping.DominantAxis(cp.Normal);
        var u = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
        var v = Vector3.Normalize(Vector3.Cross(n, u));
        float radius = Math.Clamp(grid * 12, 1.25f, 12f);
        var center = cp.Focus - n * Vector3.Dot(cp.Focus - cp.Origin, n);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1, -2); // sits on top of the face it follows
        DrawGrid(view, center, u, v, Vector4.Zero, Vector4.Zero, grid, radius, fade: 2, center, radius, strength: 3.5f, origin: cp.Origin);
        _gl.Disable(EnableCap.PolygonOffsetFill);
    }

    /// <param name="fade">0 = by distance from the eye, 1 = none (2D views), 2 = radial around <paramref name="fadeCenter"/>.</param>
    void DrawGrid(ViewInfo view, Vector3 center, Vector3 u, Vector3 v, Vector4 colU, Vector4 colV, float grid, float extent,
        int fade, Vector3 fadeCenter, float fadeRadius, float strength, Vector3? origin = null)
    {
        var p = _gridProg;
        _gl.UseProgram(p);
        SetMatrix(p, "uViewProj", view.ViewProj);
        _gl.Uniform3(Loc(p, "uCenter"), center);
        _gl.Uniform3(Loc(p, "uOrigin"), origin ?? Vector3.Zero);
        _gl.Uniform3(Loc(p, "uU"), u);
        _gl.Uniform3(Loc(p, "uV"), v);
        _gl.Uniform1(Loc(p, "uExtent"), extent);
        _gl.Uniform1(Loc(p, "uGrid"), grid);
        float major = Framework.EditorContext.MajorGrid(grid);
        _gl.Uniform1(Loc(p, "uMajor"), major);
        _gl.Uniform1(Loc(p, "uMajor2"), Framework.EditorContext.MajorGrid(major));
        _gl.Uniform4(Loc(p, "uColU"), colU);
        _gl.Uniform4(Loc(p, "uColV"), colV);
        _gl.Uniform3(Loc(p, "uEye"), view.Eye);
        _gl.Uniform1(Loc(p, "uFade"), fade);
        _gl.Uniform3(Loc(p, "uFadeCenter"), fadeCenter);
        _gl.Uniform1(Loc(p, "uFadeRadius"), fadeRadius);
        _gl.Uniform1(Loc(p, "uStrength"), strength);
        _gl.BindVertexArray(_gridVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    (float Width, float Alpha, Vector4 Tint, float Dash)? _lineState;

    void DrawLines(uint vao, int first, int count, float width, float alpha, Vector4 tint, float dash = 0)
    {
        if (count == 0) return;
        var state = (width, alpha, tint, dash);
        if (_lineState != state)
        {
            _gl.Uniform1(Loc(_lineProg, "uWidth"), width * Theme.Scale);
            _gl.Uniform1(Loc(_lineProg, "uAlpha"), alpha);
            _gl.Uniform4(Loc(_lineProg, "uTint"), tint);
            _gl.Uniform1(Loc(_lineProg, "uDash"), dash);
            _lineState = state;
        }
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Lines, first, (uint)count);
    }

    void SetColorUniforms(uint prog, ViewInfo view, float pull)
    {
        _lineState = null;
        _gl.UseProgram(prog);
        SetMatrix(prog, "uViewProj", view.ViewProj);
        _gl.Uniform3(Loc(prog, "uEye"), view.Eye);
        _gl.Uniform1(Loc(prog, "uPull"), pull);
        _gl.Uniform1(Loc(prog, "uPointSize"), 1f);
        _gl.Uniform1(Loc(prog, "uAlpha"), 1f);
    }

    // ---- Reference models ----

    /// <summary>
    /// Every visible reference model as flat-shaded clay in world space (selected ones tinted toward the accent), plus
    /// their hard edges, the selected ones' last so their hidden edges can be drawn too. A few hundred triangles each, so
    /// one rebuild per change is cheap even mid-drag.
    /// </summary>
    void SyncReferences(Session session)
    {
        var tris = new List<float>();
        var wires = new List<float>();
        var selectedWires = new List<float>();
        var loop = new List<int>();
        var sel = session.Sel;
        foreach (var e in session.Doc.Entities)
        {
            if (e.Hidden || ReferenceModels.Get(e).Model is not { } model) continue;
            var mesh = model.Mesh;
            var world = EntityOps.World(e);
            var rotation = EntityOps.Orientation(e);
            bool selected = sel.Mode == SelectMode.Object && sel.Items.Contains(ElementRef.Object(e.Id));
            var tone = selected ? Vector4.Lerp(Clay, Accent, 0.4f) : Clay;
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var n = Vector3.Transform(mesh.FaceNormal(f), rotation);
                float shade = 0.62f + 0.38f * Vector3.Dot(n, ClayLight); // wrapped, so faces turned away stay readable
                var c = new Vector4(tone.X * shade, tone.Y * shade, tone.Z * shade, 1);
                loop.Clear();
                Triangulator.Triangulate(mesh, f, loop);
                var verts = mesh.Faces[f].Verts;
                foreach (var i in loop) Vertex(tris, Vector3.Transform(mesh.Positions[verts[i]], world), c);
            }
            var into = selected ? selectedWires : wires;
            foreach (var edge in mesh.Edges())
            {
                if (mesh.SoftEdges.Contains(edge)) continue;
                Vertex(into, Vector3.Transform(mesh.Positions[edge.A], world), White);
                Vertex(into, Vector3.Transform(mesh.Positions[edge.B], world), White);
            }
        }
        _refSelectedWireFirst = wires.Count / 7;
        wires.AddRange(selectedWires);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _refVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(tris), BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _refWireVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(wires), BufferUsageARB.DynamicDraw);
        _refTriVerts = tris.Count / 7;
        _refWireVerts = wires.Count / 7;
        _refCache.Store(session, 0);
        _refRevision = ReferenceModels.DrawRevision; // read after loading: a first load bumps it
    }

    // ---- Scene buffers (per mesh, dirty-tracked) ----

    void SyncScene(Session session, IMeshSet? scene)
    {
        var doc = session.Doc;
        int version = session.Version;
        // UVs depend on material sizes: a library change (a rescan, the game's surfaces arriving) rebuilds every mesh.
        bool sameSession = ReferenceEquals(_builtSession, session) && _builtLibVersion == _lib.Version;
        _builtLibVersion = _lib.Version;
        _builtSession = session;
        _builtDoc = doc;
        _builtVersion = version;
        _builtScene = scene;
        var dirty = new List<(PolyMesh Mesh, MeshGpu Gpu)>();
        var live = new HashSet<int>();
        // Blockout meshes (always: wires and overlays draw from them), then dressing's generated meshes (negative ids).
        var meshes = scene == null ? doc.Meshes.Select(m => (m, session.MeshRevision(m)))
            : doc.Meshes.Select(m => (m, session.MeshRevision(m))).Concat(scene.Meshes.Where(m => m.Id < 0).Select(m => (m, scene.Revision(m))));
        foreach (var (mesh, revision) in meshes)
        {
            live.Add(mesh.Id);
            if (_meshes.TryGetValue(mesh.Id, out var gpu) && sameSession && gpu.Revision == revision) continue;
            if (gpu == null)
            {
                gpu = new MeshGpu();
                (gpu.Vao, gpu.Vbo) = VertexArray(8, [3, 3, 2]);
                (gpu.WireVao, gpu.WireVbo) = VertexArray(7, [3, 4]);
                _meshes[mesh.Id] = gpu;
            }
            gpu.Revision = revision;
            dirty.Add((mesh, gpu));
        }
        foreach (var id in _meshes.Keys.Where(id => !live.Contains(id)).ToList())
        {
            var gpu = _meshes[id];
            _gl.DeleteBuffer(gpu.Vbo);
            _gl.DeleteBuffer(gpu.WireVbo);
            _gl.DeleteVertexArray(gpu.Vao);
            _gl.DeleteVertexArray(gpu.WireVao);
            _meshes.Remove(id);
        }
        // CPU geometry generation is independent per mesh. Only uploads touch the GL context.
        if (dirty.Count > 1) Parallel.For(0, dirty.Count, i => BuildMesh(dirty[i].Mesh, dirty[i].Gpu));
        else if (dirty.Count == 1) BuildMesh(dirty[0].Mesh, dirty[0].Gpu);
        foreach (var (_, gpu) in dirty)
        {
            Upload(gpu.Vbo, gpu.Vertices, BufferUsageARB.DynamicDraw);
            Upload(gpu.WireVbo, gpu.Wires, BufferUsageARB.DynamicDraw);
            gpu.Vertices = []; gpu.Wires = [];
        }
        LastRebuildCount = dirty.Count;
    }

    void BuildMesh(PolyMesh mesh, MeshGpu gpu)
    {
        var byMaterial = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var tri = new List<int>();
        var normals = Topology.CornerNormals(mesh);
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            var info = _lib.Get(face.Material);
            if (!byMaterial.TryGetValue(face.Material, out var list)) byMaterial[face.Material] = list = new List<float>();
            tri.Clear();
            Triangulator.Triangulate(mesh, f, tri);
            foreach (var i in tri)
            {
                var p = mesh.Positions[face.Verts[i]];
                var n = normals[f][i];
                var uv = face.UV(i, p, info.Width, info.Height);
                list.Add(p.X); list.Add(p.Y); list.Add(p.Z);
                list.Add(n.X); list.Add(n.Y); list.Add(n.Z); list.Add(uv.X); list.Add(uv.Y);
                sums[face.Material] = sums.GetValueOrDefault(face.Material) + p;
            }
        }
        gpu.Batches.Clear();
        var all = new List<float>();
        foreach (var (mat, data) in byMaterial)
        {
            int verts = data.Count / 8;
            gpu.Batches.Add((mat, all.Count / 8, verts, verts > 0 ? sums[mat] / verts : Vector3.Zero));
            all.AddRange(data);
        }
        gpu.Vertices = all.ToArray();

        var boundary = mesh.BoundaryEdges();
        var wire = new List<float>();
        var interior = mesh.Edges().Where(e => !boundary.Contains(e)).ToList();
        foreach (var e in interior)
            if (!mesh.SoftEdges.Contains(e)) { Vertex(wire, mesh.Positions[e.A], White); Vertex(wire, mesh.Positions[e.B], White); }
        gpu.EdgeVerts = wire.Count / 7;
        foreach (var e in interior)
            if (mesh.SoftEdges.Contains(e)) { Vertex(wire, mesh.Positions[e.A], White); Vertex(wire, mesh.Positions[e.B], White); }
        gpu.SoftVerts = wire.Count / 7 - gpu.EdgeVerts;
        foreach (var e in boundary) { Vertex(wire, mesh.Positions[e.A], White); Vertex(wire, mesh.Positions[e.B], White); }
        gpu.BoundaryVerts = wire.Count / 7 - gpu.EdgeVerts - gpu.SoftVerts;
        gpu.Wires = wire.ToArray();
    }

    sealed class Overlay
    {
        public readonly List<float> Data = new();
        public int TriCount;
        public readonly List<int> Triangles = new();
        public readonly List<(int First, int Count, float Width)> Lines = new();
        public readonly List<(int First, int Count, float Px)> Points = new();
        public int VertexCount => Data.Count / 7;
    }

    static Overlay BuildOverlay(Session session, ElementRef? hover, bool showNormals, IReadOnlyList<FaceTint>? tints, Overlay o)
    {
        o.Data.Clear(); o.Lines.Clear(); o.Points.Clear();
        var doc = session.Doc;
        var sel = session.Sel;
        var tri = o.Triangles;

        void Tint(PolyMesh m, int f, Vector4 c)
        {
            tri.Clear();
            Triangulator.Triangulate(m, f, tri);
            foreach (var i in tri) Vertex(o.Data, m.Positions[m.Faces[f].Verts[i]], c);
        }

        // Triangles first so they occupy [0, TriCount).
        foreach (var e in sel.Items)
        {
            if (doc.Find(e.Mesh) is not { } m) continue;
            if (sel.Mode == SelectMode.Face && e.A < m.Faces.Count) Tint(m, e.A, Accent with { W = 0.32f });
            if (sel.Mode == SelectMode.Object) for (int f = 0; f < m.Faces.Count; f++) Tint(m, f, Accent with { W = 0.10f });
        }
        if (hover is { } h && sel.Mode == SelectMode.Face && doc.Find(h.Mesh) is { } hm && h.A < hm.Faces.Count)
            Tint(hm, h.A, HoverCol with { W = 0.18f });
        if (tints != null)
        {
            PolyMesh? tm = null;
            foreach (var t in tints)
            {
                if (tm?.Id != t.Mesh) tm = doc.Find(t.Mesh);
                if (tm is not { Hidden: false }) continue;
                var c = t.Striped ? t.Color with { W = -t.Color.W } : t.Color; // negative alpha: striped
                if (t.Face < 0) for (int f = 0; f < tm.Faces.Count; f++) Tint(tm, f, c);
                else if (t.Face < tm.Faces.Count) Tint(tm, t.Face, c);
            }
        }
        o.TriCount = o.VertexCount;

        void Lines(IEnumerable<(Vector3, Vector3)> segs, Vector4 c, float width)
        {
            int start = o.VertexCount;
            foreach (var (a, b) in segs) { Vertex(o.Data, a, c); Vertex(o.Data, b, c); }
            if (o.VertexCount > start) o.Lines.Add((start, o.VertexCount - start, width));
        }

        void Points(IEnumerable<Vector3> pts, Vector4 c, float px)
        {
            int start = o.VertexCount;
            foreach (var p in pts) Vertex(o.Data, p, c);
            if (o.VertexCount > start) o.Points.Add((start, o.VertexCount - start, px));
        }

        IEnumerable<(Vector3, Vector3)> ElementEdges(ElementRef e, SelectMode mode)
        {
            if (doc.Find(e.Mesh) is not { } m) yield break;
            switch (mode)
            {
                case SelectMode.Object:
                    foreach (var ed in m.Edges()) yield return (m.Positions[ed.A], m.Positions[ed.B]);
                    break;
                case SelectMode.Face when e.A < m.Faces.Count:
                    foreach (var ed in m.Faces[e.A].Edges()) yield return (m.Positions[ed.A], m.Positions[ed.B]);
                    break;
                case SelectMode.Edge when e.B < m.Positions.Count:
                    yield return (m.Positions[e.A], m.Positions[e.B]);
                    break;
            }
        }

        if (sel.Mode == SelectMode.Vertex)
            Points(doc.Meshes.Where(m => !m.Hidden).SelectMany(m => m.Positions), new Vector4(0.85f, 0.85f, 0.85f, 0.9f), 5);
        // Selection and hover get a dark backing stroke so they read on any texture, including orange dev materials.
        void Outlined(IEnumerable<(Vector3, Vector3)> segs, Vector4 c, float width)
        {
            var list = segs.ToList();
            Lines(list, Backing, width + 3);
            Lines(list, c, width);
        }
        void OutlinedPoints(IEnumerable<Vector3> pts, Vector4 c, float px)
        {
            var list = pts.ToList();
            Points(list, Backing, px + 4);
            Points(list, c, px);
        }

        if (hover is { } hv)
        {
            if (sel.Mode == SelectMode.Vertex && doc.Find(hv.Mesh) is { } vm && hv.A < vm.Positions.Count)
                OutlinedPoints([vm.Positions[hv.A]], HoverCol, 9);
            else Outlined(ElementEdges(hv, sel.Mode), HoverCol, sel.Mode == SelectMode.Edge ? 3f : 1.5f);
        }
        Outlined(sel.Items.SelectMany(e => ElementEdges(e, sel.Mode)), Accent, sel.Mode == SelectMode.Edge ? 3f : 2f);
        if (sel.Mode == SelectMode.Vertex)
            OutlinedPoints(sel.Items.Select(e => doc.Find(e.Mesh) is { } m && e.A < m.Positions.Count ? m.Positions[e.A] : (Vector3?)null)
                .Where(p => p.HasValue).Select(p => p!.Value), Accent, 8);
        if (showNormals) Lines(NormalLines(session), NormalColor, 1.5f);
        return o;
    }

    /// <summary>
    /// Short corner-normal strokes for the faces the selection touches (objects: every face). Each starts slightly inside
    /// its face, so split normals at a hard vertex read as separate strokes. Length follows the face size.
    /// </summary>
    static List<(Vector3, Vector3)> NormalLines(Session session)
    {
        var segs = new List<(Vector3, Vector3)>();
        var sel = session.Sel;
        foreach (var group in sel.Items.GroupBy(i => i.Mesh))
        {
            if (session.Doc.Find(group.Key) is not { } m || m.Hidden) continue;
            var faces = sel.Mode switch
            {
                SelectMode.Face => group.Select(i => i.A).Where(f => f >= 0 && f < m.Faces.Count).Distinct().ToList(),
                SelectMode.Object => Enumerable.Range(0, m.Faces.Count).ToList(),
                _ => FacesTouching(m, group.SelectMany(i => sel.Mode == SelectMode.Edge ? new[] { i.A, i.B } : [i.A]).ToHashSet()),
            };
            var normals = Topology.CornerNormals(m);
            foreach (var f in faces)
            {
                var c = m.FaceCenter(f);
                float len = Math.Clamp(MathF.Sqrt(Topology.FaceArea(m, f)) * 0.35f, 0.05f, 0.5f);
                var verts = m.Faces[f].Verts;
                for (int i = 0; i < verts.Length; i++)
                {
                    var p = Vector3.Lerp(m.Positions[verts[i]], c, 0.08f);
                    segs.Add((p, p + normals[f][i] * len));
                }
            }
        }
        return segs;
    }

    static List<int> FacesTouching(PolyMesh m, HashSet<int> verts) =>
        Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].Verts.Any(verts.Contains)).ToList();

    static void Vertex(List<float> d, Vector3 p, Vector4 c)
    {
        d.Add(p.X); d.Add(p.Y); d.Add(p.Z);
        d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
    }

    (uint Tex, AlphaMode Alpha) LoadTexture(string path)
    {
        ImageResult img;
        using (var stream = File.OpenRead(path)) img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var alpha = _lib.DetectAlpha(path, img.Data, img.Width, img.Height);
        if (alpha != AlphaMode.Opaque) MaterialAlpha.Bleed(img.Data, img.Width, img.Height); // no halo from colour hidden under alpha 0
        return (CreateTexture(img.Width, img.Height, img.Data), alpha);
    }

    uint Checker()
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool on = ((x / 8) + (y / 8)) % 2 == 0;
                int i = (y * n + x) * 4;
                px[i] = (byte)(on ? 255 : 0); px[i + 1] = 0; px[i + 2] = (byte)(on ? 255 : 0); px[i + 3] = 255;
            }
        return CreateTexture(n, n, px);
    }

    uint CreateTexture(int w, int h, byte[] rgba)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 8f); // anisotropy where supported
        _gl.GetError(); // ignore if anisotropic filtering is unavailable
        return tex;
    }

    (uint Vao, uint Vbo) VertexArray(int stride, int[] sizes)
    {
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        int offset = 0;
        for (uint i = 0; i < sizes.Length; i++)
        {
            _gl.EnableVertexAttribArray(i);
            _gl.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)(offset * sizeof(float)));
            offset += sizes[i];
        }
        _gl.BindVertexArray(0);
        return (vao, vbo);
    }

    void Upload(uint vbo, float[] data, BufferUsageARB usage)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, usage);
    }

    /// <summary>Uniform locations are looked up once per program (four panes × many draws per frame).</summary>
    int Loc(uint prog, string name)
    {
        if (!_uniforms.TryGetValue(prog, out var map)) _uniforms[prog] = map = new Dictionary<string, int>();
        if (!map.TryGetValue(name, out int loc)) map[name] = loc = _gl.GetUniformLocation(prog, name);
        return loc;
    }

    void SetMatrix(uint prog, string name, Matrix4x4 m) => _gl.UniformMatrix4(Loc(prog, name), 1, false, (float*)&m);

    uint Program(string vs, string? gs, string fs)
    {
        uint Compile(ShaderType type, string src)
        {
            uint s = _gl.CreateShader(type);
            _gl.ShaderSource(s, src);
            _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint p = _gl.CreateProgram();
        var shaders = new List<uint> { Compile(ShaderType.VertexShader, vs), Compile(ShaderType.FragmentShader, fs) };
        if (gs != null) shaders.Add(Compile(ShaderType.GeometryShader, gs));
        foreach (var s in shaders) _gl.AttachShader(p, s);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException($"Program link failed: {_gl.GetProgramInfoLog(p)}");
        foreach (var s in shaders) { _gl.DetachShader(p, s); _gl.DeleteShader(s); }
        return p;
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values.Select(v => v.Tex).Concat(_uploads).Distinct().Where(t => t != _missingTex)) _gl.DeleteTexture(t);
        _gl.DeleteTexture(_missingTex);
        foreach (var gpu in _meshes.Values)
        {
            _gl.DeleteBuffer(gpu.Vbo);
            _gl.DeleteBuffer(gpu.WireVbo);
            _gl.DeleteVertexArray(gpu.Vao);
            _gl.DeleteVertexArray(gpu.WireVao);
        }
        _meshes.Clear();
        foreach (var t in _targets) t.Dispose();
        foreach (var p in new[] { _meshProg, _colorProg, _lineProg, _gridProg }) _gl.DeleteProgram(p);
        foreach (var b in new[] { _overlayVbo, _gridVbo, _refVbo, _refWireVbo }) _gl.DeleteBuffer(b);
        foreach (var a in new[] { _overlayVao, _gridVao, _refVao, _refWireVao }) _gl.DeleteVertexArray(a);
    }
}
