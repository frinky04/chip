# Contributing to Chip

Chip is built by several agents working in parallel git worktrees (see `PLAN.md`). This file is the shared playbook: where things live, how to add a feature without touching anyone else's files, and the bar a branch must clear before it merges.

## Build, test, capture

C# (.NET 10), Silk.NET + OpenGL 3.3, ImGui.NET.

```
dotnet run --project src/Chip.Editor          # editor
dotnet test                                    # core + scenario tests
Chip.exe --shot level.chip out.png faces      # headless UI capture (empty|objects|faces|block|backfaces)
Chip.exe --rules-schema [out.json]            # JSON Schema for dressing rules files
python scripts/make-banner.py                 # redraw docs/images/banner.png (needs Pillow)
```

The README screenshots in `docs/images` are `--shot` captures at 1600×940: `editor.png` is the `dressing` state with
`CHIP_SHOT_CAMERA=1,5.5,0.8,7,0,5`; `dressed.png`, `blockout.png`, `dressing-rules.png` and `dress-with.png` are the
`dressing`, `dressing-blockout`, `dressing-rules` and `dressing-dresswith` states; `faces.png`, `hotspot-editor.png` and
`palette.png` are those states on `tests/fixtures/level.chip`.

### Frame benchmark

Build Release, then run `Chip.exe --bench [stress|level.chip] [out.json]` (or `dotnet run -c Release --project src/Chip.Editor -- --bench stress artifacts/frame.json`). The default is `stress`: 2,000 meshes, about 100k faces, 200 entities and 20 groups. The window stays hidden, uses a 1600×900 backbuffer (times `CHIP_UI_SCALE`, so every scale lays out the same panels) with vsync off, and never reads or writes user settings. All mouse positions, camera motion and `TransformGesture` moves are scripted inside the app.

Each scenario reports CPU median/p95/max, process-wide managed bytes per frame (including worker threads), GL elapsed timer-query results, stage medians and changed-mesh upload counts. GPU waits happen outside the CPU sample; GL elapsed time includes gaps while the CPU submits the frame, so it is not isolated GPU execution time. Gesture setup and cancel are outside the frame samples. There are 240 driver/JIT warmup frames, then 15 warmup and 60 measured frames per scenario.

Set `CHIP_BENCH_FRAMES`, `CHIP_BENCH_WARMUP` or the scenario prefix `CHIP_BENCH_FILTER` to adjust a run. For diagnosis, `CHIP_BENCH_SKIP_OCCLUSION=1` skips entity visibility rays, and `CHIP_BENCH_SKIP_PICKING=1` skips hover picks. Both flags are recorded in the JSON; leave them unset for full-editor results. Compare the same build configuration, scale, frame counts and flags on the same machine. CPU timings include script application and rendering submission; inspector section timings are subsets of `shell`.

## Map

```
src/Chip.Core/            engine-free logic, fully unit-tested
  Geometry/PolyMesh.cs    n-gon mesh: Positions, Faces (Verts, Material, Map, UVs?), SoftEdges, Locked, Collision
  Geometry/Ops/*.cs       MeshOps, one file per op family (static partial class)
  Geometry/Topology.cs    adjacency queries + smoothed corner normals
  Texturing/              TextureMapping (planar projection; scale in metres per texel)
  Scene/                  Document (meshes, entities), Selection, Session (undo), TransformGesture, MaterialLibrary
  Picking/                rays, face/vertex/edge picking, marquee
  IO/                     .chip (JSON v2) serializer, GLB exporter
src/Chip.Editor/
  Framework/              EditorContext, CommandRegistry/Hotkey, IViewportTool, IInspectorSection, IDockPanel, IEditorFeature
  Features/Base/          built-in commands (file, edit, view, material)
  Tools/                  SelectTool, BlockTool (one file per tool)
  Inspector/              built-in inspector sections
  Shell/                  host panels generated from the registries (menu bar, dock, tool strip, header, inspector, status, dialogs)
  Viewport/               Camera, Gizmo, Navigator
  Rendering/              Renderer (GL 3.3, offscreen MSAA), shaders
  UI/                     Theme, Fonts, Icons, Widgets (the widget vocabulary)
  EditorApp.cs            the host: window, frame loop, layout, input routing
tests/Chip.Core.Tests     core logic
tests/Chip.Editor.Tests   registry/command tests on a headless EditorContext
tests/fixtures/level.chip scenario level used by captures
```

Units are **metres**, Y-up, right-handed. Texel density is per level (`Document.TexelDensity`, new levels 256 px/m, older files 512) unless a texture has its own; read it through `MaterialInfo.TexelsPerMeter`, never a constant.

## Adding a feature (no shared-file edits)

Create one feature class. It is discovered automatically:

```csharp
namespace Chip.Editor.Features.MeshEditing;   // your stream's folder; never name a namespace after a type (MeshOps, Selection...)

public sealed class MeshOpsFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        ctx.Commands.Add("mesh.bridge", "Bridge", () => Bridge(ctx), menu: "Mesh", hotkey: "Shift+C",
            canExecute: () => ctx.Sel.Mode == SelectMode.Face && ctx.Sel.Count == 2, group: "3-connect");
        ctx.AddTool(new ClipTool());                      // tool strip + Tools menu + hotkey
        ctx.AddSection(new BevelOptionsSection());        // inspector block
        ctx.AddDockPanel(new PrefabsPanel());             // a tab of the left dock
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, false, false, true,
            "Texture", "wrap texture", c => Wrap(c)));     // Alt+RMB, checked before camera/tools
        ctx.Commands.AddReference("Mesh", "Shift + drag", "push/pull"); // sheet-only entry
    }
}
```

- **Commands**: id `area.camelCase`; `Menu` must be one of `CommandRegistry.MenuOrder`. `Group` names sort, so prefix with a number. `CanExecute` greys the item and blocks the hotkey. Set `DuringInteraction` only for things that must work mid-drag (undo/redo/cancel).
- **Where UI goes.** The dock (`IDockPanel`) is for what you pick from: libraries, the scene, things to place, rule sets. The inspector (`IInspectorSection`) is for the selection only, plus the Level section when nothing is selected; start `Draw` with `if (!Heading(...)) return;` so the section folds. Lay rows out in two columns: `RowLabel` then the controls, `FieldStart` for a row without a label, `ButtonRow`/`CommandRow` for buttons (equal widths filling the field column), one checkbox per line; property tables with long names use `PropertyColumn`. Pick the section's `Order` by where it belongs: selection 100, actions 105, entity 110, material 200, texture 300, hotspot 310, smoothing 400, collision 500, tags 700, dressing 710. Level-wide settings go in the Level section, not a menu. A feature's mesh operation also gets a button in `ActionsSection` for its selection mode. Floating windows are for large editors that need room (hotspot editor) and dialogs.
- **Tools** implement `IViewportTool`. Read the mouse from `ctx.Mouse` / `ctx.MouseRay()`, set `ctx.Hover`, draw overlays in `Draw`, return `Busy` while mid-operation, and undo partial work in `Cancel` (Esc). Options go in `HasOptions`/`DrawOptions`.
- **Edits** happen inside `ctx.Session.Do(label, ...)` (one undo step), or a `TransformGesture` for interactive transforms (`Begin` → `Apply` repeatedly → `Commit`/`Cancel`). Never mutate the document outside a step.
- **Mesh ops** go in a new `Geometry/Ops/<Name>.cs` partial of `MeshOps`. Build a face from a source face with `src.Derive(verts)` (material, mapping and tags; never `new Face(v, src.Material, src.Map)`, which drops tags), and add the op to `TagTests.Every_op_keeps_tags_on_every_face`, which also runs `MeshOps.Check` on the result (pass `valid: false` only when a drag is meant to finish the shape, as with zero-height extrudes). Split faces through `PlanarSplit` and put vertices on existing edges through `EdgePoints` rather than splicing loops by hand. Keep face indices stable when you can. Route vertex renumbering through `RemoveUnusedVertices`/`Reindex` so `SoftEdges` and `UVs` stay valid. Reorder `Face.UVs` whenever you reorder `Face.Verts`. Return what the caller needs to fix the selection (maps or new indices).
- **Helpers inside `MeshOps` partials share one class**: give private helpers op-specific names, or promote common ones (like `SafeNormalize`) to `Ops/MeshOps.cs`.
- **Per-mesh caches** key on `Session.MeshRevision(mesh)`, which changes whenever that mesh's content does (undo and cancel included). Don't hash meshes yourself.
- **Performance**: `dotnet run -c Release --project tests/Chip.Bench [-- prefix]` times core operations on `StressLevel.Generate()` (2,000 meshes, ~100k faces). Add benches as `tests/Chip.Bench/Benches/<Area>.cs`.
- **Editor frame performance**: `Chip.exe --bench stress artifacts/frame.json` runs the hidden full-editor scenarios (Release; `CHIP_UI_SCALE` respected); see README for timing and diagnostic flags.
- **Selection after topology edits**: use `Selection.Remap` or rebuild the selection explicitly; `Session.End` prunes anything invalid.
- **Entity classes** come from the engine (`docs/entities.md`): don't add built-in classes in C#. Add a scene to `godot/addons/chip_import/entities/`, then `powershell -File scripts/godot-test.ps1 -UpdateBuiltins` regenerates Chip's embedded copy. Read entity values through `EntityValues` (they may be v2 strings) and set class properties with `EntityOps.SetProperty(e, spec, value)`, which types them and drops defaults. Changes to the addon need `scripts/godot-test.ps1` to pass.
- **Dressing rule kinds** (`src/Chip.Core/Dressing/`, design in `docs/dressing.md`): a kind is a `RuleKind` subclass with a `DisplayName` and a `ParamSpec` table (validation, defaults, the schema, the Dressing tab and docs all come from it: give each parameter a `Label`, a `Unit` for numbers and `ChoiceLabels` for choices), added to `RuleKind.All`, and run from `Dresser` as cached per-(rule, mesh) jobs. Never mutate the document; return meshes. Add a recipe for it (`Recipes.All`), cases to `DressingTests` (including the randomised incremental-equals-fresh test) and check a sample with `Chip --rules-check`. Editor code changes rules only through `DressingFeature.EditRules` (a `RulesWorkspace` edit written as one undo step), never by writing the file directly.

## Shared files: who may touch them

| Path | Owner | Others |
|---|---|---|
| `Framework/*`, `EditorApp.cs`, `Shell/*` | Phase 0 / stream K | Need a hook? Open a tiny PR to `main` first, then rebase. |
| `Features/Base/*`, `Inspector/*` built-ins | Phase 0 | Stream E owns `TextureSection.cs`; stream I owns `MaterialSection.cs`. |
| `Rendering/*`, `Viewport/*` | stream G | Others draw through `IViewportTool.Draw` only. |
| `Core/Geometry/PolyMesh.cs`, `Core/IO/DocumentSerializer.cs`, `Scene/Document.cs` | Phase 0 (schema v2 is frozen for Wave 1) | Schema changes need a PR to `main` with a migration and a version bump. |
| `UI/Widgets.cs`, `UI/Theme.cs` | Phase 0 / K | Add new widgets in your own file; propose promotion later. |

Everything else: create new files in your stream's folders (listed in `PLAN.md`).

## Hotkeys

Scythe's bindings first (`SPEC.md` §4–§9), then Source 2 Hammer. `Chip.Editor.Tests` fails on any duplicate, so check `F1` / the tests before claiming a key. Decisions so far:
- `H` / `Alt+H` = hide / unhide (Hammer). Scythe's Hard normals (`H`) gets a menu entry and a free chord instead.
- `Shift+H` / `Shift+F` are reserved for hotspots (stream F).
- `Ctrl+T` = tags popup; `Ctrl+Shift+T` = Dress with; `Ctrl+Shift+D` = Dressing tab; `` ` `` is reserved for the dressed/blockout view toggle (`docs/dressing.md`).
- Tool-internal keys (Enter, Esc, Q/E in a UV editor) are read inside the tool's `Update`, not registered globally.

## UI rules

- Build from `UI/Widgets` (ToolButton, SegmentedControl, TextSegments, Toggle, IconButton, ValueSlider, TaggedFields, Heading, RowLabel, KeyHints, PushPrimary, SelectedRow).
- **Krita dark orange**: neutral greys (chrome `Theme.Bg`, docks `Theme.Panel`, fields `Theme.Frame`, recessed tracks `Theme.Track`), dark hairlines (`Theme.Border`), and one solid orange fill with dark text (`Theme.OnAccent`) for what is active or selected: the active tool and mode, selected rows (`SelectedRow` + `RowText`), the primary button of a dialog or pending tool (`PushPrimary`), and the value part of a slider (`ValueSlider`). Settings that are on use a neutral raised fill with an orange icon. Never orange for decoration.
- Inspector sections start with `Heading`; the host turns that first heading into the section's dock title bar.
- Sizes go through `Theme.Px(...)` (DPI scaling). Text sizes are `Fonts.BodySize` / `Fonts.SmallSize`. Icons come from Tabler (`UI/Icons.cs`; add glyphs to the map).
- Copy: sentence case, plain verbs, and buttons say what happens. Errors say what happened and how to fix it (`ctx.Status(msg, error: true)`). No ALL-CAPS labels, no middle-dot meta strings.

## Definition of done (per stream)

1. Core logic has unit tests in `tests/Chip.Core.Tests` (topology stays manifold where expected, UVs/materials propagate, undo restores).
2. Commands/tools register cleanly: `tests/Chip.Editor.Tests` passes (no hotkey conflicts).
3. Every gesture is one undo step; Esc cancels cleanly.
4. **Headless proof**: `powershell -File scripts/verify.ps1 -Run "your.command,..."` passes, and you have looked at the captures in `artifacts/verify/` (add a capture state to `EditorApp.Snapshot.cs` only if `--run` can't reach it). To prove a hotkey end to end, set `CHIP_SHOT_KEYS=F1` (or `Ctrl+P`, comma-separated) on a `--shot` run: the keys are pressed inside ImGui, never through the OS. `--run` executes commands directly and skips the keyboard path. `CHIP_SHOT_DRAG=x0,y0,x1,y1[,Ctrl+Alt]` (viewport fractions) sweeps the left button across the viewport over 16 frames, for drag gestures; a sixth field `hold` (`x0,y0,x1,y1,,hold`) keeps the button down so the capture shows the drag in progress, and `CHIP_SHOT_TYPE=1.5` then types into it. `CHIP_SHOT_REPEAT=2` runs the drag twice; a zero-length drag is a click, so that proves clicking the same spot again (cycling through overlapping objects). `CHIP_SHOT_CAMERA=px,py,pz,tx,ty,tz` (metres) puts the camera at p looking at t, for close-ups.
5. `SPEC.md` tags updated for what you shipped; no new compiler warnings.
6. Rebased on `main`, one stream per branch, commits explain why.
