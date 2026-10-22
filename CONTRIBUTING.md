# Contributing to Chip

Chip is built by several agents working in parallel git worktrees (see `PLAN.md`). This file is the shared playbook: where things live, how to add a feature without touching anyone else's files, and the bar a branch must clear before it merges.

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
  Framework/              EditorContext, CommandRegistry/Hotkey, IViewportTool, IInspectorSection, IEditorFeature
  Features/Base/          built-in commands (file, edit, view, material)
  Tools/                  SelectTool, BlockTool (one file per tool)
  Inspector/              built-in inspector sections
  Shell/                  host panels generated from the registries (menu bar, tool strip, header, status, dialogs)
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
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, false, false, true,
            "Texture", "wrap texture", c => Wrap(c)));     // Alt+RMB, checked before camera/tools
        ctx.Commands.AddReference("Mesh", "Shift + drag", "push/pull"); // sheet-only entry
    }
}
```

- **Commands**: id `area.camelCase`; `Menu` must be one of `CommandRegistry.MenuOrder`. `Group` names sort, so prefix with a number. `CanExecute` greys the item and blocks the hotkey. Set `DuringInteraction` only for things that must work mid-drag (undo/redo/cancel).
- **Tools** implement `IViewportTool`. Read the mouse from `ctx.Mouse` / `ctx.MouseRay()`, set `ctx.Hover`, draw overlays in `Draw`, return `Busy` while mid-operation, and undo partial work in `Cancel` (Esc). Options go in `HasOptions`/`DrawOptions`.
- **Edits** happen inside `ctx.Session.Do(label, ...)` (one undo step), or a `TransformGesture` for interactive transforms (`Begin` → `Apply` repeatedly → `Commit`/`Cancel`). Never mutate the document outside a step.
- **Mesh ops** go in a new `Geometry/Ops/<Name>.cs` partial of `MeshOps`. Build a face from a source face with `src.Derive(verts)` (material, mapping and tags; never `new Face(v, src.Material, src.Map)`, which drops tags), and add the op to `TagTests.Every_op_keeps_tags_on_every_face`, which also runs `MeshOps.Check` on the result (pass `valid: false` only when a drag is meant to finish the shape, as with zero-height extrudes). Split faces through `PlanarSplit` and put vertices on existing edges through `EdgePoints` rather than splicing loops by hand. Keep face indices stable when you can. Route vertex renumbering through `RemoveUnusedVertices`/`Reindex` so `SoftEdges` and `UVs` stay valid. Reorder `Face.UVs` whenever you reorder `Face.Verts`. Return what the caller needs to fix the selection (maps or new indices).
- **Helpers inside `MeshOps` partials share one class**: give private helpers op-specific names, or promote common ones (like `SafeNormalize`) to `Ops/MeshOps.cs`.
- **Per-mesh caches** key on `Session.MeshRevision(mesh)`, which changes whenever that mesh's content does (undo and cancel included). Don't hash meshes yourself.
- **Performance**: `dotnet run -c Release --project tests/Chip.Bench [-- prefix]` times core operations on `StressLevel.Generate()` (2,000 meshes, ~100k faces). Add benches as `tests/Chip.Bench/Benches/<Area>.cs`.
- **Editor frame performance**: `Chip.exe --bench stress artifacts/frame.json` runs the hidden full-editor scenarios (Release; `CHIP_UI_SCALE` respected); see README for timing and diagnostic flags.
- **Selection after topology edits**: use `Selection.Remap` or rebuild the selection explicitly; `Session.End` prunes anything invalid.
- **Entity classes** come from the engine (`docs/entities.md`): don't add built-in classes in C#. Add a scene to `godot/addons/chip_import/entities/`, then `powershell -File scripts/godot-test.ps1 -UpdateBuiltins` regenerates Chip's embedded copy. Read entity values through `EntityValues` (they may be v2 strings) and set class properties with `EntityOps.SetProperty(e, spec, value)`, which types them and drops defaults. Changes to the addon need `scripts/godot-test.ps1` to pass.
- **Dressing rule kinds** (`src/Chip.Core/Dressing/`, design in `docs/dressing.md`): a kind is a `RuleKind` subclass with a `DisplayName` and a `ParamSpec` table (validation, defaults, the schema, the Dressing rules window and docs all come from it: give each parameter a `Label`, a `Unit` for numbers and `ChoiceLabels` for choices), added to `RuleKind.All`, and run from `Dresser` as cached per-(rule, mesh) jobs. Never mutate the document; return meshes. Add a recipe for it (`Recipes.All`), cases to `DressingTests` (including the randomised incremental-equals-fresh test) and check a sample with `Chip --rules-check`. Editor code changes rules only through `DressingFeature.EditRules` (a `RulesWorkspace` edit written as one undo step), never by writing the file directly.

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
- `Ctrl+T` = tags popup; `Ctrl+Shift+T` = Dress with; `Ctrl+Shift+D` = Dressing rules window; `` ` `` is reserved for the dressed/blockout view toggle (`docs/dressing.md`).
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
4. **Headless proof**: `powershell -File scripts/verify.ps1 -Run "your.command,..."` passes, and you have looked at the captures in `artifacts/verify/` (add a capture state to `EditorApp.Snapshot.cs` only if `--run` can't reach it). To prove a hotkey end to end, set `CHIP_SHOT_KEYS=F1` (or `Ctrl+P`, comma-separated) on a `--shot` run: the keys are pressed inside ImGui, never through the OS. `--run` executes commands directly and skips the keyboard path. `CHIP_SHOT_DRAG=x0,y0,x1,y1[,Ctrl+Alt]` (viewport fractions) sweeps the left button across the viewport over 16 frames, for drag gestures; a sixth field `hold` (`x0,y0,x1,y1,,hold`) keeps the button down so the capture shows the drag in progress, and `CHIP_SHOT_TYPE=1.5` then types into it. `CHIP_SHOT_CAMERA=px,py,pz,tx,ty,tz` (metres) puts the camera at p looking at t, for close-ups.
5. `SPEC.md` tags updated for what you shipped; no new compiler warnings.
6. Rebased on `main`, one stream per branch, commits explain why.
