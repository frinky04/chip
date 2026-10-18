using System.Numerics;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Viewport;

/// <summary>
/// Camera navigation, independent of the active tool: RMB + WASD/QE fly (Shift faster, wheel sets speed),
/// MMB pan, Alt+LMB orbit, wheel dolly toward the cursor. Mouse bindings with modifiers are resolved before this runs.
/// </summary>
public sealed class Navigator
{
    bool _flying, _panning, _orbiting;
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
            _panDepth = ctx.RaycastFace()?.Distance ?? 8;
        }
        if (_panning)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _panning = false;
            else
            {
                float upp = cam.UnitsPerPixel(_panDepth, ctx.ViewportSize.Y);
                cam.Position += (-cam.Right * io.MouseDelta.X + cam.Up * io.MouseDelta.Y) * upp;
                return true;
            }
        }

        if (hovered && io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _orbiting = true;
            _orbitPivot = ctx.SelectionPivot() ?? ctx.RaycastFace()?.Point ?? cam.Position + cam.Forward * 8;
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
}
