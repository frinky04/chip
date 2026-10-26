# Export contract

What Chip's `.glb` holds and how a game reads it. A loader needs a glTF parser and the `chip_*` keys in node `extras`,
and nothing from Chip itself. `samples/toy-game/game/Level.cs` is a complete loader of about 200 lines.

## Space

- glTF 2.0 binary: right-handed, +Y up, units are metres × the level's export scale (Export settings, 100 for
  centimetre engines). Convert to the engine's axes in the loader.
- Every node is a root node. Mesh nodes have no transform: their vertices are already in world space.
- Entities are empty nodes with `translation` and a unit quaternion `rotation`, each left out when it is zero or
  identity (glTF's defaults). An entity faces −Z, and Chip edits its angles as yaw Y, then pitch X, then roll Z.
- The export scale applies to positions only. Property values go out as typed (a `range` of 8 stays 8).

## Node kinds

Tell nodes apart by their `extras`. Check `chip_class` and `chip_tool` before `chip_collider`.

| Node | Has a mesh | `extras` |
|---|---|---|
| Entity | no | `chip_class`, `chip_id`, `properties`; `chip_name` when named; `chip_scene` / `chip_uid` when the manifest gives them |
| Trigger volume | yes | `chip_tool: "trigger"`, `chip_render: false` |
| Player clip | yes | `chip_tool: "clip"`, `chip_collider: "trimesh"`, `chip_render: false` |
| Render mesh | yes | `chip_mesh` (the mesh's name), `chip_id`; `chip_collider: "trimesh"` when it is its own collider; `chip_collision` when it isn't Default (`None`, `Only`, `ConvexOnly`); `chip_remap` (material name → engine path) |
| Collider only | yes | `chip_collider: "trimesh"` or `"convex"`, `chip_render: false`; no material |
| Baked world collision | yes | `chip_collider`, `chip_render: false`, `chip_baked: true`; named `chip_world_collision_N` (32 m chunks) and `chip_hull_<meshId>` |

The rules a loader follows:

- **Draw** a mesh node unless `chip_render` is `false`.
- **Collide** with a mesh node when it has `chip_collider`: a triangle mesh for `trimesh`, a convex hull of its vertices
  for `convex`.
- **Trigger**: a `chip_tool: "trigger"` node is a closed volume. Use it for overlap tests and make it non-solid.
- **Clip**: a `chip_tool: "clip"` node blocks players but draws nothing.

With baked collision (the default) render meshes carry no `chip_collider`: the `chip_world_collision_N` nodes are the
level's collision, so a floor collides through them. Every kind is optional, and a level without clip or triggers simply
has no such nodes. When `collisionSuffixes` is on, collider node names also end in `-col`, `-colonly` or `-convcolonly`;
read the extras and treat names as labels.

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

## Materials

Each glTF material is named after its Chip material, with the base colour texture embedded or referenced
(Export settings). A material the level remaps (Export settings > Material remap) carries `extras.chip_path`, the
engine path to load in its place. Mesh nodes repeat the remaps in `extras.chip_remap` for importers that drop
material extras.
