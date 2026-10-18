using System.Numerics;
using Silk.NET.OpenGL;

namespace Chip.Editor.Rendering;

/// <summary>One pane's offscreen MSAA colour/depth buffers and the resolved texture ImGui displays.</summary>
public sealed unsafe class RenderTarget : IDisposable
{
    public const int Samples = 4;
    readonly GL _gl;
    uint _msColor, _msDepth, _resolveFbo;

    public RenderTarget(GL gl) => _gl = gl;

    public uint MsFbo { get; private set; }
    public uint Texture { get; private set; }
    public Vector2 Size { get; private set; }

    public void Ensure(Vector2 size)
    {
        if (size == Size) return;
        Size = size;
        uint w = (uint)size.X, h = (uint)size.Y;
        if (MsFbo == 0)
        {
            MsFbo = _gl.GenFramebuffer();
            _msColor = _gl.GenRenderbuffer();
            _msDepth = _gl.GenRenderbuffer();
            _resolveFbo = _gl.GenFramebuffer();
            Texture = _gl.GenTexture();
        }
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msColor);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, Samples, InternalFormat.Rgba8, w, h);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msDepth);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, Samples, InternalFormat.Depth24Stencil8, w, h);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, MsFbo);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msColor);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _msDepth);

        _gl.BindTexture(TextureTarget.Texture2D, Texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Texture, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Resolves the multisampled image into <see cref="Texture"/>.</summary>
    public void Resolve()
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, MsFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        int w = (int)Size.X, h = (int)Size.Y;
        _gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        if (MsFbo == 0) return;
        _gl.DeleteTexture(Texture);
        _gl.DeleteFramebuffer(MsFbo);
        _gl.DeleteFramebuffer(_resolveFbo);
        _gl.DeleteRenderbuffer(_msColor);
        _gl.DeleteRenderbuffer(_msDepth);
        MsFbo = 0;
    }
}
