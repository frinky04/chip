using System.Collections.Concurrent;
using System.Diagnostics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using StbImageSharp;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Small preview textures for the browser and inspector, made on demand so a 500+ texture library stays responsive.
/// Only thumbnails that are drawn get requested. Small files decode on the UI thread within a per-frame budget (so they
/// appear at once); larger ones decode and downscale on two worker threads, newest request first, and requests that
/// scrolled out of view are dropped. Uploads go through <see cref="EditorContext.UploadTexture"/>, a few per frame.
/// </summary>
public sealed class ThumbnailCache
{
    const int MaxSize = 192;               // px; the largest browser tile at 1.5x UI scale
    const long SyncMaxBytes = 256 * 1024;  // smaller files decode on the UI thread
    const double SyncBudgetMs = 4;
    const int UploadsPerFrame = 8;
    const int Workers = 2;
    const int StaleFrames = 30;            // a queued request not drawn for this long is dropped

    sealed class Entry(string path, DateTime stamp)
    {
        public readonly string Path = path;
        public DateTime Stamp = stamp;
        public nint Tex;
        public bool Queued, Failed;
        public volatile int LastSeen;
    }

    readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentStack<Entry> _todo = new();
    readonly ConcurrentQueue<(Entry Entry, int W, int H, byte[]? Rgba)> _done = new();
    readonly Stopwatch _frameClock = new();
    int _running;
    volatile int _frame;

    /// <summary>Thumbnails waiting to decode or upload (for status text).</summary>
    public int Pending => _byPath.Values.Count(e => e.Queued);

    /// <summary>Once per frame: uploads finished thumbnails and keeps the workers busy.</summary>
    public void Pump(EditorContext ctx)
    {
        _frame++;
        _frameClock.Restart();
        for (int i = 0; i < UploadsPerFrame && _done.TryDequeue(out var r); i++) Finish(ctx, r.Entry, r.W, r.H, r.Rgba);
        while (!_todo.IsEmpty && Volatile.Read(ref _running) < Workers)
        {
            Interlocked.Increment(ref _running);
            Task.Run(Work);
        }
    }

    /// <summary>The material's thumbnail, or 0 while it loads. Materials without a file use the renderer's texture.</summary>
    public nint Get(EditorContext ctx, MaterialInfo m)
    {
        if (m.Path == null) return ctx.TextureId(m.Name);
        if (!_byPath.TryGetValue(m.Path, out var e))
        {
            _byPath[m.Path] = e = new Entry(m.Path, Stamp(m.Path));
        }
        e.LastSeen = _frame;
        if (e.Tex != 0) return e.Tex;
        if (e.Failed) return ctx.TextureId(m.Name); // the renderer shows its missing checker
        if (!e.Queued) Request(ctx, e);
        return 0;
    }

    /// <summary>After a rescan: thumbnails of files that changed on disk are rebuilt.</summary>
    public void Refresh()
    {
        foreach (var e in _byPath.Values)
        {
            var stamp = Stamp(e.Path);
            if (stamp == e.Stamp) continue;
            e.Stamp = stamp;
            e.Tex = 0;
            e.Failed = false;
        }
    }

    void Request(EditorContext ctx, Entry e)
    {
        e.Queued = true;
        long size = 0;
        try { size = new FileInfo(e.Path).Length; } catch (IOException) { }
        if (size > 0 && size <= SyncMaxBytes && _frameClock.Elapsed.TotalMilliseconds < SyncBudgetMs)
        {
            var (w, h, rgba) = Decode(e.Path);
            Finish(ctx, e, w, h, rgba);
            return;
        }
        _todo.Push(e);
    }

    void Work()
    {
        try
        {
            while (_todo.TryPop(out var e))
            {
                if (_frame - e.LastSeen > StaleFrames) { _done.Enqueue((e, 0, 0, null)); continue; } // dropped; re-requested when drawn
                var (w, h, rgba) = Decode(e.Path);
                _done.Enqueue((e, w, h, rgba ?? []));
            }
        }
        finally { Interlocked.Decrement(ref _running); }
    }

    static void Finish(EditorContext ctx, Entry e, int w, int h, byte[]? rgba)
    {
        e.Queued = false;
        if (rgba == null) return;                 // stale: try again next time it is drawn
        if (rgba.Length == 0) { e.Failed = true; return; }
        e.Tex = ctx.UploadTexture(w, h, rgba);
        if (e.Tex == 0) e.Failed = true;          // headless: fall back to the renderer
    }

    /// <summary>Decodes and box-downscales to at most <see cref="MaxSize"/> px. Returns an empty array on failure.</summary>
    static (int W, int H, byte[] Rgba) Decode(string path)
    {
        try
        {
            var img = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            return Downscale(img.Width, img.Height, img.Data);
        }
        catch (Exception) { return (0, 0, []); } // unreadable or unsupported: the tile shows the missing checker
    }

    static (int, int, byte[]) Downscale(int w, int h, byte[] src)
    {
        int f = (Math.Max(w, h) + MaxSize - 1) / MaxSize;
        if (f <= 1) return (w, h, src);
        int nw = Math.Max(1, w / f), nh = Math.Max(1, h / f);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    int row = (y * f + dy) * w;
                    for (int dx = 0; dx < f; dx++)
                    {
                        int i = (row + x * f + dx) * 4;
                        r += src[i]; g += src[i + 1]; b += src[i + 2]; a += src[i + 3];
                    }
                }
                int n = f * f, o = (y * nw + x) * 4;
                dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(b / n); dst[o + 3] = (byte)(a / n);
            }
        return (nw, nh, dst);
    }

    static DateTime Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch (IOException) { return default; }
    }
}
