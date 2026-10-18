# Chip

Standalone level-blockout editor in the spirit of Scythe / Source 2 Hammer. C# (.NET 10), Silk.NET + OpenGL 3.3, ImGui.NET. Exports `.glb` for Godot (or anything else that reads glTF).

```
dotnet run --project src/Chip.Editor          # editor
dotnet test                                    # core + scenario tests
Chip.exe --shot level.chip out.png faces      # headless UI capture (empty|objects|faces|block|backfaces)
```

Everything is in metres. Materials are the PNGs in `assets/textures`, and texel density is 512 px/m: a 512 px texture tiles every 1 m and a 128 px one every 25 cm. `Shift+Alt+1..0` picks the first ten. The export scale defaults to 1 (Godot); use 100 for centimetre engines.

| | |
|---|---|
Press **F1** in the editor for the full shortcut sheet.

Layout: the left strip holds tools (select, block, move/rotate/scale). The viewport header holds how you look and select (vertex/edge/face/object, grid, snap, backfaces). The inspector edits the selection. The status bar only reports.

| | |
|---|---|
| Camera | RMB + WASD/QE fly (Shift fast, wheel speed) · Alt+LMB orbit · MMB pan · wheel dolly · F focus |
| Create | Shift+B block: drag base, release, move for height, click/Enter |
| Select | 1 vert · 2 edge · 3 face · 4 object · click / Shift add / Ctrl toggle / drag marquee · Ctrl+A · Ctrl+I · Esc |
| Transform | W move · E rotate · R scale · Shift+drag (faces) extrude · Alt+arrows nudge · `[` `]` grid · inspector Position/Size (Enter) |
| Edit | Del · Shift+D duplicate · Shift+N flip · H hide · Alt+H unhide · Shift+Ctrl+Alt+V verts to grid · Ctrl+Z / Ctrl+Y |
| View | Alt+B select backfaces (clicks can hit faces pointing away; rendering doesn't change) · `[` `]` grid 3 cm to 16 m |
| Texture | Shift+T apply · Shift+RMB lift · Shift+Ctrl+G grid · Shift+Ctrl+F face · Ctrl+Alt+Num5 fit · inspector shift/scale/rotate/flip |
| File | Ctrl+N/O/S, Ctrl+Shift+S · Ctrl+E export GLB (Ctrl+Shift+E export as) |

The UI follows the monitor's display scale (Windows DPI). Override it with `CHIP_UI_SCALE=1.25` if it looks off.

Godot: drop the `.glb` into the project. With *Static collision* on, nodes are named `*-col` and Godot generates trimesh colliders.

See `SPEC.md` for the full Scythe/S2 feature map, the v1 scope, and the review log.
