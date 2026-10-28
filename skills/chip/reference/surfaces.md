# Surfaces and mesh entities

## Surfaces

A surface is a material name that changes what faces do in the export: render, collide, form a volume, or disappear.
Chip has four built in, Source's tool textures:

| Surface | Renders | Collides | Volume |
|---|---|---|---|
| `nodraw` | no | when its mesh collides | no |
| `clip` | no | always | no |
| `trigger` | no | no | yes |
| `skip` | left out of the export entirely | | |

A game adds its own, or restyles the built-ins, under `"surfaces"` in `chip.project.json`:

```json
"surfaces": {
  "monster_clip": { "render": false, "collide": "always", "doc": "Blocks monsters, lets players through", "look": { "color": "#d0406a" } },
  "water":        { "collide": "none", "volume": true, "look": { "color": [0.1, 0.4, 1], "opacity": 0.4 } },
  "video":        { "doc": "Shows its screen's video", "look": { "image": "chip/surfaces/video.png" } }
}
```

| Field | Default | Meaning |
|---|---|---|
| `render` | `true` | Faces draw in the game, with a glTF material named after the surface. |
| `collide` | `"mesh"` | `"mesh"`: solid when the mesh collides, like an ordinary face. `"always"`: solid whatever the mesh's setting. `"none"`: never solid. |
| `volume` | `false` | The faces also form a closed, non-solid volume the game tests for overlap. |
| `skip` | `false` | Left out of the export. |
| `doc` | | Shown in the material browser. |
| `look` | | How Chip draws it; never exported. |

`look` takes `color` (`"#rrggbb"` or `[r, g, b]`), `pattern` (`"solid"`, `"stripes"`, `"checker"`), `opacity` (0 to 1)
and `image` (a path relative to the project file). A pattern is drawn in world space and needs no UVs. An image is drawn
through the faces' UVs, so it can stand in for what the game will show (a video frame); fit it with the Texture tools.
A surface that doesn't render starts striped and see-through; one that renders starts solid. Surfaces that don't
render draw see-through while View > Translucent tool surfaces is on.

Designers apply a surface like any material: it appears in the Materials tab. The game gives each surface its meaning
from `chip_surface` in the export (see [`export.md`](export.md)).

## Mesh entities

A mesh entity is an entity made of level geometry, like a door, a moving platform, a screen or a breakable wall: Hammer's
brush entity. Its class says `"mesh": true` in the manifest (Godot: a scene whose root is a `CollisionObject3D`, such as
`AnimatableBody3D` or `Area3D`).

- A designer selects meshes and uses Edit > Tie to entity (`Ctrl+Alt+T`) to pick the class; Untie (`Ctrl+Shift+W`)
  turns it back into plain meshes.
- The entity's origin starts at the meshes' bottom centre and is set in the inspector, like a door's hinge. Its
  meshes stay put when the origin moves.
- Selecting any part selects all of it, so it moves, turns, copies and deletes as one.
- In a level file the entity lists its meshes: `"meshes": [13, 14]` ([`levels.md`](levels.md)).
- In the export its meshes are its node's children, in its space, with their own colliders. They never join the baked
  world collision, because they move.

Properties and values work as for any entity. Use a mesh entity when geometry needs settings of its own (which video
this screen plays, how far this platform travels). Use a surface when the faces only need to behave differently.
