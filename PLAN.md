# Chip: path to feature-complete

"Feature-complete" = everything tagged v1/v2 in `SPEC.md` (Scythe guide parity and the core Source 2 Hammer workflow), plus a production shell (settings, autosave, docking) and a solid Godot pipeline. Items tagged `later` in the spec stay out unless listed here.

The work runs in three phases. **Phase 0 is serial and short.** It creates the seams that let **Phase 1 run as parallel workstreams in separate git worktrees**, with almost no merge conflicts. Phase 2 integrates and hardens.

```
Phase 0  Foundation (1 agent, serial)
   │
   ├── Wave 1 (parallel worktrees) ── A Mesh ops   B Cut tools   C Creation   D Selection
   │                                   E Texturing  G Views       I Materials  K Shell
   │
   ├── Wave 2 (parallel worktrees) ── F Hotspots   H Scene/entities   J Pipeline   L Normals
   │
Phase 2  Integration, performance, release (1–2 agents)
```

---

## Phase 0: Foundation (serial, blocks everything)

Goal: after this, a feature lives in **its own files** and registers itself. No workstream edits a shared file except to add its own new files.

| # | Seam | What changes | Why it matters |
|---|---|---|---|
| 0.1 | **`EditorContext`** | One deep module that features talk to: `Session`, selection, camera/view, grid/snap, active material, status messages, overlay drawing, materials, `Picker` with current options. `EditorApp` becomes the host that builds it. | Features never reach into `EditorApp` privates. |
| 0.2 | **Command registry** | `Command(id, label, menuPath, hotkey, canExecute, execute)`. Menus, hotkeys, the F1 sheet and (later) the command palette and remapping are all **generated** from it. A unit test fails on duplicate hotkeys or ids. | Removes the two hottest conflict files (`Commands.cs` switch, menu code). |
| 0.3 | **Viewport tool interface** | `IViewportTool { Icon, Name, Hotkey, Update(ctx, input), Draw(ctx, dl), Hints, Cancel(), OptionsUI(ctx) }`. Select/transform and Block become the first two implementations. The tool strip is generated from the tool list, and `OptionsUI` renders in a "Tool" inspector section. | Clip, knife, bevel, polygon, UV and hotspot tools each become one file. |
| 0.4 | **Inspector sections** | `IInspectorSection { Order, Title, Visible(ctx), Draw(ctx) }`. The current Selection/Material/Texture/Export blocks move into four section files. | Each stream adds its own panel section. |
| 0.5 | **Feature discovery** | `IEditorFeature.Register(EditorContext)`, discovered by reflection at startup. Each workstream ships one `XxxFeature.cs` that registers its commands, tools and sections. | **Zero shared-file edits to add a feature.** |
| 0.6 | **Split `MeshOps`** | `static partial class MeshOps` across `Geometry/Ops/*.cs` (Create, Extrude, Delete, Weld…). Shared helpers (adjacency, face/edge queries) go in `Topology.cs`. | Mesh-op streams add files instead of editing one file. |
| 0.7 | **Data model v2** (schema bump, migration from v1) | **Per-corner explicit UVs** (`Face.UVs: Vector2[]?`; when set, they override the projection in render, export and picking). **Edge sharpness** (`PolyMesh.HardEdges: HashSet<Edge>` plus a smoothing render path). **Stable ids** for meshes (already) and `Document.Entities` (minimal: id, class name, position, rotation, key-values). **Per-mesh flags** (`Collision: Default/None/Only`, `Locked`). The serializer, exporter and renderer read all of these, defaulting to today's behaviour. | E, F, H, J, L need the data shape now. Defining it once avoids four competing versions. |
| 0.8 | **Test & verification harness** | `--shot` gains `--script` (JSON list of commands to run before capture), so every stream can add a headless UI check. Add a `tests/Chip.Editor.Tests` project for registry/context tests (no GL). CI script: `dotnet test` + scripted shots. | Gives every stream the same proof bar. |
| 0.9 | **`CONTRIBUTING.md`** | File-ownership rules, how to add a command/tool/section, hotkey policy (Scythe first, then Hammer), widget vocabulary (`UI/Widgets`), and the definition of done. | Agents follow one playbook. |

**Exit:** the app looks and behaves exactly as now, all existing tests pass, the registry has every current command, and `Panels.cs`/`Commands.cs` are thin hosts.

---

## Phase 1: Parallel workstreams

Each stream gets a branch `feat/<letter>-<name>` in its own worktree off post-Phase-0 `main`. "Owns" means only that stream creates or edits those paths. **Spec refs** point at `SPEC.md` sections.

### Wave 1 (start together)

**A · Mesh operations** (§6) · owns `Core/Geometry/Ops/{Connect,MergeFaces,Bridge,Inset,Collapse,Mirror,Detach,PushPull,EdgeExtrude}.cs`, `Editor/Features/MeshOpsFeature.cs`
- Connect `V`, Merge faces `Backspace`, Bridge `Shift+C`, Inset/Outset `I/O` (interactive amount), Collapse, Weld overlapping `Shift+Ctrl+Alt+M`, Merge meshes `M`, Detach `N` / Extract `Alt+N`, Mirror `Shift+Z`.
- Push/pull (`Shift+Ctrl` + drag, normal-only with coplanar side merge). Extrude boundary edges (`Shift`-drag in edge mode).
- Done: every op tested for closed-manifold preservation and UV/material propagation; one undo step each.

**B · Cutting tools** (§6) · owns `Core/Geometry/Ops/{Clip,Knife,Bevel}.cs`, `Editor/Tools/{Clip,Knife,Bevel}Tool.cs`
- Clip `Shift+X` (plane from two clicks plus the view; keep front/back/both; capped cut).
- Knife/Cut `C` (draw a polyline across faces, snapped to grid and edges).
- Bevel `B` (edges/verts, segments, width by drag).
- Done: cap faces are closed and world-aligned; interactive previews; Esc cancels cleanly.

**C · Creation tools** (§5) · owns `Core/Geometry/Primitives/*.cs`, `Editor/Tools/{Polygon,Rectangle}Tool.cs`, `Editor/Tools/Block/Shapes/*`
- Block tool shape picker (box, cylinder, wedge, stairs, arch, sphere, torus, spiral stairs) with per-shape options in the Tool section (sides, steps, arc).
- Add Rectangle `Shift+R` (open quad). Polygon draw tool (click points on a plane, then extrude). Ctrl flips the base plane toward the camera (Scythe).
- Done: every primitive is closed and world-UV'd, its parameters are remembered, and it previews before commit.

**D · Selection power** (§4) · owns `Core/Scene/SelectionOps.cs`, `Core/Picking/Topology queries` (new file), `Editor/Features/SelectionFeature.cs`
- Double-click flood fill (connected faces / edge loop). Select coplanar (CapsLock/Mouse4/5 + double-click). Edge loop `L` / ring `X`. Grow/shrink `Alt+Num±`.
- Path select (drag across elements). Select by material. Named selection sets saved in the doc. A visible-only marquee toggle.
- Done: works on n-gon meshes with boundary edges; tested on stairs/arch primitives once C lands (use the test fixtures until then).

**E · Texturing** (§8) · owns `Core/Texturing/{Unwrap,Wrap,Justify}.cs`, `Editor/Panels/FastTextureTool.cs`, `Editor/Features/TextureFeature.cs`
- **Fast Texture Tool** `Shift+Q`: a 2D UV editor panel (grid `[`/`]`, frame, rotate `Q/E`, flip, repeat background, coordinates, world scale, and the ideal-texel-density rectangle).
- Unwraps: Use Existing / Conforming / Square / Follow Quads (Length/Even/Avg) / Planar, writing explicit UVs.
- Wrap Texture `Alt+RMB`, Wrap to Selection `Shift+Alt+RMB`, Flow `Ctrl+Alt+RMB`. Justify, numpad shift/scale/rotate hotkeys, flip face UV. Texture lock toggle. Fit options (Keep Ratio, Treat as One, Fit Width/Height).
- Done: projection↔explicit precedence is documented; explicit UVs survive moves (they stay locked by definition).

**G · Views & navigation** (§2, §3) · owns `Editor/Viewport/{Camera,ViewportPane,Layout,Gizmo}.cs`, `Rendering/Renderer.cs` view-mode code, `Editor/Features/ViewFeature.cs`
- `ViewportPane` abstraction. Top/front/side orthographic views from the header "Perspective" dropdown. 1/2/4-pane layouts.
- View modes: lit, unlit, wireframe, UV checker. Boundary edges dashed (Scythe).
- Gizmo world/local/selection orientation, temporary pivot (MMB-drag the gizmo), drag-align (CapsLock vertex snap), separate rotation snap and snap bypass.
- Construction grid follows the hovered face while creating. Camera bookmarks. Focus at cursor.
- Done: every tool works unchanged in ortho panes (tools use `ViewInfo`, not `Camera`).

**I · Materials & assets** (§7) · owns `Core/Scene/MaterialLibrary*.cs`, `Editor/Panels/MaterialBrowser.cs`, `Editor/Features/MaterialFeature.cs`
- Material browser panel: search, folders, thumbnail size, recent/favourites, drag onto faces. Project texture folders (from settings, not just `assets/`). JPG/TGA support.
- Per-material texel density override. **Tool materials** (`nodraw`, `clip`, `trigger`, `skip`) that set collision/visibility on export. Replace material. Name aliasing (Albedo/BaseColor…) for engine mapping.
- Done: 500+ textures stay responsive (lazy thumbnails).

**K · App shell** (§1) · owns `Editor/Shell/*`, `Editor/Settings/*`
- Preferences (units display, grid presets, theme accents, autosave interval, **hotkey remapping** via the registry). Recent files. **Autosave and crash recovery**.
- Command palette `Ctrl+P` (searches the registry). Re-scale on monitor DPI change. Dockable panels: ImGui.NET 1.90 exposes `DockSpaceOverViewport` but not the internal `DockBuilder` API, so ship a default layout as a bundled `imgui.ini` (or P/Invoke `igDockBuilder*` from cimgui), with a "Reset layout" command.
- Single-file `dotnet publish` profile plus an icon.
- Done: settings persist in `%APPDATA%/Chip`; recovery restores after a forced kill.

### Wave 2 (start when their Wave 1 dependency merges)

**F · Hotspots** (§9) · needs E (explicit UVs) · owns `Core/Hotspots/*`, `Editor/Panels/HotspotEditor.cs`, `Editor/Features/HotspotFeature.cs`
- `.hotspot.json` asset (normalized rects + flags + target materials). Hotspot editor (draw rects on the texture with a grid, flags R/X/Y/T, inset, lit/hide toggles).
- Apply `Shift+H` (coplanar regions) / `Shift+F` (per face), using the best fit by aspect and size at the texel density, with seeded random mirror/rotation saved in the doc. Settings: tiling mode, Disallow Random, Don't Apply Material, mapping mode (Automatic/Square/Conforming/Follow Quads).

**H · Scene organisation & entities** (§10) · needs 0.7 · owns `Core/Scene/{Entities,Groups}.cs`, `Editor/Panels/Outliner.cs`, `Editor/Tools/EntityTool.cs`
- Outliner (tree, rename, hide, lock, multi-select, drag to group). Groups/visgroups.
- Point entities (player start, light, generic class + key-values), entity tool, gizmos. Per-object origin/pivot.

**J · Pipeline & interop** (§11) · needs H for entity export · owns `Core/IO/{Export*,Import*}.cs`, `godot/addons/chip_import/*`
- Export options: per-mesh collision (`-col`, `-colonly`, `-convcolonly`); smoothing-aware normals (from L); entities as glTF nodes with `extras`; material remap to `res://` paths.
- **Watch mode**: re-export on save so Godot hot-reloads. OBJ export. glTF/OBJ import as editable meshes.
- **Godot addon**: an import post-processor that turns entities into scenes and swaps materials.

**L · Normals & smoothing** (§6) · needs 0.7 · owns `Core/Geometry/Ops/Normals.cs`, smoothing render path
- Soft `J` / Hard `H` / Dominant `K` normals on edges, rendered smoothed in the viewport and exported. Rebind Chip's current H/Alt+H (hide/unhide) to avoid the clash with Scythe's Hard normals (Hammer uses H for hide; decide in 0.2's hotkey policy).

---

## Phase 2: Integration and hardening

- **Merge train:** merge Wave 1 in dependency-risk order G → D → A → B → C → E → I → K, rebasing each on `main` and re-running the full suite. Wave 2 follows the same way.
- **Performance:** a BVH for picking, incremental per-mesh GPU buffers (today the whole scene rebuilds on any edit), and snapshot undo by per-mesh deltas. Target 2,000 meshes / 100k faces interactive.
- **Robustness:** n-gon validity checks (concave/non-planar warnings, auto-triangulate on export), fuzz tests for ops, and load/save of hostile files.
- **UX pass:** run the Sol UI review loop again on the full feature set; first-run onboarding; docs site from the registry (every command with its hotkey).
- **Release:** versioned builds and a changelog.

---

## Parallel-work rules (for every agent)

1. **Branch from post-Phase-0 `main`** in your own worktree; never edit files another stream owns. Need a hook in a shared file? Add it to `EditorContext` in a tiny separate PR to `main` first.
2. **Register, don't wire:** commands, tools and inspector sections come from your `XxxFeature.cs`.
3. **Hotkeys** come from `SPEC.md` (Scythe first). The registry duplicate test must pass.
4. **Definition of done:** core logic unit-tested; one undo step per gesture; Esc cancels; headless `--shot --script` capture of the feature; `SPEC.md` tags updated; no new warnings.
5. **Rebase before merge;** keep PRs to one stream.

## Suggested agent allocation

| When | Agents | Streams |
|---|---|---|
| Phase 0 | 1 | Foundation |
| Wave 1 | 8 in parallel | A B C D E G I K |
| Wave 2 | 4 in parallel | F H J L |
| Phase 2 | 1–2 | integration, performance, release |

Biggest risks: **E/F** (UV editor and hotspot fitting are the deepest features), **G** (viewport refactor touches what every tool draws into; mitigated by tools depending only on `ViewInfo` and the context), and **0.7** (the schema must be right before Wave 2).
