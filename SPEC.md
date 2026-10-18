# Chip: standalone level-blockout editor (Scythe and Source 2 Hammer feature set)

Chip is a standalone C# level editor. It covers the Scythe workflow (Unreal plugin) and Source 2 Hammer mesh editing, and it exports to any engine. Godot is the primary target. Chip owns the editable polygon mesh and exports finished geometry. It does not run inside an engine.

Sources: scytheeditor.com/guide (Overview, Quick Start, Hotkeys, Hotspots, Hotspot Texture Maker) and Source 2 Hammer behaviour.

Tags: **[v1]** = first shippable slice. **[v2]** = next. **[later]** = backlog. **[S2]** = from Source 2 Hammer, not in the Scythe docs.

---

## 0. Ground rules

- **Coordinates:** Y-up, right-handed, metres. The grid runs in powers of two from 1/32 m to 16 m (default 25 cm). Texel density is 512 px/m, so a 512 px dev wall tiles at 1 m. `Document.ExportScale` (default 1) multiplies on export: 1 for Godot, 100 for centimetre engines. glTF and Godot need no axis swizzle.
- **Mesh model:** each mesh is a polygon mesh of vertices and faces, where a face is an ordered vertex loop with a material and a texture mapping. A face is a Hammer face. A Scythe polygroup can be non-planar, but Chip restricts a face to one planar-ish n-gon. Edges are derived from the face loops. A triangle-topology view is **[later]**: Chip keeps n-gons and triangulates (ear clipping, shared by render, pick and export) only for output.
- **Vertices are stored in world space.** A mesh has no separate transform in v1. The pivot is the selection-bounds centre. Export keeps node origins at the world origin, so Godot placement stays stable across re-exports. **Review risk (v2):** stable vertex/face IDs, per-corner UV/normal attributes, an explicit per-object origin, and transforms are needed before instances, explicit UVs and hotspots. Face-index stability is kept by ops today.
- **Materials** are image files in `assets/textures`. The file stem is the material name and the pixel size drives texel density. Shift+Alt+1..0 picks the first ten.
- **`.chip` is versioned JSON** (`"version": 1`).
- **Editing is non-destructive to engine assets.** The `.chip` file is the source of truth. Exports are derived from it.
- **Hotkeys follow Scythe** wherever Scythe defines one. Otherwise they follow Hammer.

## 1. Application shell [v1]

| Feature | Notes |
|---|---|
| Dark, minimal, utilitarian theme with a Hammer-orange accent (#FF8A1F-ish) | Flat panels, 1px separators, dense spacing, monospace numerals in fields. Orange is used only for active, selected and focused states. |
| Fixed panel layout with resizable splitters (v1) | Menu bar, left tool strip, centre viewport, right inspector, bottom status bar. Every command is also in a menu with its hotkey. Tool strip and inspector widths drag and persist (double-click an edge to reset; View > Reset panel sizes). Docking is not planned while ImGui.NET lacks DockBuilder. |
| Status bar | Selection mode, element count, grid size, snap state, active tool hint. |
| File: New / Open / Save / Save As (`Ctrl+N/O/S`, `Ctrl+Shift+S`) | JSON `.chip` document. |
| Export glTF binary (`Ctrl+E`) | `.glb` with one node per mesh, one primitive per material, flat normals, UVs, and embedded dev textures (PNG). |
| Undo / Redo (`Ctrl+Z`, `Ctrl+Y` / `Ctrl+Shift+Z`) | Document snapshot per command. |
| Unsaved-changes marker in the title bar | |
| Preferences (`Ctrl+Shift+P`) **[v1]** | Settings in `%APPDATA%/Chip/settings.json`: interface scale, autosave interval, starting grid, hotkey remap (search, click to rebind, conflicts rejected), extra texture folders. Units display and theme accents are **[later]**. |
| Command palette (`Ctrl+P`) **[v1]** | Fuzzy search over every registered command (label, menu, id) with hotkeys; unavailable commands greyed; recent commands first. |
| Recent files, autosave and crash recovery **[v1]** | File > Open recent. Unsaved changes autosave to `%APPDATA%/Chip/autosave/` (atomic writes); at startup an autosave newer than its file is offered back (Restore / Discard). |
| Live re-scale on monitor DPI change **[v1]** | Fonts and style rebuild when the window moves to a monitor with a different scale, or when Preferences changes it. |
| Single-file Windows build **[v1]** | `scripts/publish.ps1`: self-contained `Chip.exe` plus `assets/`, with an app icon. |
| Multiple viewports (2D top/front/side + 3D) **[S2]** | **[v2]** |

## 2. Viewport and camera

| Feature | Tag | Notes |
|---|---|---|
| Perspective 3D viewport rendered to an offscreen target, shown in a dock window | v1 | MSAA. |
| Fly camera: hold RMB + WASD/QE, Shift for fast | v1 | Unreal muscle memory. Hammer's Z-toggle fly is [later]. |
| Orbit: Alt+LMB. Pan: MMB. Dolly: wheel | v1 | |
| Focus selection / focus at cursor (`F`) | v1 | Scythe: "Focus View at Cursor". Shipped (G): keeps the current distance (2–12 m), ortho keeps zoom; orbit falls back to the last focus point. |
| Reference grid (major/minor lines, axis tint) | v1 | Fades with distance. |
| Shaded and wireframe overlays: boundary edges dashed | v1 (overlay) / v2 (dashed) | Scythe shows open (boundary) edges as dashed lines. Shipped (G): dashed cyan, View > Dashed open edges. |
| Lit / unlit / wireframe-only / UV-checker view modes | v2 | Shipped (G): per pane, header dropdown, `Alt+Z` cycles. UV checker: 64-texel cells, tile borders, red/blue tint when texel density is above/below 512 px/m. |
| Orthographic 2D views with clip/box tools **[S2]** | v2 | Shipped (G): Top / Front / Side via the header dropdown or `Alt+1..4`; 1 / 2 / 4-pane layouts (View menu); RMB/MMB pan, wheel zooms at the cursor, grid in the view plane. The hovered pane is active, so every tool works in any pane. |
| Camera bookmarks (`Ctrl+Shift+1..9` store, `Ctrl+1..9` recall) **[S2]** | v2 | Shipped (G): stored per level in `%APPDATA%/Chip/camera-bookmarks.json`, including the projection. Conflicts with Scythe follow-quads hotkeys inside the UV tool only. |

## 3. Grid, snapping, pivot

| Feature | Tag | Notes |
|---|---|---|
| Power-of-two grid (1/64 m … 16 m), `[` / `]` to step | v1 | Hammer convention. |
| Grid snap for creation and translation (toggle) | v1 | |
| Rotation snap 15° (toggle with the grid snap) | v1 | Shipped (G): separate step (View > Rotation snap 5/15/45/90°). Hold Ctrl mid-drag to bypass snapping (except Ctrl+Shift push/pull). |
| Vertices to Grid (`Shift+Ctrl+Alt+V`) | v1 | |
| Temp Move Pivot: drag the gizmo with MMB. Resets on new selection | v2 | Scythe. Shipped (G): `EditorContext.PivotOverride`; snaps to the grid, or to elements with CapsLock; follows the selection when it moves. |
| Drag Alignment: CapsLock + drag the gizmo snaps the selection to hovered mesh elements | v2 | Scythe. Shipped (G): the pivot snaps to the nearest vertex, edge midpoint or face centre under the cursor, within the handle's axis/plane. |
| Nudge selection (`Alt+Arrows`) by one grid step in the view plane | v1 | Scythe. |
| Grid plane follows the hovered face while a creation tool is active | v1 | Block tool, Shift = place on wall. Shipped (G): a local grid on the face's axis plane (tools opt in via `ViewSettings.ConstructionTools`). |
| Gizmo axes: world / local (selection's main face) / view | v2 | Shipped (G): header dropdown and Tools menu. |

## 4. Selection

| Feature | Hotkey | Tag |
|---|---|---|
| Vertex / Edge / Face / Object modes | `1` `2` `3` `4` | v1 |
| Triangle modes (verts, edges, faces) and toggle topology | `Shift+Ctrl+1/2/3`, `6` | later (n-gon only in v1) |
| Click select. Shift add, Ctrl toggle | LMB | v1 |
| Marquee (box) select on drag from empty space | LMB drag | v1 |
| Select all / invert | `Ctrl+A` / `Ctrl+I` | v1 |
| Clear selection | `Esc` (when no tool is active) | v1 |
| Convert selection on mode switch (edges→faces etc.) | switch mode | v1 |
| Flood fill (faces: connected; edges: loop; verts: connected). Shift adds, Ctrl removes | double-click | v2 (done) |
| Select faces on plane (coplanar flood, 1° from the clicked face) | CapsLock/Mouse4/Mouse5 + double-click | v2 (done) |
| Path selection (shortest path from the active element; by edge length for verts/edges, adjacency for faces) | Ctrl+Shift+click | v2 (done) |
| Grow / shrink selection (faces by shared edges, verts/edges along edges; open borders count as rim) | `Alt+Num+` / `Alt+Num-` | v2 (done) |
| Edge ring / edge loop (quad topology; stops at poles, n-gons, borders; a border edge follows the border) | `X` / `L` | v2 (done) |
| Select by material (active face's, else the active material) / select connected | Select menu | v2 (done) |
| Box select visible only (toggle; off = vertices and edges select through) | Select menu | v2 (done) |
| Select through (x-ray) toggle **[S2]** | | later |
| Selection sets (named) **[S2]** | Select menu, inspector | v2 (done, session only; saving in the doc needs a schema bump) |

## 5. Creation tools

| Tool | Hotkey | Tag | Notes |
|---|---|---|---|
| Add Block | `Shift+B` | v1 | Drag the base on the grid plane, release, move for height, click to confirm. Ctrl at the start of the drag flips the base plane toward the camera: the plane passes through the start point and uses the world axis that faces the view most (other than the surface normal), so a base can be drawn face-on. The base starts on the hovered face, or on the grid when no face is under the cursor. `Enter` accepts, `Esc` cancels. Uses the active material and world-aligned UVs. |
| Add Rectangle (single quad, open plane) | `Shift+R` | v1 | Drag like a block base, release to create one quad facing the camera's side of the plane. Its four boundary edges feed edge-extrude workflows. Ctrl stands the plane up toward the camera. |
| Primitive shapes: cylinder, wedge, stairs, arch, sphere, torus, spiral stairs **[S2]** | | v1 | Shape dropdown in the block tool's inspector section, with per-shape parameters remembered for the session: cylinder sides; stair steps by count or by step height; arch segments, arc (5 to 180 degrees) and band width; sphere rings and segments; torus segments, sides and tube size; spiral stairs steps, turn, inner opening, solid-to-floor and direction; Rotate 90° for wedge, stairs and arch; Smooth (soft edges) for curved shapes. Every shape exactly fills the dragged box and previews as a wireframe while you set the height. Wedges, stairs and arches run along the longer side of the base, in the drag direction. Palette commands `block.shape<Name>`. |
| Polygon draw tool (click points, extrude) **[S2]** | `Shift+P` | v1 | Click snapped points on a surface or the grid (Ctrl on the first click stands the plane up toward the camera). Backspace removes the last point. Enter or a click on the first point closes the outline, and outlines that cross themselves are refused. Move to set the height, then click or press Enter to create a closed prism. Concave outlines are supported. |
| Path/pipe extrude along spline **[S2]** | | later | |

## 6. Geometry commands

Commands act on the current selection and mode. Each command is one undo step.

| Command | Hotkey | Tag | Behaviour |
|---|---|---|---|
| Move / Rotate / Scale gizmo | `W` / `E` / `R` | v1 | Axis, plane and screen handles. Snapped. |
| Extrude faces | Shift + drag gizmo (face mode) | v1 | Duplicates the selected region, bridges its boundary with quads, then moves. |
| Push/pull faces (extrude, then move along normal, merging coplanar sides) | Shift+Ctrl + drag gizmo | v2 shipped | Motion is constrained to the faces' average normal. Outline vertices whose neighbours already contain that direction slide instead of extruding, so pushing a box face resizes the box with no new faces; side faces left flush with a neighbour merge into it. |
| Extrude boundary edges | Shift + drag gizmo (edge mode) | v2 shipped | Boundary edges only; the new outer edges become the selection. Closed edges just move. |
| Rotate-extrude (Shift while rotating) | Shift + drag rotate gizmo | later | |
| Delete selection | `Del` | v1 | Faces → remove. Verts/edges → remove adjacent faces. Objects → remove mesh. |
| Duplicate | `Shift+D` | v1 | Object mode: copy the mesh. Face mode: copy the faces into the same mesh. |
| Flip / invert normals | `Shift+N` | v1 | |
| Merge Meshes | `M` | v1 shipped | Object mode, ≥2 meshes → one mesh (the active one, else the first). Vertices are not welded. |
| Detach faces → new mesh | `N` | v1 shipped | The new mesh's faces stay selected. |
| Extract (detach a copy) | `Alt+N` | v1 shipped | |
| Soft / Hard / Dominant normals | `J` / `H` / `K` | v2 | Per-edge smoothing flags. Export smooth groups. |
| Connect (split faces between selected verts/edges) | `V` | v2 shipped | Vertices: chords between selected corners of each face. Edges: midpoints, joined across faces holding two or more (edge-ring connect). |
| Merge Faces (dissolve shared edges) | `Backspace` | v2 shipped | Each connected region becomes one n-gon with the active face's surface. Regions with holes are skipped. Leftover straight-through vertices are dissolved. |
| Bridge (two faces or two edge loops) | `Shift+C` | v2 shipped | Same mesh, same corner/edge count. Faces are removed and their outlines joined by world-aligned quads. Open edge chains or closed loops of boundary edges. |
| Inset / Outset | `I` / `O` | v2 shipped | Interactive amount: previews the last width, move the mouse to change it (grid-snapped), click/Enter applies, Esc cancels; width also in the inspector. Region-aware. Outset keeps the faces' size and pushes the old outline outward. |
| Weld Overlapping verts | `Shift+Ctrl+Alt+M` | v1 shipped | Distance threshold 1 mm, per selected mesh (selected vertices only in vertex mode). |
| Bevel (edges/verts, segments) | `B` | v2, shipped | Tool: drag sets the width, 1–8 sets segments while dragging (1 = chamfer, more = rounded, soft-shaded). Corners where bevelled edges meet are patched; meshes stay closed. Vertices chamfer flat. Exact values in the Tool section or Mesh > Bevel selection. |
| Cut / knife (draw a line across faces) | `C` | v2, shipped | Click points (snapped to vertices, edges, grid); Enter cuts every face the line crosses edge to edge, as seen from the camera. Selected faces if any, else the meshes under the line. Backspace removes the last point. |
| Clip (plane split, keep front/back/both) | `Shift+X` | v2, shipped | Click or drag two points; the plane is perpendicular to that surface (or along the view). Tab cycles keep front/back/both; Enter clips the selected meshes, else every visible mesh the plane crosses. World-aligned caps (cut face or active material); holes are bridged. |
| Mirror (across pivot axis) | `Shift+Z` | v2 shipped | In place, across the selection pivot, along the horizontal world axis closest to the view's left-right. Faces fully inside the selection flip winding. |
| Collapse / merge verts to centre **[S2]** | | v2 shipped | Mesh menu. Vertices merge to one point; each connected edge chain or face region collapses to its own centre. |
| Quad slice / subdivide **[S2]** | | later | |
| Boolean subtract/union **[S2-ish]** | | later | |

## 7. Materials

| Feature | Hotkey | Tag | Notes |
|---|---|---|---|
| Active Material (shown in the inspector and the lower-left chip) | | v1 | |
| Built-in blockout materials 1–10 (procedural dev grids: orange, grey, dark, blue, green, red, yellow, purple, white, black) | `Shift+Alt+1..0` | v1 | Generated in code, so no asset dependency. |
| Apply material to selected faces (or whole mesh in object mode) | `Shift+T` | v1 | |
| Lift material from the hovered face | `Shift+RMB` | v1 | |
| Material browser (thumbnails grid, search) | `T` | v1 (palette) / v2 (browser) | **Shipped (I).** Floating window over the viewport: search, folder tree, thumbnail size, favourites and recent (in memory until settings land), hover details. Click = active, double-click = apply, drag onto a face = apply there. Lazy thumbnails (on-screen only, worker-thread decode) keep 500+ textures smooth. |
| Import material library from a folder of images (PNG/JPG/TGA). Texture size drives texel density | | v2 | **Shipped (I).** Sizes come from file headers. Sources: bundled `assets/textures` and `assets/tools`, `CHIP_TEXTURE_DIRS` (`;`-separated), and Texture > Add texture folder (this session only; stream K persists). Texture > Rescan texture folders. Name aliasing: `_Albedo`/`_BaseColor`/`_Diffuse`… are stripped and other PBR maps (`_Normal`, `_Roughness`…) are skipped. Exports embed JPG as JPEG and convert TGA to PNG. |
| Per-material texel density override | | v2 | **Shipped (I).** The inspector's "Tiles every" field. Saved to `chip-materials.json` in the texture's folder. New blocks use it, and so does Texture > Reset scale to material density. Apply, replace and drop keep each face's scale relative to its material's density. |
| Tool materials `nodraw`, `clip`, `trigger`, `skip` **[S2]** | | v2 | **Shipped (I).** Bundled striped textures. Export: nodraw and skip never render. Clip faces, and nodraw faces of a colliding mesh, go to a `name_clip-colonly` node. Trigger faces go to `name_trigger-colonly` with `extras.chip_tool = "trigger"`. |
| Replace material across the level | | v2 | **Shipped (I).** Texture > Replace material… is one undo step. |
| Engine material remap on export (Chip material name → `res://` path) | | v2 | Godot import script or `.tres` side-car. |
| Replace blockout materials (settings) | | later | |

## 8. Texturing and UVs

Each face stores a planar projection: U axis, V axis, offset (u, v), scale (u, v) and rotation. UVs are derived from world position, as in Hammer's face edit. Moving geometry keeps world-aligned textures stable. Explicit per-vertex UVs (from unwraps or hotspots) override the projection **[v2]**. Precedence: a face with `Face.UVs` set shows those UVs everywhere (render, export, picking) and its projection is ignored; clearing them (Texture > Clear explicit UVs) brings the projection back. Explicit UVs stay on the geometry through any move, so texture lock only affects projected faces. Justify, fit, shift, scale, rotate and flip keep a projected face projected (they are exact projection edits); unwraps, Flow, wraps from an explicit face and Fast Texture Tool edits write explicit UVs.

| Feature | Hotkey | Tag |
|---|---|---|
| Align to Grid (world-aligned, dominant-axis projection, texel density) | `Shift+Ctrl+G` | v1 |
| Align to Face (face-plane projection from the longest edge) | `Shift+Ctrl+F` | v1 |
| Shift U/V by one step (8 directions) | `Numpad 1–4, 6–9` | v1, done |
| Scale X/Y up/down | `Ctrl+Alt+Numpad 4/6/8/2` | v1, done |
| Rotate CW / CCW (15°) | `Alt+Numpad 9 / 7` | v1, done |
| Fit to face | `Ctrl+Alt+Numpad5` | v1, done |
| Justify left/right/top/bottom/centre | `Alt+Numpad 4/6/8/2/5` | v2, done |
| Flip texture horizontal/vertical | `Alt+D` / `Alt+E` | v1, done |
| Flip face UV horizontal/vertical | `Alt+R` / `Alt+T` | v2, done |
| Modify Texture panel: numeric shift/scale/rotation, Fit to Height/Width, Keep Ratio, Treat as One | | v1 (numeric) / v2 (fit options), done |
| Texel density setting (px per metre, default 256) | | v1 (global) |
| Wrap Texture onto the hovered adjacent face (continue the projection across the edge, copy the material) | `Alt+RMB` | v2, done |
| Wrap Texture to Selection | `Shift+Alt+RMB` | v2, done |
| Flow Texture along curves (follow quads) | `Ctrl+Alt+RMB` | v2, done |
| Texture lock toggle for moves **[S2]** | | v2, done |
| **Fast Texture Tool** (`Shift+Q`): a 2D UV editor overlay with a grid (`[`/`]`), frame (`F`), reset (`R`), rotate (`Q`/`E`), flip (`Alt+R/T`), repeat background (`B`), coordinates (`C`), original material (`T`), world scale (`G`). Unwraps: Use Existing (1), Conforming (2), Square (3), Follow Quads (4), Planar (5). Follow-quads spacing: Length / Even / Length Avg (`Ctrl+1/2/3`). The blue rect shows the ideal texel-density footprint | | v2, done |

## 9. Hotspots

| Feature | Tag | Notes |
|---|---|---|
| Hotspot asset (`.hotspot.json`): one per pattern, listing target materials + rects | v2 | Resolution independent (normalised rects). |
| Hotspot editor: draw rects on the texture with a grid (`[`/`]`), pan RMB, zoom wheel, per-rect flags: Allow Rotation (`Alt+A`), Mirror X (`Alt+D`), Mirror Y (`Alt+E`), Tiling (`Alt+T`, full width/height only), Inset px; toggles: lit, hide texture (`Alt+V`), hide rects (`Alt+B`). List shows `[R][X][Y][T][Inset]` | v2 | |
| Apply Hotspot (`Shift+H`) to coplanar-connected regions, Individual Faces (`Shift+F`) | v2 | Best fit by aspect ratio and size at the texel density. Random mirror and rotate when allowed. |
| Apply options: Tiling (No / Allow / Only), Disallow Random, Don't Apply Material, Mapping (Automatic / Square / Conforming / Follow Active Quads) | v2 | |
| Apply a selected patch from the editor | v2 | |
| Overlapping patches as extra fit candidates | v2 | |
| Hotspot texture maker (layered masks) | out of scope | Engine TextureGraph feature. Use Substance or Material Maker. |

## 10. Scene organisation [S2]

| Feature | Tag |
|---|---|
| Outliner (mesh list, rename, hide, lock) | v2 |
| Visgroups / layers, hide/show by group | later |
| Entities / markers (player start, lights, generic point entity with key-values) exported as glTF empty nodes with `extras` | v2 |
| Prefabs / instances (referenced `.chip` files) | later |
| Collision-only meshes / nodraw-style tool materials (`tools/clip`, `nodraw`, `trigger`) exported with a name suffix (`-col`, `-colonly`) for Godot's import hints | v2 |

## 11. Interop

| Feature | Tag |
|---|---|
| Export `.glb` (meshes, materials, embedded textures) | v1 |
| Godot import hints: `-col`/`-colonly` suffixes, per-node `extras` | v2 |
| Re-export on save (watch mode) so Godot hot-reloads | v2 |
| OBJ export | later |
| Import `.glb`/`.obj` as an editable mesh | later |
| Source `.vmf` / UE `.t3d` import | later |

## 12. Architecture (v1)

```
Chip.Core   (no GL, unit-tested)
  Geometry/   PolyMesh, Triangulator, MeshOps (box, extrude, delete, flip, duplicate, snap)
  Texturing/  TextureMapping (planar projection, align grid/face, fit, rigid texture lock)
  Scene/      Document, MaterialLibrary, Selection (mode + element set + conversion), Session (edit/undo surface)
  Picking/    Ray and screen-space picking of vertices, edges, faces, meshes; marquee
  IO/         DocumentSerializer (.chip JSON v1), GlbExporter
Chip.Editor (Silk.NET window + OpenGL 3.3 + ImGui.NET, IBM Plex Mono Regular/SemiBold, OFL)
  EditorApp   frame loop, fixed panel layout, hotkeys, command dispatch
  Rendering/  Renderer (scene, wire, selection overlay, shader grid) into an MSAA FBO
  Viewport/   Camera, Gizmo (translate/rotate/scale; Shift = extrude), BlockTool
  UI/         Theme, dark title bar; panels in EditorApp.Panels
  --shot      hidden-window capture of the full editor UI (docs/ui/*.png)
Chip.Core.Tests (xUnit)
```

Seam: the editor edits only through `Session` (`Begin/End/Cancel`, `Do`, `Undo/Redo`). Session owns the `Document`, the `Selection` and the snapshot history, and it bumps a version that the renderer watches. Topology changes go through `MeshOps`. Rendering is read-only. GL, windowing and input are the only platform adapters.

## 13. v1 acceptance (scenario, per review)

Build **two rooms joined by a doorway and a corridor, plus a ramp**:

1. The app launches to a dark, fixed panel layout (tool strip, viewport, inspector, status bar) with the orange accent and a grid.
2. Camera: RMB+WASD fly, Alt+LMB orbit, MMB pan, wheel dolly, `F` focus.
3. `Shift+B` drags blocks snapped to the grid (`[`/`]`). Blocks start on the hovered surface. Inspector **numeric position/size** gives exact wall thickness and doorway width.
4. `1/2/3/4` modes with click, shift/ctrl, marquee, `Ctrl+A`, `Ctrl+I`, `Esc`, and conversion on mode switch.
5. `W/E/R` gizmo with snapping on any mode. Moving a box's top edge down makes the ramp. Shift-drag on faces extrudes the corridor.
6. `Del`, `Shift+D`, `Shift+N`, `H` hide selected, `Alt+H` unhide all.
7. `Shift+Alt+1..0` / palette picks a material, `Shift+T` applies, `Shift+RMB` lifts.
8. `Shift+Ctrl+G` / `Shift+Ctrl+F` / `Ctrl+Alt+Num5` fit, plus inspector shift/scale/rotation/flip.
9. Undo and redo cover every committed gesture. `Esc` mid-drag restores the state.
10. Save, then reopen round-trips. New/Open/Quit with unsaved changes asks Save / Discard / Cancel.
11. `Ctrl+E` writes `.glb` atomically, scaled to metres, with dev textures. The static-collision toggle names nodes `-col`, so Godot builds trimesh colliders and you can walk the level. Re-export after an edit keeps placement.

## 14. v1 non-goals

Triangle-topology modes, the Fast Texture Tool, hotspots, bevel/cut/clip/connect/bridge/inset, weld/merge/detach/extract, numpad UV hotkeys, 2D views, primitives beyond the box, the outliner, entities, gizmo orientation modes, stable element IDs, per-corner UVs, hotkey remapping, and engine live-link.

## 15. Review log

- R1 (GPT-6.1-Sol): adopted scenario acceptance; numeric entry; hide/reveal; save protection; export collision; versioned schema; atomic export; stable export origin. Cut weld/detach/extract/merge and numpad UV keys from v1. **Declined:** removing vertex/edge editing from v1, because the ramp needs it and it shares the one vertex-set transform path. Clip and Mirror are Scythe commands too (not S2-only). Risks around stable IDs, per-corner attributes and n-gon validity are recorded in §0 for v2.
- R2 UI review (GPT-6.1-Sol + frontend-design pass): all 17 issues and both optional ideas adopted. Orange now means only "what you're editing". Persistent settings (snap, checkboxes) are neutral and the wordmark is neutral. Selection and gizmo strokes get a dark backing so they read on orange dev textures. The grid is quieter and sits behind coplanar floors (polygon offset, not draw order). Icon tool strip with visible keys. X/Y/Z-tagged numeric fields. "Mixed" texture values, with per-component edits. Sentence-case headings, with weight carrying the hierarchy. Disabled states. A status bar with fixed selection and grid/snap fields and persistent errors. An empty-scene invitation. Export settings collapsed. The font moved to IBM Plex Mono for glyph coverage (×, arrows, dashes) and a SemiBold weight. Deviation: "Align to grid" / "Align to face" are shortened to "Align grid" / "Align face" so three buttons fit one row; tooltips carry the full names.
- R3 compact pass (user: "bulky"): 14 px body type, 21 px controls, 40 px tool strip, 280 px inspector, 22 px status bar in small type. Inspector rows put the label in a fixed left column. X/Y/Z and U/V fields share one tagged-field pattern. Helper copy moved into tooltips. The texture toolbar is a single row (Grid, Face, Fit, flip arrows). Export settings tucked behind a tree node.
- R4 (user): 16 px body / 13 px small at the monitor's DPI scale. Metric units with 512 px/m texel density. Backface selection mode (Alt+B, tool strip, View menu) that picks faces pointing away and renders them darkened. Font rasteriser tuned (3x/2x oversampling, pixel-snapped advances, 1.12 coverage). Removed the redundant `--render` mode in favour of `--shot`.
- R5 (user: icons "wigged"): replaced the hand-drawn vector glyphs with the Tabler icon font (MIT, assets/fonts/tabler-icons.ttf; only the 13 glyphs used go into the atlas, at 18 logical px, pixel-aligned). The texture flip buttons use the flip-horizontal/vertical glyphs.
- R6 UI foundation pass (frontend-design): each panel has one job. The tool strip is tools only. A new viewport header holds the selection-mode segmented control (icon + label, orange underline), the view name, and grid/snap/backfaces. The status bar only reports (selection, hints/messages, scene counts). The menu bar shows the document name and saved/unsaved state. The F1 keyboard-shortcuts sheet is grouped by task. The block tool shows a snapped construction cursor before the first click. A corner world-axis indicator. `UI/Widgets` is the shared vocabulary (ToolButton, Segment, Toggle, IconButton, TaggedFields, Heading, RowLabel, KeyHints), with three states: active = warm fill + orange bar/underline, setting on = neutral raised, hover = frame.
