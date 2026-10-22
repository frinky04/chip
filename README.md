# Chip

Standalone level-blockout editor in the spirit of Scythe / Source 2 Hammer. C# (.NET 10), Silk.NET + OpenGL 3.3, ImGui.NET. Exports `.glb` for Godot (or anything else that reads glTF).

```
dotnet run --project src/Chip.Editor          # editor
dotnet test                                    # core + scenario tests
Chip.exe --shot level.chip out.png faces      # headless UI capture (empty|objects|faces|block|backfaces)
Chip.exe --rules-check level.chip [--json]    # check a level's dressing rules (exit 0 clean, 1 warnings, 2 errors)
Chip.exe --rules-schema [out.json]            # JSON Schema for dressing rules files
```

**Dressing** (docs/dressing.md): rules turn the blockout into the shipped level: baseboards and trims along walls, pipes in
corners, bevelled corners, boxes that become railings, ramps that become stairs, lights along walls, rubble on floors.
Select walls and press Ctrl+Shift+T (Dress with) to add a rule from a recipe and apply it; Ctrl+Shift+D (Dressing rules)
edits rules in centimetres and words, with styles shared between levels. Edit the blockout and the dressing follows. The
rules are saved as JSON beside the level (`level.dressing.json`), so agents can edit them too, and Chip reloads the file
live. Override a rule for one mesh in the inspector, or Bake it to edit by hand. `` ` `` switches between the dressed
level and the bare blockout; export always ships the dressed level. Try it: Tools > Open dressing sample.

Everything is in metres. Materials are the PNGs in `assets/textures`, and texel density is set per level, Scythe style, in the Material section: "1 m is 256 px" by default, so a 128 px texture tiles every 50 cm. Textures can have their own density. `Shift+Alt+1..0` picks the first ten. The export scale defaults to 1 (Godot); use 100 for centimetre engines.

| | |
|---|---|
Press **F1** in the editor for the full shortcut sheet.

Layout: the left strip holds tools (select, block, move/rotate/scale). The viewport header holds how you look and select (vertex/edge/face/object, grid, snap, backfaces). The inspector edits the selection. The status bar only reports.

| | |
|---|---|
| Camera | RMB + WASD/QE fly (Shift fast, wheel speed) · Alt+LMB orbit · MMB pan · wheel dolly · F focus |
| Create | Shift+B block: drag base, release, move for height, click/Enter |
| Select | 1 vert · 2 edge · 3 face · 4 object · click / Shift add / Ctrl toggle · drag paints a selection (Shift adds, Ctrl removes) · Ctrl+Alt+drag box select · Ctrl+A · Ctrl+I · Esc |
| Transform | W move · E rotate · R scale · Shift+drag (faces) extrude · Alt+arrows nudge · `[` `]` grid · inspector Position/Size (Enter) |
| Mesh | I inset · O outset · Shift+O offset faces along their normals (trims, baseboards) · B bevel · C knife · V connect |
| Edit | Del · Shift+D duplicate · Shift+N flip · H hide · Alt+H unhide · Shift+Ctrl+Alt+V verts to grid · Ctrl+T tags (faces, or meshes in object mode) · Ctrl+Shift+T dress with · Ctrl+Shift+D dressing rules · Ctrl+Z / Ctrl+Y |
| View | Alt+B select backfaces (clicks can hit faces pointing away; rendering doesn't change) · `[` `]` grid 3 cm to 16 m |
| Texture | Shift+T apply · Shift+RMB lift · Shift+Ctrl+G grid · Shift+Ctrl+F face · Ctrl+Alt+F fit · Ctrl+arrows shift · Ctrl+Shift+arrows scale · Alt+, / Alt+. rotate · Shift+Alt+arrows justify (numpad keys work too) |
| File | Ctrl+N/O/S, Ctrl+Shift+S · Ctrl+E export GLB (Ctrl+Shift+E export as) |

The UI follows the monitor's display scale (Windows DPI). Override it with `CHIP_UI_SCALE=1.25` if it looks off.

**Reference models** (Tools > Reference model): show a `.glb` in the level, such as your door or the built-in 1.78 m figure, to size holes and check scale. Click a wall and a door sits inside it. Turn on "Export as marker" to export an empty node where the real asset goes; otherwise references stay in the editor.

Godot: drop the `.glb` into the project. With static collision on, Chip bakes the world collision into `chip_world_collision_N-colonly` nodes that Godot turns into colliders (File > Export settings).

**Entity classes** come from Godot: with the `godot/addons/chip_import` addon enabled, every scene under `res://entities/` is a class Chip can place, its `@export` vars are the properties the inspector edits, and on import each entity becomes an instance of its scene. Save or export the level inside the Godot project and Chip picks the classes up (and reloads them as you change the scenes). See `docs/entities.md`. Headless export: `Chip.exe --export level.chip [out.glb]`.

See `SPEC.md` for the full Scythe/S2 feature map, the v1 scope, and the review log.

## Frame benchmark

Build Release, then run `Chip.exe --bench [stress|level.chip] [out.json]` (or `dotnet run -c Release --project src/Chip.Editor -- --bench stress artifacts/frame.json`). The default is `stress`: 2,000 meshes, about 100k faces, 200 entities and 20 groups. The window stays hidden, uses a fixed 1600?900 backbuffer with vsync off, respects `CHIP_UI_SCALE`, and never reads or writes user settings. All mouse positions, camera motion and `TransformGesture` moves are scripted inside the app.

Each scenario reports CPU median/p95/max, process-wide managed bytes per frame (including worker threads), GL elapsed timer-query results, stage medians and changed-mesh upload counts. GPU waits happen outside the CPU sample; GL elapsed time includes gaps while the CPU submits the frame, so it is not isolated GPU execution time. Gesture setup and cancel are outside the frame samples. There are 240 driver/JIT warmup frames, then 15 warmup and 60 measured frames per scenario.

Set `CHIP_BENCH_FRAMES`, `CHIP_BENCH_WARMUP` or the scenario prefix `CHIP_BENCH_FILTER` to adjust a run. For diagnosis, `CHIP_BENCH_SKIP_OCCLUSION=1` skips entity visibility rays, and `CHIP_BENCH_SKIP_PICKING=1` skips hover picks. Both flags are recorded in the JSON; leave them unset for full-editor results. Compare the same build configuration, scale, frame counts and flags on the same machine. CPU timings include script application and rendering submission; inspector section timings are subsets of `shell`.
