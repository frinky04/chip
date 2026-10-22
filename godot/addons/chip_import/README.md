# Chip import (Godot 4.3+)

Makes your scenes Chip's entity classes, and turns the `.glb` files Chip exports into a usable level. Design and details:
`docs/entities.md` in the Chip repository.

## Entity classes

Every scene under `res://entities/` (Project Settings > `chip/entities/folders`) is an entity class Chip can place. Its
properties are the root script's `@export` vars, with their defaults, hints, groups and `##` doc comments. The addon keeps
`res://.chip/entities.json` up to date as you save; Chip reads it. Don't edit that file.

- Class names are paths without extension: `res://entities/enemies/grunt.tscn` is `enemies/grunt`.
- Files and folders starting with `_` are skipped (helper scenes).
- Root metadata `chip_expose` (`PackedStringArray`) adds native properties (`light_color`); `chip_bounds` (`AABB`) sets
  the box Chip draws for a scene without meshes. Scenes with meshes get a preview Chip draws instead.
- A project scene with the same name as a built-in class replaces it.

Built-in classes (in `entities/`): `info_player_start` (`Marker3D` in group `chip_spawn`), `info_target`, `light_point`
(`OmniLight3D`) and `light_spot` (`SpotLight3D`, shining along -Z).

## On import

| In Chip | In Godot |
|---|---|
| entity | a linked instance of its class's scene, with the level's values applied |
| entity reference (`partner` → another entity) | the property points at that entity's instance |
| entity whose scene is gone | `Marker3D` carrying the values, with a warning |
| material remap (File > Export settings) | the `res://` material replaces the imported one (surface override) |
| trigger faces | `Area3D` with the trigger's collision shape |
| mesh collision Default / Only / Convex only | `-col` / `-colonly` / `-convcolonly`, handled by Godot itself |

Every entity keeps `chip_class`, `chip_properties` (the values Chip set) and `chip_id` as metadata, and is in the
`chip_entities` group.

## Install

1. Copy the `addons/chip_import` folder into your project, so you have `res://addons/chip_import/plugin.cfg`.
2. Open Project > Project Settings > Plugins and enable **Chip import**. It writes the manifest right away.
3. Save your Chip level in the project folder, or export into it (File > Export settings > File, e.g. `levels/level01.glb`).
   Chip then lists the project's classes. Godot imports the file, and the plugin processes every scene that carries Chip
   data.

Turn on **Export on save** in Chip (`Ctrl+Alt+E`): every save rewrites the `.glb` and Godot re-imports it when its window
gets focus.

Headless (CI, or before anyone opens the project):
`godot --headless --path <project> --script res://addons/chip_import/chip_manifest_cli.gd`.

### Without enabling the plugin

Use the per-file import script instead: select the `.glb` in the FileSystem dock, open the Import dock, set **Import
Script** to `res://addons/chip_import/chip_post_import.gd` and click **Reimport**. It does the same as the plugin for that
file, but nothing writes the manifest, so Chip only offers its built-in classes. Running both is harmless.

## Material remap

In Chip, File > Export settings > Material remap maps a Chip material (e.g. `brick`) to a Godot resource (e.g.
`res://materials/brick.tres`). The path is written to the glTF material's `extras.godot_path` and to each mesh node's
`extras.chip_remap`. If the path doesn't exist when Godot imports, the plugin prints a warning and keeps the imported
material.

## Requirements

Godot 4.3 or later (tested on 4.5, 4.6 and 4.7): earlier versions don't import glTF `extras` as metadata.
