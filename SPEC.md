# Chip: standalone level-blockout editor (Scythe and Source 2 Hammer feature set)

Chip is a standalone C# level editor. It covers the Scythe workflow (Unreal plugin) and Source 2 Hammer mesh editing, and it exports to any engine. Godot is the primary target. Chip owns the editable polygon mesh and exports finished geometry. It does not run inside an engine.

Sources: scytheeditor.com/guide (Overview, Quick Start, Hotkeys, Hotspots, Hotspot Texture Maker) and Source 2 Hammer behaviour.

Tags: **[v1]** = first shippable slice. **[v2]** = next. **[later]** = backlog. **[S2]** = from Source 2 Hammer, not in the Scythe docs.

---

## 0. Ground rules

- **Coordinates:** Y-up, right-handed, metres. The grid runs 1, 5, 10, 25, 50 cm, then 1, 2, 4, 8, 16 m (default 25 cm). Texel density is per level, Scythe style ("1 m is 256 px" for new levels; files saved before the setting load at 512 px/m, the scale they were built at). `Document.ExportScale` (default 1) multiplies on export: 1 for Godot, 100 for centimetre engines. glTF and Godot need no axis swizzle.
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
| Fixed panel layout with resizable splitters (v1; dock v2) | Menu bar, left dock, tool strip, centre viewport, right inspector, bottom status bar. The dock holds what you pick from as tabs (`IDockPanel`): Materials (`T`), Scene (`Ctrl+Shift+O`), Place and Dressing (`Ctrl+Shift+D`); a tab's key (or clicking the showing tab) collapses it to an icon rail, `Ctrl+\` toggles it, and the tab and state persist. The inspector is the selection only, with mode-aware mesh operation buttons and foldable sections (persisted); with nothing selected it shows the Level (texel density, export, export on save, dressing rules). The menu bar has a command search field (the palette) and Export with its options. Every command is also in a menu or the palette with its hotkey. Dock, tool strip and inspector widths drag and persist (double-click an edge to reset; View > Reset panel sizes). Docking is not planned while ImGui.NET lacks DockBuilder. |
| Status bar | Selection mode, element count, grid size, snap state, active tool hint. |
| File: New / Open / Save / Save As (`Ctrl+N/O/S`, `Ctrl+Shift+S`) | JSON `.chip` document. |
| Export glTF binary (`Ctrl+E`) | `.glb` with one node per mesh, one primitive per material, per-corner normals (flat, or smoothed across soft edges), UVs, and embedded dev textures (PNG). |
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
| Grid 1 cm … 16 m, `[` / `]` to step | v1 | Shipped: 1, 5, 10, 25, 50 cm, then 1, 2, 4, 8, 16 m. Below 25 cm the steps are round numbers rather than Hammer's halvings (12.5, 6.25 cm). 10 cm doesn't nest in 25 cm; 5 cm nests in both. Brighter lines at a round size the step divides (1 → 10 cm, 5 → 25 cm, 10 → 50 cm, 25 cm → 1 m, then ×4), plus the tier above that, which stays visible when the finer lines thin out with distance. |
| Grid snap for creation and translation (toggle) | v1 | |
| Dimensions beside the selection (Scythe) | | v2 shipped | View > Show dimensions (on by default). Faces: the outline of each selected region and creases inside it, so a wall shows its width and height and a whole box its three sizes, while a floor split into coplanar faces shows only its outline. Edges: each selected edge. Two vertices: the distance between them. Objects: the bounding box's three sizes. Parallel edges of equal length get one label, on the one nearest the camera. Labels sit just outside the shape, coloured by the world axis the edge runs along (neutral otherwise), to the tenth of a millimetre (1.125 m, 12.5 cm); edges too short on screen and labels that would overlap are skipped. Overall size: two or more faces (or edges, or three vertices) are also measured as one shape, faces in one sloped plane along that plane, anything else by its bounding box, drawn as dimension lines further out (extension lines, end ticks, framed size). A side an edge label already gives is left out, so two stacked wall faces add only their combined height. View > Dimensions: edges and overall (default), edges, or overall. The block, rectangle, polygon and knife tools show the same labels while drawing (box sizes, each segment, the extrude height). |
| Gizmo drag readout and exact values | type during a drag | v2 shipped | While dragging the gizmo, a tag beside it shows how far: per-axis distance (gizmo axes, coloured) and the straight-line total for move, extrude and push/pull; the angle for rotate; the factor and new size for scale. A faint line runs back to where a move started; in World snap space a diamond marks the point sitting on the grid. Typing a digit, point or minus during the drag enters an exact value (the mouse stops steering and letting go no longer ends the drag): one number along an axis handle (or along the way a plane drag is heading), two for a plane handle, three for all gizmo axes; metres unless cm or mm (150cm), degrees for rotate, a factor or percentage for scale (150%). It previews live; Enter applies (one undo step), Backspace edits, Esc cancels. |
| Snap space: World / Relative (`\`) | v2 shipped | Header dropdown beside Snap; View > Snap to world grid; starting value in Preferences > Viewport (World by default). World: gizmo moves (and Shift-extrude, push/pull) put the selection on world grid lines along the axes being dragged; the temporary pivot lands on the line if one is placed, else the selection's bounds' low corner (Hammer), so a box whose corners are on the grid keeps them there. A gizmo axis that isn't a world axis (local or view axes on a turned selection) moves in whole steps. Nudge goes to the next grid line, then whole steps. Relative: moves go in whole grid steps from where the selection was, so off-grid geometry stays off by the same amount. Rotate and scale snap their own amounts either way; creation tools, the knife, clip and the arc axis always snap to world positions; inset, offset and bevel snap distances. A press on the gizmo that doesn't move moves nothing. |
| Rotation snap 15° (toggle with the grid snap) | v1 | Shipped (G): separate step (View > Rotation snap 5/15/45/90°). Hold Ctrl mid-drag to bypass snapping, including Ctrl+Shift push/pull. |
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
| Overlapping objects (object mode): a glyph under the cursor wins, then the box the ray leaves first (an entity inside a trigger volume beats the volume), then everything by depth (a wall inside a volume beats the volume; a volume around the camera comes last). Clicking again on the same spot, even on the gizmo, selects the next object under the cursor; a tag beside the cursor names the hovered entity and says "2 of 3" | LMB again | v2, shipped |
| List everything under the cursor, nearest first; hover outlines, click selects (Shift adds, Ctrl toggles) | `Ctrl+RMB` / `Alt+S` | v2, shipped |
| Pick filter for object mode: everything, geometry only, or entities only (entities and mesh entities' meshes). Applies to clicks, paint, box select and select all; the header shows it next to the modes | `5` (entities only, again for everything) | v2, shipped |
| Paint select (Scythe): drag selects everything the cursor sweeps over; Shift+drag paints into the selection, Ctrl+drag paints out of it | LMB drag | v2, shipped |
| Box (marquee) select, Unreal-style; Shift adds | `Ctrl+Alt+drag` | v1 (moved off plain drag) |
| Select all / invert | `Ctrl+A` / `Ctrl+I` | v1 |
| Clear selection | `Esc` (when no tool is active) | v1 |
| Convert selection on mode switch (edges→faces etc.) | switch mode | v1 |
| Flood fill (faces: connected; edges: loop; verts: connected). Shift adds, Ctrl removes | double-click | v2 (done) |
| Select faces on plane (coplanar flood, 1° from the clicked face) | CapsLock/Mouse4/Mouse5 + double-click | v2 (done) |
| Path selection (shortest path from the active element; by edge length for verts/edges, adjacency for faces) | Ctrl+Shift+click | v2 (done) |
| Grow / shrink selection (faces by shared edges, verts/edges along edges; open borders count as rim) | `Alt+=` / `Alt+-` (or `Alt+Num+` / `Alt+Num-`) | v2 (done) |
| Edge ring / edge loop (quad topology; stops at poles, n-gons, borders; a border edge follows the border) | `X` / `L` | v2 (done) |
| Select by material (active face's, else the active material) / select connected | Select menu | v2 (done) |
| Box select visible only (toggle; off = vertices and edges select through) | Select menu | v2 (done) |
| Select through (x-ray) toggle **[S2]** | | later |
| Selection sets (named) **[S2]** | Select menu, inspector | v2 (done, session only; saving in the doc needs a schema bump) |

## 5. Creation tools

| Tool | Hotkey | Tag | Notes |
|---|---|---|---|
| Add Block | `Shift+B` | v1 | Drag the base on the grid plane, release, move for height, click to confirm. Shift at the start of the drag flips the base plane toward the camera: the plane passes through the start point and uses the world axis that faces the view most (other than the surface normal), so a base can be drawn face-on. The base starts on the hovered face, or on the grid when no face is under the cursor. `Enter` accepts, `Esc` cancels. Uses the active material and world-aligned UVs. |
| Add Rectangle (single quad, open plane) | `Shift+R` | v1 | Drag like a block base, release to create one quad facing the camera's side of the plane. Its four boundary edges feed edge-extrude workflows. Shift stands the plane up toward the camera. |
| Primitive shapes: cylinder, wedge, stairs, arch, sphere, torus, spiral stairs **[S2]** | | v1 | Shape dropdown in the block tool's inspector section, with per-shape parameters remembered for the session: cylinder sides; stair steps by count or by step height; arch segments, arc (5 to 180 degrees) and band width; sphere rings and segments; torus segments, sides and tube size; spiral stairs steps, turn, inner opening, solid-to-floor and direction; Rotate 90° for wedge, stairs and arch; Smooth (soft edges) for curved shapes. Every shape exactly fills the dragged box and previews as a wireframe while you set the height. Wedges, stairs and arches run along the longer side of the base, in the drag direction. Palette commands `block.shape<Name>`. |
| Polygon draw tool (click points, extrude) **[S2]** | `Shift+P` | v1 | Click snapped points on a surface or the grid (Shift on the first click stands the plane up toward the camera). Backspace removes the last point. Enter or a click on the first point closes the outline, and outlines that cross themselves are refused. Move to set the height, then click or press Enter to create a closed prism. Concave outlines are supported. |
| Path/pipe extrude along spline **[S2]** | | later | |

## 6. Geometry commands

Commands act on the current selection and mode. Each command is one undo step.

| Command | Hotkey | Tag | Behaviour |
|---|---|---|---|
| Move / Rotate / Scale gizmo | `W` / `E` / `R` | v1 | Axis, plane and screen handles. Snapped. |
| Extrude faces | Shift + drag gizmo (face mode) | v1 | Duplicates the selected region, bridges its boundary with quads, then moves. |
| Push/pull faces (extrude, then move along normal, merging coplanar sides) | Shift+Ctrl + drag gizmo | v2 shipped | Motion is constrained to the faces' average normal. A connected outline slides only when all its vertices' neighbours contain that direction, so pushing a box face resizes the box with no new faces. Otherwise the whole outline extrudes with quads, avoiding triangular gussets at partially supported edges; side faces left flush with a neighbour merge into it. |
| Extrude boundary edges | Shift + drag gizmo (edge mode) | v2 shipped | Boundary edges only; the new outer edges become the selection. Closed edges just move. |
| Rotate-extrude (Shift while rotating) | Shift + drag rotate gizmo | v2 shipped | Shift extrudes with every gizmo (rotate and scale too; Ctrl+Shift push/pull stays on the move gizmo). MMB-drag the gizmo to put a temporary pivot beside the selection, then each Shift+rotate drag adds one arc segment. The pivot survives the extrude (including edge extrude, which replaces the selection) so the drags can be repeated. |
| Arc extrude (faces or open edges round an axis) | `Shift+A` | v2 shipped | Tool for curved corridors, pipes, coves and arches: the rotate-extrude done in one go. Live at the last angle (90°) and segments (8). Point to the side to bend toward and slide the axis out along it like a gizmo arrow (held to the four square directions and onto grid lines, Ctrl free; never inside the selection), click, then point where the arc should end (rotation snap step, Ctrl free); click or Enter applies, Esc cancels. A guide circle with snap marks (longer every 90°) shows the path, the built part bright. Distances read as the inner radius (axis to the selection's nearest side), remembered between uses. Wheel or 1–9 for segments (up to 64). Faces sweep away from the mesh they're attached to (an end cap that faces back into its corridor still sweeps out of the end); a loose patch follows its normal; Tab reverses. Angle, segments, radius, Reverse and Smooth (soft edges between steps) are in the Tool section. Faces: the cap stays selected; open edges: the far edges become the selection. An axis on the selection's edge folds that edge into fans. One undo step. |
| Delete selection | `Del` | v1 | Faces → remove. Verts/edges → remove adjacent faces. Objects → remove mesh. |
| Duplicate | `Shift+D` | v1 | Object mode: copy the mesh. Face mode: copy the faces into the same mesh. |
| Flip / invert normals | `Shift+N` | v1 | |
| Merge Meshes | `M` | v1 shipped | Object mode, ≥2 meshes → one mesh (the active one, else the first). Vertices are not welded. |
| Detach faces → new mesh | `N` | v1 shipped | The new mesh's faces stay selected. |
| Extract (detach a copy) | `Alt+N` | v1 shipped | |
| Soft / Hard / Dominant normals | `J` / `Ctrl+J` / `K` | v2 shipped | Per-edge smoothing flags (`H` stays hide, so Hard is `Ctrl+J`). Edges act on themselves, vertices on the edges between them, faces on all their edges, objects on the whole mesh. Soft/Hard on faces or objects also clear dominance. Dominant (face mode): the faces stay flat and neighbours meeting them at under 60° (bevels, chamfers) take their normal at shared corners; saved as an optional per-face `dominant` flag. Hard edges split smoothing per vertex, so one hard seam creases a soft surface. Viewport and glTF export use the same per-corner normals. Edge mode draws soft edges dotted; elsewhere they are faint. Smoothing section in the inspector. |
| Auto-smooth by angle / Clear smoothing | | v2 shipped | Mesh menu. Auto-smooth: soft where the two faces meet at less than the angle (Smoothing section, 45° default, 30/45/60 presets), hard elsewhere, over the selection's edges. Clear smoothing: every edge of the selected meshes hard, no dominant faces. |
| Show normals | | v2 shipped | View menu. Short lavender strokes along each corner normal of the selection's faces. |
| Connect (split faces between selected verts/edges) | `V` | v2 shipped | Vertices: chords between selected corners of each face. Edges: midpoints, joined across faces holding two or more (edge-ring connect). |
| Merge Faces (dissolve shared edges) | `Backspace` | v2 shipped | Each connected region becomes one n-gon with the active face's surface. Regions with holes are skipped. Leftover straight-through vertices are dissolved. |
| Bridge (two faces or edge chains) | `Shift+C` | v2 shipped | Same mesh. Faces: same corner count; both are removed and their outlines joined by world-aligned quads. Edges: any number of open chains or closed loops of boundary edges, paired two by two (each with the partner it bridges to most directly without a flat or twisted face). Chain ends that sit on top of each other count as one corner and are welded. Equal edge counts join with quads; unequal ones match corners by distance along each chain, with triangles where only one side has a corner. |
| Fill holes | `Alt+F` | v2 shipped | Caps each hole (loop of open edges) the selection touches with one n-gon: one rim edge or vertex is enough; in object mode every hole. The cap faces the same way as the surface around it, takes the rim's most common material (world-aligned), and keeps tags only from a rim face facing the same way. Holes whose outline crosses itself are skipped; caps that aren't flat are reported. Selects the new faces. |
| Inset / Outset | `I` / `O` | v2 shipped | Interactive amount: previews the last width, move the mouse to change it (grid-snapped), click/Enter applies, Esc cancels; width also in the inspector. Region-aware. Outset keeps the faces' size and pushes the old outline outward. |
| Offset faces (extrude along their own normals) | `Shift+O` | v2 shipped | For trims and baseboards: select a strip of wall faces round a room and push it out. Each region is extruded and every face plane moves by the same distance along its own normal, with mitred corners (even thickness). Interactive like inset: previews the last distance, move right/left to push out/pull in (negative recesses), grid-snapped, Ctrl for free, click/Enter applies, Esc cancels. Side faces are world-aligned. A closed region (a whole box) inflates in place. An open patch (a lone Shift+R rectangle) keeps a reversed cap where it started, so it becomes a closed slab (Unreal's shells to solids), turned round for negative distances. Not in Scythe's guide; this is Unreal Modeling Mode's PolyGroup Edit offset in "Selected Triangle Normals Even" mode (mitres capped at 4× the distance). |
| Weld Overlapping verts | `Shift+Ctrl+Alt+M` | v1 shipped | Distance threshold 1 mm, per selected mesh (selected vertices only in vertex mode). |
| Bevel (edges/verts, segments) | `B` | v2, shipped | Tool (Source 2 style): the bevel is live as soon as the tool opens, at the last width and segments, and stays adjustable until Enter (Esc or Ctrl+Z cancels; switching tools applies). Drag to change the width relative to where the drag started (pull away from the edges to widen); it snaps to the grid, Ctrl for free, `[` `]` change the grid mid-bevel. The mouse wheel or 1–9 set segments (up to 16; 1 = chamfer, more = rounded, soft-shaded). Corners where bevelled edges meet are patched; meshes stay closed. Vertices chamfer flat. Exact values in the Tool section or Mesh > Bevel selection. |
| Cut / knife (draw a line across faces) | `C` | v2, shipped | Click points (snapped to vertices, edges, grid); Enter cuts every face the line crosses, as seen from the camera, into every region the line divides it into. The line may cross itself, double back or close a loop: a stroke round a square and on to the border cuts the square out as its own face. An end that stops inside a face without meeting the rest of the line carries on in its direction to the first border or cut line (Scythe / Source 2), so a stroke from the bottom edge that stops halfway cuts to the top. Faces can't hold holes, so a closed loop touching nothing gets two straight bridges out to its surroundings, and a region left wrapped round a piece it touches at one point gets one. A face that crosses itself is untangled by the cut. Selected faces if any, else the meshes under the line. Backspace removes the last point. |
| Dissolve vertices / edges | `Ctrl+Backspace` | v2 shipped | Removes the selection but keeps the faces round it (Blender's dissolve). A vertex between two edges leaves every face using it, so a stray point on a straight edge just goes; where more edges meet, the faces round it merge first (only if they lie in one plane). Edges merge the two faces on them, like Merge Faces. Open edges are skipped. |
| Check mesh | | v2 shipped | Mesh menu. Finds degenerate, pinched and self-crossing faces, T-junctions (a vertex on a neighbour's open edge), edges with more than two faces and neighbours wound against each other, in the selected meshes (all if none). Selects them and reports counts. Open edges alone are fine. Core tests run it after every mesh op. |
| Clean up mesh | | v2 shipped | Mesh menu, one undo step per use: welds vertices within 1 mm, untangles self-crossing or pinched faces, sews T-junctions (the vertex goes into the long edge, so the faces share edges again), removes faces with no area, and dissolves vertices that only continue a straight edge (left behind when what split the edge was deleted, e.g. a door's corners in the floor). |
| Clip (plane split, keep front/back/both) | `Shift+X` | v2, shipped | Click or drag two points; the plane is perpendicular to that surface (or along the view). Tab cycles keep front/back/both; Enter clips the selected faces in face mode (as Scythe does: other faces only gain the cut points, and Both unwelds the two halves so they pull apart), else the selected meshes, else every visible mesh the plane crosses. World-aligned caps (cut face or active material); holes are bridged. |
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
| Material browser (thumbnails grid, search) | `T` | v1 (palette) / v2 (browser) | **Shipped (I).** The Materials tab of the left dock (was a floating window): search, All / Starred / Recent and a folder picker, thumbnail size, the active material pinned below with its own density, the slots and Apply; favourites and recent (in memory until settings land), hover details. Click = active, double-click = apply, drag onto a face = apply there. Lazy thumbnails (on-screen only, worker-thread decode) keep 500+ textures smooth. |
| Import material library from a folder of images (PNG/JPG/TGA). Texture size drives texel density | | v2 | **Shipped (I).** Sizes come from file headers. Sources: bundled `assets/textures` and `assets/tools`, `CHIP_TEXTURE_DIRS` (`;`-separated), and Texture > Add texture folder (this session only; stream K persists). Texture > Rescan texture folders. Name aliasing: `_Albedo`/`_BaseColor`/`_Diffuse`… are stripped and other PBR maps (`_Normal`, `_Roughness`…) are skipped. Exports embed JPG as JPEG and convert TGA to PNG. |
| Per-material texel density override | | v2 | **Shipped (I).** The inspector's "Tiles every" field. Saved to `chip-materials.json` in the texture's folder. New blocks use it, and so does Texture > Reset scale to material density. Apply, replace and drop keep each face's scale relative to its material's density. |
| Transparent textures (cutout and blend) | | v2 | **Shipped.** `MaterialAlpha.Classify` reads the pixels: no alpha is opaque. Partial alpha only along edges is Mask (cut at 0.5), and partial alpha that fills regions is Blend. The Materials tab's Alpha row overrides it, saved as `"alpha"` in `chip-materials.json`. Viewport: cutouts use alpha to coverage with a sharpened, mip-compensated alpha. Blend batches draw sorted back to front, depth-tested but not depth-written. Colour hidden under alpha 0 is bled from neighbouring texels, so there are no halos. Export: glTF `alphaMode` MASK (`alphaCutoff` 0.5) or BLEND, and OBJ `map_d`. |
| Double-sided materials | | v2 | **Shipped.** A per-material toggle in the Materials tab (not offered for tool surfaces), saved as `"doubleSided"` in `chip-materials.json`, off by default. The viewport skips culling for those batches and flips the normal on back faces for lighting. `PickCache.DoubleSided` keeps them pickable from behind with backface selection off. Export: glTF `doubleSided: true`. OBJ has no equivalent. |
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
| Shift U/V by one step (8 directions) | `Ctrl+arrows` (4 directions), `Numpad 1–4, 6–9` | v1, done |
| Scale X/Y up/down | `Ctrl+Alt+Numpad 4/6/8/2` | v1, done |
| Rotate CW / CCW (15°) | `Alt+.` / `Alt+,`, or `Alt+Numpad 9 / 7` | v1, done |
| Fit to face | `Ctrl+Alt+F` or `Ctrl+Alt+Numpad5` | v1, done |
| Justify left/right/top/bottom/centre | `Shift+Alt+arrows` / `Shift+Alt+C`, or `Alt+Numpad 4/6/8/2/5` | v2, done |
| Flip texture horizontal/vertical | `Alt+D` / `Alt+E` | v1, done |
| Flip face UV horizontal/vertical | `Alt+R` / `Alt+T` | v2, done |
| Modify Texture panel: numeric shift/scale/rotation, Fit to Height/Width, Keep Ratio, Treat as One | | v1 (numeric) / v2 (fit options), done |
| Texel density setting ("[1] m is [256 ▾] px", per level, saved in the .chip) | | v2, shipped. Inspector with nothing selected, Level > Density. Changing it rescales every face whose texture follows the level (projected scale and explicit UVs), as one undo step. Textures can override it (Materials tab, "Density" under the active material, saved next to the texture). New blocks, Align to grid, unwraps, hotspots, the fast texture tool, the UV checker target and the Scale field (1 = at density) all follow it. |
| Wrap Texture onto the hovered face (continue the projection, copy the material). Any face, as in Scythe: neighbours fold about the shared edge; other faces, including on other meshes, fold about the line where the two planes meet; parallel faces slide across, or turn half a turn when they face the other way | `Alt+RMB` | v2, done |
| Wrap Texture to Selection | `Shift+Alt+RMB` | v2, done |
| Flow Texture along curves (follow quads) | `Ctrl+Alt+RMB` | v2, done |
| Texture lock toggle for moves **[S2]** | | v2, done |
| **Fast Texture Tool** (`Shift+Q`): a 2D UV editor overlay with a grid (`[`/`]`), frame (`F`), reset (`R`), rotate (`Q`/`E`), flip (`Alt+R/T`), repeat background (`B`), coordinates (`C`), original material (`T`), world scale (`G`). Unwraps: Use Existing (1), Conforming (2), Square (3), Follow Quads (4), Planar (5). Follow-quads spacing: Length / Even / Length Avg (`Ctrl+1/2/3`). The blue rect shows the ideal texel-density footprint | | v2, done |

## 9. Hotspots

| Feature | Tag | Notes |
|---|---|---|
| Hotspot asset (`.hotspot.json`): one per pattern, listing target materials + rects | v2, done | Resolution independent (normalised rects, x right / y down like UVs); inset in px at a stored reference resolution; optional preview material. Found recursively in every material library folder. Sample: `assets/hotspots/hotspot_concrete` (procedural concrete-panel trim sheet, 12 patches). |
| Hotspot editor: draw rects on the texture with a grid (`[`/`]`), pan RMB, zoom wheel, per-rect flags: Allow Rotation (`Alt+A`), Mirror X (`Alt+D`), Mirror Y (`Alt+E`), Tiling (`Alt+T`, full width/height only), Inset px; toggles: lit, hide texture (`Alt+V`), hide rects (`Alt+B`). List shows `[R][X][Y][T][Inset]` | v2, done | `Ctrl+Shift+H` (View menu). Floating window: LMB-drag draws (grid-snapped, Ctrl free), click selects/moves, handles resize, Del deletes, F frames, Esc cancels a drag. The flag keys set the defaults for new patches and toggle the selected one. Side column: patch list, numeric rect, flags, inset, target materials, preview material. Edits live in a working copy that Shift+H already uses; Save writes the file, Revert discards. New makes an asset beside the active material's texture. Window keys act only while it has focus. |
| Apply Hotspot (`Shift+H`) to coplanar-connected regions, Individual Faces (`Shift+F`) | v2, done | Regions: faces joined across edges within 1° of the first face. Each is flattened, bounded by its smallest rectangle (turned at most 45°, so walls stay upright), measured in texels at the material's density and fitted: aspect error (x2), then size, upright before turned (only with R), earlier patch on ties. Tiling patches match the cross size and repeat along U/V; a region must be at least as long as the patch to fit it perfectly. Random 180° turn / mirrors where allowed, seeded from mesh id + lowest face index, so re-applying is stable and the result is saved as explicit UVs. Uses the active material's asset, else the selected face's; sets the material unless Don't apply material. One undo step; nothing fits = no step and an error. |
| Apply options: Tiling (No / Allow / Only), Disallow Random, Don't Apply Material, Mapping (Automatic / Square / Conforming / Follow Active Quads) | v2, done | Inspector "Hotspot" section (and Texture menu toggles). Automatic = square unwrap unless an edge stretches more than ~10%, then conforming. |
| Apply a selected patch from the editor | v2, done | "Apply to faces" / "Each face" under the patch's properties. |
| Overlapping patches as extra fit candidates | v2, done | Every patch is a candidate, overlapping or not. |
| Hotspot texture maker (layered masks) | out of scope | Engine TextureGraph feature. Use Substance or Material Maker. |

## 10. Scene organisation [S2]

| Feature | Tag |
|---|---|
| Outliner (mesh list, rename, hide, lock) | v2 (done: `Ctrl+Shift+O`; folders, groups, meshes and entities, a mesh entity holding its meshes; rename by double-click/F2, eye and lock toggles, multi-select synced with the viewport, selecting in the viewport unfolds and scrolls to the row, arrow keys walk and fold rows, drag into folders and groups or beside a row, right-click menu (focus, rename, duplicate, delete, remove folder keeping contents, turn a group into a folder and back, new folder, move to top level, hide everything else), search, show only meshes or only entities, counts) |
| Groups (nested; selecting a member selects the group; hide/lock per group) | v2 (done: `Ctrl+G` / `Ctrl+Alt+G`, "Select whole groups" toggle; stored as optional `groups` in the file) |
| Folders (Unreal's outliner folders: organise without binding; members select one by one, empty folders stay) | v2 (done: Edit > New folder / Move to new folder, the outliner's folder button; a group with `folder: true` in the file, so older files read as groups) |
| Lock selected / unlock all | v2 (done: `Ctrl+L` / `Ctrl+Shift+L`; meshes and entities) |
| Visgroups / layers, hide/show by group | later |
| Entities / markers (player start, lights, generic point entity with key-values) exported as glTF empty nodes with `extras` | v2 (editor done: entity tool `Shift+E`, glyphs in every pane, object-mode select/gizmo/delete/duplicate, inspector with key-values; glTF export is stream J) |
| Entity classes from the engine project: Godot scenes are the definitions (root `@export` vars → typed properties with defaults, ranges, enums, groups, docs, and lists of them from typed arrays), a generated manifest Chip reloads, previews of the scene's meshes, references between entities, instancing on import **[new, replaces an FGD]** | v2 shipped (phases 1–2): `.chip` v3 stores only changed values, typed; v2 levels migrate. Design and limits in `docs/entities.md`. Phase 3 shipped: mesh entities (Tie to entity `Ctrl+Alt+T`, Untie `Ctrl+Shift+W`; origin in the inspector; exported as the entity node's children; Godot body/area roots). Class looks shipped: a class's `icon` (any Tabler icon by name, baked into the atlas when a manifest asks for it) and `color` mark it in the viewport, outliner and pickers; Godot roots set them with `chip_icon` / `chip_color`; `--entities-check` warns on names and colours it can't draw |
| Reference models: a `.glb`/`.gltf` (a door, a prop, the built-in `block_human` figure) drawn in the level to fit holes and judge scale **[new]** | v2 shipped: entity class `ref_model` (keys `model`, `export_marker`), placed from the Place tab of the dock (built-ins, or From file…) or the entity tool. Drawn as lit clay exactly where an engine puts the file at the node (+Z front), with hard edges; a selected one also shows its hidden edges dashed, so a door inside a wall shows its hole. Picked by its triangles (clicks pass through a doorway); selecting it shows its size in the dimensions and inspector. On a wall it turns to face out and stands on the wall's bottom edge: centred in the wall's thickness if it's no deeper than the wall, else against it; the status bar says how it fits ("12.5 cm deep in a 20 cm wall…"). Files load once, reload when they change on disk, and are stored relative to a saved level. Editor-only by default; "Export as marker" exports an empty node (`main_door_8`, or the entity's name) to drop the real asset on. Built-ins live in `assets/references`. |
| Per-object origin / pivot for the gizmo | later |
| Prefabs / instances (referenced `.chip` files) | later |
| Collision-only meshes / nodraw-style tool materials (`tools/clip`, `nodraw`, `trigger`) exported with a name suffix (`-col`, `-colonly`) for Godot's import hints | v2 |
| Tags on faces and meshes (what dressing rules select by) | v2 (done: `Ctrl+T` popup at the mouse, inspector Tags section, select by tag; tags travel with the surface through every op and undo; optional `tags` arrays in the file) |
| Dressing: rule files turn the blockout into the shipped level (trims, bevels, proxies, scatter) **[new, not in Scythe or S2]** | v2 (phases 0–1 done: tags; `level.dressing.json` with imports, vars and overrides; `bevel` and `sweep` (contact across separate meshes, mitred, overlapping corners handled; boundary, border, convex, concave); live reload; `` ` `` dressed/blockout view; export ships the dressed level with blockout collision; inspector Dressing section; `--rules-check`, `--rules-schema`; Tools > Open dressing sample. Phase 2 done: `railing` and `stairs` proxies by mesh tag (striped in the blockout view, collide as their box), `repeat` and `scatter` with built-in or glTF props, per-mesh overrides and switches in the inspector, Bake dressing. Editor authoring done: Tools > Dressing rules window (Ctrl+Shift+D) edits every rule in words and units and writes the JSON as undo steps; recipes; Edit > Dress with (Ctrl+Shift+T) tags the selection for a rule; styles picked, edited and saved from the window; View > Tag colours; why-isn't-this-dressed in the inspector. Design in `docs/dressing.md`) |

## 11. Interop

| Feature | Tag | Notes |
|---|---|---|
| Export `.glb` (meshes, materials, embedded textures) | v1 | Shipped (J): File > Export settings adds smoothing on/off, include hidden, and embed vs reference textures (relative paths to the library files; TGA and other-drive images are copied to `name_textures/`). Settings are saved in an optional `export` block of the `.chip` file. |
| Godot import hints: `-col`/`-colonly` suffixes, per-node `extras` | v2 | Shipped (J): per-mesh collision in the inspector (Default / None / Collision only / Convex only, the last giving `-convcolonly`). Mesh nodes carry `extras.chip_mesh`/`chip_id`; clip and trigger nodes carry `chip_tool`. Entities are empty nodes (translation, rotation, × export scale) with `extras = { chip_class, chip_id, chip_uid?, chip_scene?, chip_name?, properties }` (values typed by the class, `docs/entities.md`), exported flat (groups once H lands). |
| Baked world collision **[S2]** (Source 2 style: collision is its own baked product, built from the original geometry) | v2 | Shipped. On by default (Export settings > Collision > Bake world collision). Default meshes' render and nodraw faces are welded, faces resting back to back are removed, coplanar faces merge into single polygons (no internal edges for characters to snag on), and the result is chunked (32 m) into `chip_world_collision_N-colonly` nodes tagged `extras.chip_baked` (re-import skips them). Optional: convex meshes become `chip_hull_<id>-convcolonly` hulls (off by default: separate shapes keep the seams). Collision-only / convex-only meshes keep their own nodes; clip and trigger nodes are unchanged. The status bar reports triangles before and after. |
| Tessellate for vertex lighting (retro per-vertex lighting) | v2 | Shipped. Export settings > Lighting, with a grid spacing (default 1 m). Render faces are cut along a world-aligned grid in their plane at export only; edges split identically on both sides (no T-junctions), constrained-Delaunay triangulation, UVs and smoothed normals interpolated, so the surface is unchanged. Colliders always use the original faces (baked, or a `name_col-colonly` node when baking is off). Collision-only meshes are never tessellated. Re-import rebuilds the original n-gons. |
| Material remap to engine resources | v2 | Shipped (J): table in Export settings (Chip material to `res://` path), written to material `extras.chip_path` and mesh node `extras.chip_remap`. |
| Engines other than Godot | v2 | `chip.project.json` at the game root names the engine, the entity manifest (hand-written or generated by the game's tools) and its conventions (collision name suffixes, asset path prefix). Colliders always carry `extras.chip_collider`/`chip_render`, so a loader needs only glTF extras. `Chip --entities-check` validates a manifest. Sample game: `samples/toy-game`. `docs/engines.md`. |
| Game surfaces (custom tool materials) **[S2]** | v2 | Shipped: `"surfaces"` in `chip.project.json` declare render / collide / volume behaviour and an editor look (colour, world-space pattern, opacity, or an image through the UVs); the built-in tool materials are surfaces too. Exported nodes and materials carry `chip_surface`. Tool surfaces draw see-through (View > Translucent tool surfaces). |
| Godot import addon | v2 | Shipped (J): `godot/addons/chip_import` (Godot 4.3+; tested headless on 4.5–4.7 by `scripts/godot-test.ps1`). Writes the entity manifest; entities become linked instances of their class's scene with the level's values and references applied (a class without a scene: `Marker3D`); remapped materials load; triggers become `Area3D`. Collision suffixes are left to Godot. |
| Re-export on save (watch mode) so Godot hot-reloads | v2 | Shipped (J): `Ctrl+Alt+E` toggles File > Export on save (saved with the level). It hooks `Session.Saved`; the status bar reports the export or how to fix it. |
| OBJ export | later | Shipped (J): File > Export OBJ… writes OBJ + MTL with n-gons, UVs and normals, and no collision-only or tool faces. |
| Import `.glb`/`.obj` as an editable mesh | later | Shipped (J): File > Import mesh… welds vertices and rebuilds coplanar, same-material, UV-continuous triangles into simple n-gons. It keeps UVs as explicit UVs, recovers soft edges from normals, and matches materials to the library by name. Chip's own exports round-trip names, collision, clip/trigger faces and entities. One undo step; the imported meshes are selected. |
| Source `.vmf` / UE `.t3d` import | later | |

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
- R4 (user): 16 px body / 13 px small at the monitor's DPI scale. Metric units with 512 px/m texel density. Backface selection mode (Alt+B, tool strip, View menu) that lets picking hit faces pointing away. (Corrected later, per the user: it first also drew backfaces darkened. Rendering never changes; it's a picking mode only.) Font rasteriser tuned (3x/2x oversampling, pixel-snapped advances, 1.12 coverage). Removed the redundant `--render` mode in favour of `--shot`.
- R5 (user: icons "wigged"): replaced the hand-drawn vector glyphs with the Tabler icon font (MIT, assets/fonts/tabler-icons.ttf; only the 13 glyphs used go into the atlas, at 18 logical px, pixel-aligned). The texture flip buttons use the flip-horizontal/vertical glyphs.
- R6 UI foundation pass (frontend-design): each panel has one job. The tool strip is tools only. A new viewport header holds the selection-mode segmented control (icon + label, orange underline), the view name, and grid/snap/backfaces. The status bar only reports (selection, hints/messages, scene counts). The menu bar shows the document name and saved/unsaved state. The F1 keyboard-shortcuts sheet is grouped by task. The block tool shows a snapped construction cursor before the first click. A corner world-axis indicator. `UI/Widgets` is the shared vocabulary (ToolButton, Segment, Toggle, IconButton, TaggedFields, Heading, RowLabel, KeyHints), with three states: active = warm fill + orange bar/underline, setting on = neutral raised, hover = frame.
- R7 Krita dark orange (user, with a Krita reference): neutral near-black chrome (#1e1e1e) and docks (#262626), dark hairlines, fields with a dark outline. The orange is now a solid fill with dark text for whatever is active or selected: the active tool tile, the active selection mode, selected outliner/palette/preferences rows, primary buttons, and value sliders (fill = value, label centred on it, dark text over the fill and light beyond it). Settings that are on stay neutral with an orange icon. Inspector sections read as Krita dockers, each with a chrome title bar. Brighter toolbox icons; recessed tracks darker than the chrome.
