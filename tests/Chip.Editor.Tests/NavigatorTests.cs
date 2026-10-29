using System.Numerics;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Tests;

/// <summary>Real ImGui input/focus behavior, without a window or GL renderer.</summary>
public sealed class NavigatorTests : IDisposable
{
    readonly nint _imgui = ImGui.CreateContext();
    string _name = "Original";

    public unsafe NavigatorTests()
    {
        var io = ImGui.GetIO();
        io.NativePtr->IniFilename = null;
        io.DisplaySize = new Vector2(800, 600);
        io.DeltaTime = 1f / 60;
        io.ConfigInputTrickleEventQueue = false;
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height);
    }

    void Frame(Action viewport, bool focusText = false, bool hideMouse = false)
    {
        ImGui.NewFrame();
        var mousePos = ImGui.GetIO().MousePos;
        if (hideMouse) ImGui.GetIO().MousePos = new Vector2(-float.MaxValue);
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(300, 600));
        ImGui.Begin("Scene");
        if (focusText) ImGui.SetKeyboardFocusHere();
        ImGui.InputText("Rename", ref _name, 128);
        ImGui.End();
        ImGui.SetNextWindowPos(new Vector2(300, 0));
        ImGui.SetNextWindowSize(new Vector2(500, 600));
        ImGui.Begin("Viewport", ImGuiWindowFlags.NoFocusOnAppearing);
        viewport();
        ImGui.End();
        if (hideMouse) ImGui.GetIO().MousePos = mousePos;
        ImGui.Render();
    }

    [Theory]
    [InlineData(false, 1, false, false)] // perspective fly
    [InlineData(false, 2, false, false)] // perspective pan
    [InlineData(false, 0, true, false)] // orbit
    [InlineData(true, 1, false, false)] // orthographic RMB pan
    [InlineData(true, 2, false, false)] // orthographic MMB pan
    [InlineData(false, -1, false, true)] // dolly
    [InlineData(true, -1, false, true)] // zoom
    public void Navigation_releases_rename_focus(bool ortho, int button, bool alt, bool wheel)
    {
        var ctx = RegistryTests.Headless();
        ctx.Camera.Projection = ortho ? Projection.Top : Projection.Perspective;
        ctx.ViewportSize = new Vector2(500, 600);
        ctx.ViewportHovered = true;
        ctx.Mouse = ctx.ViewportSize * 0.5f;
        var nav = new Navigator();
        Frame(() => { });
        Frame(() => { }, focusText: true);
        Frame(() => { }); // keyboard focus is applied on the following frame
        Assert.True(ImGui.IsAnyItemActive());

        var io = ImGui.GetIO();
        io.AddMousePosEvent(500, 300);
        if (button >= 0) io.AddMouseButtonEvent(button, true);
        if (alt) io.AddKeyEvent(ImGuiKey.ModAlt, true);
        if (wheel) io.AddMouseWheelEvent(0, 1);
        Frame(() =>
        {
            nav.Update(ctx, 1f / 60);
            Assert.True(ImGui.IsWindowFocused());
        });

        io.AddInputCharacter('w');
        Frame(() => nav.Update(ctx, 1f / 60));
        Assert.Equal("Original", _name);
    }

    [Fact]
    public void NoMouse_suppresses_panel_hover_but_preserves_navigation_input()
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(100, 100);
        bool hovered = false;
        Frame(() => { });
        Frame(() => hovered = ImGui.IsWindowHovered());
        io.AddMousePosEvent(500, 300);
        Frame(() => hovered = ImGui.IsWindowHovered());
        Assert.True(hovered);

        io.ConfigFlags |= ImGuiConfigFlags.NoMouse;
        io.AddMousePosEvent(510, 305);
        io.AddMouseButtonEvent(1, true);
        io.AddKeyEvent(ImGuiKey.W, true);
        Frame(() =>
        {
            Assert.False(ImGui.IsWindowHovered());
            Assert.False(ImGui.IsMouseHoveringRect(new Vector2(300, 0), new Vector2(800, 600)));
            Assert.Equal(new Vector2(10, 5), io.MouseDelta);
            Assert.True(ImGui.IsMouseDown(ImGuiMouseButton.Right));
            Assert.True(ImGui.IsKeyDown(ImGuiKey.W));
        }, hideMouse: true);

        io.AddMousePosEvent(520, 310);
        Frame(() => Assert.Equal(new Vector2(10, 5), io.MouseDelta), hideMouse: true);

        io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse;
        io.AddMouseButtonEvent(1, false);
        Frame(() => Assert.True(ImGui.IsWindowHovered()));
    }

    public void Dispose() => ImGui.DestroyContext(_imgui);
}
