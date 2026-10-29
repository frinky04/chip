using System.Numerics;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Viewport;

/// <summary>
/// Camera navigation, independent of the active tool. Perspective: RMB + WASD/QE fly (Shift faster, wheel sets speed),
/// MMB pan, Alt+LMB orbit, wheel dolly toward the cursor. Orthographic: RMB or MMB pan, wheel zooms at the cursor.
/// Mouse bindings with modifiers are resolved before this runs.
/// </summary>
public sealed class Navigator
{
    bool _flying, _panning, _orbiting;
    ImGuiMouseButton _panButton;
    Vector3 _orbitPivot;
    float _panDepth;

    /// <summary>Hides and frees the OS cursor while flying. Set by the host (it owns the input device).</summary>
    public Action<bool> CaptureCursor { get; set; } = _ => { };

    public bool Flying => _flying;
    public bool Active => _flying || _panning || _orbiting;

    public static void RegisterReferences(CommandRegistry commands)
    {
        commands.AddReference("Camera", "RMB + WASD", "fly");
        commands.AddReference("Camera", "Q / E", "down / up while flying");
        commands.AddReference("Camera", "Shift", "fly faster");
        commands.AddReference("Camera", "Scroll", "dolly; fly speed while flying");
        commands.AddReference("Camera", "Alt + LMB", "orbit");
        commands.AddReference("Camera", "MMB", "pan");
    }

    /// <summary>Runs one frame. Returns true while a navigation gesture owns the mouse (tools should not update).</summary>
    public bool Update(EditorContext ctx, float dt)
    {
        var io = ImGui.GetIO();
        var cam = ctx.Camera;
        bool wasActive = Active;
        bool navigating = cam.IsOrtho ? UpdateOrtho(ctx, cam, io) : UpdatePerspective(ctx, cam, io, dt);
        // RMB/MMB don't focus an ImGui window automatically. Release text inputs as soon
        // as navigation starts, including wheel zoom/dolly, so movement keys can't edit them.
        if (!wasActive && (Active || (ctx.ViewportHovered && io.MouseWheel != 0)))
            ImGui.SetWindowFocus();
        return navigating;
    }

    bool UpdateOrtho(EditorContext ctx, Camera cam, ImGuiIOPtr io)
    {
        bool hovered = ctx.ViewportHovered;
        if (hovered && !_panning)
            foreach (var b in new[] { ImGuiMouseButton.Right, ImGuiMouseButton.Middle })
                if (ImGui.IsMouseClicked(b)) { _panning = true; _panButton = b; break; }
        if (_panning)
        {
            if (!ImGui.IsMouseDown(_panButton)) _panning = false;
            else
            {
                cam.Pan(io.MouseDelta, cam.UnitsPerPixel(0, ctx.ViewportSize.Y));
                return true;
            }
        }
        if (hovered && io.MouseWheel != 0)
        {
            var ray = ctx.MouseRay();
            cam.Zoom(MathF.Pow(0.85f, io.MouseWheel), ray.Origin);
        }
        return false;
    }

    bool UpdatePerspective(EditorContext ctx, Camera cam, ImGuiIOPtr io, float dt)
    {
        bool hovered = ctx.ViewportHovered;

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { _flying = true; CaptureCursor(true); }
        if (_flying)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Right)) { _flying = false; CaptureCursor(false); }
            else
            {
                cam.Rotate(io.MouseDelta.X * 0.0032f, -io.MouseDelta.Y * 0.0032f);
                var move = Vector3.Zero;
                if (ImGui.IsKeyDown(ImGuiKey.W)) move += cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.S)) move -= cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.D)) move += cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.A)) move -= cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitY;
                if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitY;
                if (io.MouseWheel != 0) cam.FlySpeed = Math.Clamp(cam.FlySpeed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 0.5f, 256);
                if (move != Vector3.Zero) cam.Position += Vector3.Normalize(move) * cam.FlySpeed * (io.KeyShift ? 3f : 1f) * dt;
                return true;
            }
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            _panning = true;
            _panButton = ImGuiMouseButton.Middle;
            _panDepth = ctx.RaycastFace()?.Distance ?? 8;
        }
        if (_panning)
        {
            if (!ImGui.IsMouseDown(_panButton)) _panning = false;
            else
            {
                cam.Pan(io.MouseDelta, cam.UnitsPerPixel(_panDepth, ctx.ViewportSize.Y));
                return true;
            }
        }

        // Alt+LMB orbits; Ctrl+Alt+LMB is the select tool's box select (Unreal's marquee), so it's left alone.
        if (hovered && io.KeyAlt && !io.KeyCtrl && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _orbiting = true;
            _orbitPivot = ctx.SelectionPivot() ?? ctx.RaycastFace()?.Point ?? FocusPivot(cam) ?? cam.Position + cam.Forward * 8;
        }
        if (_orbiting)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _orbiting = false;
            else
            {
                cam.Orbit(_orbitPivot, io.MouseDelta.X * 0.006f, -io.MouseDelta.Y * 0.006f);
                return true;
            }
        }

        if (hovered && io.MouseWheel != 0)
        {
            var ray = ctx.MouseRay();
            float dist = ctx.RaycastFace()?.Distance ?? 8;
            cam.Position += ray.Dir * io.MouseWheel * MathF.Max(dist * 0.15f, 0.1f);
        }
        return false;
    }

    /// <summary>The last focus point, if it is still in front of the camera and not absurdly far.</summary>
    static Vector3? FocusPivot(Camera cam) =>
        cam.FocusTarget is { } t && Vector3.Dot(t - cam.Position, cam.Forward) > 0.1f && Vector3.Distance(t, cam.Position) < 200 ? t : null;
}
