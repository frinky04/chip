# Export contract

What Chip's `.glb` holds and how a game reads it. A loader needs a glTF parser and the `chip_*` keys in node `extras`,
and nothing from Chip itself. `samples/toy-game/game/Level.cs` is a complete loader of about 200 lines.

## Space

- glTF 2.0 binary: right-handed, +Y up, units are metres × the level's export scale (Export settings, 100 for
  centimetre engines). Convert to the engine's axes in the loader.
- Apply every mesh node's `translation` and `rotation`, as for any glTF node. A root mesh node usually has neither, and
  its vertices are then in world space. A mesh with its own origin, and every linked copy, has its node at that origin
  with vertices relative to it; linked copies share one glTF mesh, so only the node says where each one is. A mesh
  entity's meshes are children of its entity node, with vertices in the entity's space.
- Entities are empty nodes with `translation` and a unit quaternion `rotation`, each left out when it is zero or
  identity (glTF's defaults). An entity faces −Z, and Chip edits its angles as yaw Y, then pitch X, then roll Z.
- The export scale applies to positions only. Property values go out as typed (a `range` of 8 stays 8).

## Node kinds

Tell nodes apart by their `extras`. Check `chip_class` and `chip_volume` before `chip_collider`.

| Node | Has a mesh | `extras` |
|---|---|---|
| Entity | no | `chip_class`, `chip_id`, `properties`; `chip_name` when named; `chip_scene` / `chip_uid` when the manifest gives them; `chip_tags` when tagged; `chip_io` when it has connections; a mesh entity has its meshes as `children` |
| Render mesh | yes | `chip_mesh` (the mesh's name), `chip_id`; `chip_collider: "trimesh"` when it is its own collider; `chip_collision` when it isn't Default (`None`, `Only`, `ConvexOnly`); `chip_remap` (material name → engine path). A mesh with its own origin has the node's `translation`/`rotation` at it, vertices relative to it. A linked copy's part also has `chip_instance` (the prop's name), `chip_instance_id` (the copy), `chip_part`, and `chip_prefab` (the prefab file, project-relative) when it comes from one; its node sits at the copy's origin and every copy of a part shares one glTF mesh (unless tessellating) |
| Collider only | yes | `chip_collider: "trimesh"` or `"convex"`, `chip_render: false`; no material |
| Surface collider | yes | `chip_surface` (`clip`, `nodraw`, or the game's `monster_clip`), `chip_collider: "trimesh"`, `chip_render: false`; `chip_tool: "clip"` for Chip's clip |
| Volume | yes | `chip_surface` (`trigger`, or the game's `water`), `chip_volume: true`, `chip_render: false`; `chip_tool: "trigger"` for Chip's trigger |
| Surface that draws without colliding | yes | `chip_surface`, a material; no `chip_collider` |
| Baked world collision | yes | `chip_collider`, `chip_render: false`, `chip_baked: true`; named `chip_world_collision_N` (32 m chunks) and `chip_hull_<meshId>` |

The rules a loader follows:

- **Draw** a mesh node unless `chip_render` is `false`.
- **Collide** with a mesh node when it has `chip_collider`: a triangle mesh for `trimesh`, a convex hull of its vertices
  for `convex`. `chip_surface` says which surface made it, so the game can, for example, let a `monster_clip` block
  monsters only.
- **Volume**: a `chip_volume` node is a closed volume. Use it for overlap tests and make it non-solid; `chip_surface`
  says which kind (`trigger`, `water`).
- **Clip**: Chip's `clip` blocks players but draws nothing.

With baked collision (the default) render meshes carry no `chip_collider`: the `chip_world_collision_N` nodes are the
level's collision, so a floor collides through them. Every kind is optional, and a level without clip or triggers simply
has no such nodes. When `collisionSuffixes` is on, collider node names also end in `-col`, `-colonly` or `-convcolonly`;
read the extras and treat names as labels.

## Mesh tags

Object-mode tags (Ctrl+T) export as a sorted, unique string array in `node.extras.chip_tags`, for example
`"chip_tags": ["destructible", "interactable"]`. The key is absent for an untagged mesh. Read tags from the node,
not the glTF mesh resource. Surface and volume nodes split from that mesh repeat its tags; `chip_id` identifies
their source. Chip restores these mesh tags on GLB reimport. Face tags are not exported.

Dressing replacements (modified meshes and proxies) keep their source mesh's tags on export; added dressing
geometry does not inherit them. Derived collision nodes marked `chip_baked` have no mesh tags, including merged
world collision. Entities can carry tags through a manifest property (`type: "array", of: "string"`), read from
`extras.properties.tags`.

## Entities

```json
{ "name": "left_door", "translation": [4.5, 0, 0],
  "extras": { "chip_class": "door", "chip_id": 9, "chip_name": "left_door",
              "properties": { "keys": ["key_red"], "partner": { "entity": 10 } } } }
```

- `properties` holds only the values the designer changed, typed by the manifest (the type table in
  [`manifest.md`](manifest.md)). The game applies its own default for every absent property.
- A reference `{ "entity": 10 }` names another entity's `chip_id`. Resolve references after every entity exists.
  References to entities that weren't exported are left out (and dropped from lists), and Chip warns at export.
- Values the class doesn't declare (the class changed after the level was made) still go out as they were. Ignore
  unknown keys, and log unknown `chip_class` names.
- Node names are the entity's name, else `<class>_<id>`. Look entities up by `chip_id` or `chip_name`.
- `chip_tags` is the entity's tags, a sorted array of strings, which `tag` connection targets select by.
- `chip_io` is the entity's connections, every field written out:
  `[{ "on": "opened", "target": { "entity": 12 }, "do": "turn_off", "args": {}, "delay": 0.5, "once": false }]`,
  plus `when` when guarded. A connection to an entity that wasn't exported is left out (Chip warns).
  [`logic.md`](logic.md) gives the target forms and how a game must run them.
- When the project declares flags, the glTF scene's `extras.chip_flags` lists them all:
  `{ "power_on": { "type": "bool", "default": false, "scope": "game" } }`.
- A mesh entity's `children` are its geometry: render meshes, colliders and volumes as in the table, in its space.
  They move with it and never join the baked world collision. Under a mesh entity, collider names carry no Godot
  suffixes.

## Level properties

The selected glTF scene carries `extras.chip_world` beside `chip_flags`:

```json
{ "chip_world": { "chip_class": "level", "properties": { "sky": "overcast", "brightness": 0.7 } } }
```

Properties use the entity export's typing and reference handling; only explicitly set values are exported.
Apply the game's defaults for absent properties. The object is omitted when there is no world class and no world values.
If values remain without a class, they export with an empty `chip_class` and `--check-levels` reports an error.

## Materials

Each glTF material is named after its Chip material, with the base colour texture embedded or referenced
(Export settings). A material the level remaps (Export settings > Material remap) carries `extras.chip_path`, the
engine path to load in its place. Mesh nodes repeat the remaps in `extras.chip_remap` for importers that drop
material extras. A material for a surface that draws (the game's `video`) carries `extras.chip_surface`, so the renderer
can swap it, for example for the video of the screen the mesh belongs to.

## Lightmap charts

With `"lightmapCharts": true` in `chip.project.json` ([`manifest.md`](manifest.md)), Chip writes each drawn face's
**chart**: a flat piece of a level, laid out in real units, for the game's baker to pack into its atlas. Packing, texel
density, padding and baking are the game's, so it can rebake at another density without a new export.

Every primitive with a material gains two attributes, at the same `count` as `POSITION`. Collider-only primitives have
neither, and neither does any primitive with the setting off.

| Attribute | Accessor | Value at each corner |
|---|---|---|
| `TEXCOORD_1` | float `VEC2` | Position in the chart's plane, in the same units as `POSITION` (so the export scale applies). Each chart's minimum is exactly `(0, 0)`. |
| `_LM_CHART` | float `SCALAR` | The chart's ID, a whole number from 0 with no gaps, counted per glTF mesh across all its primitives. |

A chart is a set of drawn faces of one glTF mesh, connected through shared edges, lying on one plane and facing the same
way. Materials don't split a chart, so one wall painted in two materials is one chart, spread over two primitives. Each
vertex belongs to one chart, because corners are written per face.

- **Same size as the world:** a triangle's area in `TEXCOORD_1` equals its area in `POSITION`, on any planar face.
- **Tight fit:** the chart's axes are the edge direction, among all its edges projected onto its plane, that gives the
  smallest bounding rectangle. The first such edge in face order wins a tie, and rectangles within 0.001% of each other
  in area count as tied. A diagonal wall packs as tightly as a straight one.
- **Unmirrored:** `v = normal × u`, so a triangle keeps its winding in `TEXCOORD_1`.
- **Deterministic:** the same document exports byte-identical files, so a hash of the `.glb` detects a stale bake.
- **Instances:** linked copies share one glTF mesh, so their chart IDs repeat. Key charts by `(node, chart)` and give each
  copy its own atlas space. A mesh's ID says nothing about another mesh's: the same number on two glTF meshes is two charts.
- **Everything drawn gets charts,** including sky faces, surfaces that draw without colliding (their own glTF mesh, so
  their own IDs from 0), and mesh entities' meshes. Choose which to bake from material names and `chip_*` extras.

**Coplanar test.** Two faces sharing an edge join when their normals' dot product is at least `0.9999` and each lies
within 1 mm of the other's plane, measured in document units before the export scale. A face must also pass that test
against the chart's first face in face order, whose normal is the chart's normal, so a gentle curve can't chain into
one flat chart. Faces joined only by a soft edge stay in separate charts unless they pass, as Quake's and Half-Life's
per-face lightmaps did. A non-planar face is its own chart unless it passes too, projected onto the plane of its normal.
A tessellated mesh has the charts of its original faces, so a wall cut by the grid is still one chart.

To load charts, read both attributes for each primitive of a drawn node, collect each `(node, chart)`'s size as the
largest `TEXCOORD_1` over its corners across the mesh's primitives, and pack those rectangles. At a chosen density
(texels per unit of `POSITION`), a corner's atlas UV is:

```
atlasUv = (TEXCOORD_1 × density + chartOffset) / atlasSize
```

`TEXCOORD_1` is in the mesh's own space, which the node's rigid transform leaves the same size, so copies at different
places or rotations bake at the same density.
