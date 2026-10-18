using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.UI;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Chip.Editor.Rendering;

/// <summary>
/// Draws the document into an offscreen MSAA target and returns the resolved texture for ImGui.
/// Read-only over the document: scene buffers rebuild when <see cref="Session.Version"/> changes; overlays rebuild per frame.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    const int Samples = 4;
    static readonly Vector4 WireColor = new(0.06f, 0.06f, 0.07f, 0.55f);
    static readonly Vector4 Accent = Theme.Accent, HoverCol = Theme.Hover;
    static readonly Vector4 Backing = new(0.05f, 0.05f, 0.06f, 0.9f);

    readonly GL _gl;
    readonly MaterialLibrary _lib;
    readonly uint _meshProg, _colorProg, _lineProg, _gridProg;
    readonly uint _sceneVao, _sceneVbo, _wireVao, _wireVbo, _overlayVao, _overlayVbo, _gridVao, _gridVbo;
    readonly Dictionary<string, uint> _textures = new(StringComparer.OrdinalIgnoreCase);
    readonly uint _missingTex;

    uint _msFbo, _msColor, _msDepth, _resolveFbo, _resolveTex;
    Vector2 _fboSize;
    int _builtVersion = -1;
    Document? _builtDoc;
    readonly List<(string Material, int First, int Count)> _batches = new();
    int _wireCount;

    public Renderer(GL gl, MaterialLibrary lib)
    {
        _gl = gl;
        _lib = lib;
        _meshProg = Program(Shaders.MeshVs, null, Shaders.MeshFs);
        _colorProg = Program(Shaders.ColorVs, null, Shaders.ColorFs);
        _lineProg = Program(Shaders.ColorVs, Shaders.LineGs, Shaders.LineFs);
        _gridProg = Program(Shaders.GridVs, null, Shaders.GridFs);

        (_sceneVao, _sceneVbo) = VertexArray(8, [3, 3, 2]);
        (_wireVao, _wireVbo) = VertexArray(7, [3, 4]);
        (_overlayVao, _overlayVbo) = VertexArray(7, [3, 4]);
        (_gridVao, _gridVbo) = VertexArray(3, [3]);
        const float g = 1000; // metres
        Upload(_gridVbo, [-g, 0, -g, g, 0, -g, g, 0, g, -g, 0, -g, g, 0, g, -g, 0, g], BufferUsageARB.StaticDraw);

        _missingTex = Checker();
        foreach (var m in lib.All)
            if (m.Path != null) _textures[m.Name] = LoadTexture(m.Path);
    }

    public nint TextureId(string material) => (nint)_textures.GetValueOrDefault(material, _missingTex);

    /// <summary>Renders one frame and returns the resolved color texture (GL origin, flip V when displaying).</summary>
    /// <param name="backfaces">Draw back faces (darkened) so backface selection shows what it can pick.</param>
    public uint Render(Vector2 size, ViewInfo view, Session session, ElementRef? hover, float grid, bool backfaces)
    {
        if (size.X < 1 || size.Y < 1) return _resolveTex;
        EnsureTargets(size);
        if (session.Version != _builtVersion || !ReferenceEquals(session.Doc, _builtDoc)) RebuildScene(session.Doc, session.Version);
        var overlay = BuildOverlay(session, hover);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        var bg = Theme.ViewportBg;
        _gl.ClearColor(bg.X, bg.Y, bg.Z, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        if (backfaces) _gl.Disable(EnableCap.CullFace);
        else _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.Disable(EnableCap.Blend);

        var vp = view.ViewProj;
        _gl.UseProgram(_meshProg);
        SetMatrix(_meshProg, "uViewProj", vp);
        _gl.Uniform1(Loc(_meshProg, "uTex"), 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(_sceneVao);
        foreach (var (mat, first, count) in _batches)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _textures.GetValueOrDefault(mat, _missingTex));
            _gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
        }

        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        _gl.UseProgram(_gridProg);
        SetMatrix(_gridProg, "uViewProj", vp);
        _gl.Uniform1(Loc(_gridProg, "uGrid"), grid);
        _gl.Uniform3(Loc(_gridProg, "uEye"), view.Eye);
        _gl.BindVertexArray(_gridVao);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(2, 2); // coplanar floors win over the grid
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.Disable(EnableCap.PolygonOffsetFill);

        SetColorUniforms(_lineProg, view, 0.0015f);
        _gl.Uniform2(Loc(_lineProg, "uViewport"), size);
        DrawLines(_wireVao, 0, _wireCount, 1.0f, 1);

        // Overlay: tinted faces, then lines/points twice: depth-tested, then a faint x-ray pass.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _overlayVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, overlay.Data.ToArray(), BufferUsageARB.StreamDraw);
        SetColorUniforms(_colorProg, view, 0.002f);
        _gl.BindVertexArray(_overlayVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)overlay.TriCount);

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1) _gl.Disable(EnableCap.DepthTest);
            float alpha = pass == 0 ? 1f : 0.22f;
            SetColorUniforms(_lineProg, view, 0.002f);
            _gl.Uniform2(Loc(_lineProg, "uViewport"), size);
            foreach (var (first, count, width) in overlay.Lines) DrawLines(_overlayVao, first, count, width, alpha);
            SetColorUniforms(_colorProg, view, 0.002f);
            _gl.Uniform1(Loc(_colorProg, "uAlpha"), alpha);
            _gl.Enable(EnableCap.ProgramPointSize);
            foreach (var (first, count, px) in overlay.Points)
            {
                _gl.Uniform1(Loc(_colorProg, "uPointSize"), px * Theme.Scale);
                _gl.DrawArrays(PrimitiveType.Points, first, (uint)count);
            }
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        _gl.BlitFramebuffer(0, 0, (int)size.X, (int)size.Y, 0, 0, (int)size.X, (int)size.Y, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.BindVertexArray(0);
        return _resolveTex;
    }

    void DrawLines(uint vao, int first, int count, float width, float alpha)
    {
        if (count == 0) return;
        _gl.UseProgram(_lineProg);
        _gl.Uniform1(Loc(_lineProg, "uWidth"), width * Theme.Scale);
        _gl.Uniform1(Loc(_lineProg, "uAlpha"), alpha);
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Lines, first, (uint)count);
    }

    void SetColorUniforms(uint prog, ViewInfo view, float pull)
    {
        _gl.UseProgram(prog);
        SetMatrix(prog, "uViewProj", view.ViewProj);
        _gl.Uniform3(Loc(prog, "uEye"), view.Eye);
        _gl.Uniform1(Loc(prog, "uPull"), pull);
        _gl.Uniform1(Loc(prog, "uPointSize"), 1f);
        _gl.Uniform1(Loc(prog, "uAlpha"), 1f);
    }

    void RebuildScene(Document doc, int version)
    {
        _builtDoc = doc;
        _builtVersion = version;
        var byMaterial = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
        var wire = new List<float>();
        var tri = new List<int>();
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden) continue;
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
                    list.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, uv.X, uv.Y]);
                }
            }
            foreach (var e in mesh.Edges()) { Vertex(wire, mesh.Positions[e.A], WireColor); Vertex(wire, mesh.Positions[e.B], WireColor); }
        }

        _batches.Clear();
        var all = new List<float>();
        foreach (var (mat, data) in byMaterial)
        {
            _batches.Add((mat, all.Count / 8, data.Count / 8));
            all.AddRange(data);
        }
        Upload(_sceneVbo, all.ToArray(), BufferUsageARB.DynamicDraw);
        Upload(_wireVbo, wire.ToArray(), BufferUsageARB.DynamicDraw);
        _wireCount = wire.Count / 7;
    }

    sealed class Overlay
    {
        public readonly List<float> Data = new();
        public int TriCount;
        public readonly List<(int First, int Count, float Width)> Lines = new();
        public readonly List<(int First, int Count, float Px)> Points = new();
        public int VertexCount => Data.Count / 7;
    }

    static Overlay BuildOverlay(Session session, ElementRef? hover)
    {
        var o = new Overlay();
        var doc = session.Doc;
        var sel = session.Sel;
        var tri = new List<int>();

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
        return o;
    }

    static void Vertex(List<float> d, Vector3 p, Vector4 c) => d.AddRange([p.X, p.Y, p.Z, c.X, c.Y, c.Z, c.W]);

    void EnsureTargets(Vector2 size)
    {
        if (size == _fboSize) return;
        _fboSize = size;
        uint w = (uint)size.X, h = (uint)size.Y;
        if (_msFbo == 0)
        {
            _msFbo = _gl.GenFramebuffer();
            _msColor = _gl.GenRenderbuffer();
            _msDepth = _gl.GenRenderbuffer();
            _resolveFbo = _gl.GenFramebuffer();
            _resolveTex = _gl.GenTexture();
        }
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msColor);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, Samples, InternalFormat.Rgba8, w, h);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msDepth);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, Samples, InternalFormat.Depth24Stencil8, w, h);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msColor);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _msDepth);

        _gl.BindTexture(TextureTarget.Texture2D, _resolveTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _resolveTex, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    uint LoadTexture(string path)
    {
        using var stream = File.OpenRead(path);
        var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return CreateTexture(img.Width, img.Height, img.Data);
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

    int Loc(uint prog, string name) => _gl.GetUniformLocation(prog, name);

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
        foreach (var t in _textures.Values) _gl.DeleteTexture(t);
        _gl.DeleteTexture(_missingTex);
        _gl.DeleteTexture(_resolveTex);
        _gl.DeleteFramebuffer(_msFbo);
        _gl.DeleteFramebuffer(_resolveFbo);
        _gl.DeleteRenderbuffer(_msColor);
        _gl.DeleteRenderbuffer(_msDepth);
        foreach (var p in new[] { _meshProg, _colorProg, _lineProg, _gridProg }) _gl.DeleteProgram(p);
        foreach (var b in new[] { _sceneVbo, _wireVbo, _overlayVbo, _gridVbo }) _gl.DeleteBuffer(b);
        foreach (var a in new[] { _sceneVao, _wireVao, _overlayVao, _gridVao }) _gl.DeleteVertexArray(a);
    }
}
