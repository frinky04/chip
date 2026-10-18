# Chip import (Godot 4.3+)

Post-processes `.glb` files exported by Chip so they arrive as a usable level:

| In Chip | In Godot |
|---|---|
| `info_player_start` entity | `Marker3D` named `spawn` (`spawn_2`, ...), in group `chip_spawn` |
| `light_point` entity | `OmniLight3D` |
| `light_spot` entity | `SpotLight3D` (shines along the entity's -Z) |
| any other entity | `Marker3D` |
| material remap (File > Export settings) | the `res://` material replaces the imported one (surface override) |
| trigger faces | `Area3D` with the trigger's collision shape |
| mesh collision Default / Only / Convex only | `-col` / `-colonly` / `-convcolonly`, handled by Godot itself |

Every entity node keeps its data as metadata: `chip_class`, `chip_properties` (the key-values) and `chip_id`. All of them are also in the `chip_entities` group.

Light key-values read: `color` (`#ff8800`, `255 136 0` or `1 0.5 0`) or Hammer's `_light` (`r g b brightness`), `energy` / `brightness`, `range` / `radius`, `spot_angle` / `angle` / `cone` (degrees), `shadows` (`1` / `true`).

## Install

1. Copy the `addons/chip_import` folder into your project, so you have `res://addons/chip_import/plugin.cfg`.
2. Open Project > Project Settings > Plugins and enable **Chip import**.
3. Export from Chip into the project folder (File > Export settings > File, e.g. `levels/level01.glb`). Godot imports the file, and the plugin processes every scene that carries Chip data.

Turn on **Export on save** in Chip (`Ctrl+Alt+E`): every save rewrites the `.glb` and Godot re-imports it when its window gets focus.

### Without enabling the plugin

Use the per-file import script instead: select the `.glb` in the FileSystem dock, open the Import dock, set **Import Script** to `res://addons/chip_import/chip_post_import.gd` and click **Reimport**. It does the same as the plugin, for that file only. Running both is harmless.

## Material remap

In Chip, File > Export settings > Material remap maps a Chip material (e.g. `brick`) to a Godot resource (e.g. `res://materials/brick.tres`). The path is written to the glTF material's `extras.godot_path` and to each mesh node's `extras.chip_remap`. If the path doesn't exist when Godot imports, the plugin prints a warning and keeps the imported material.

## Requirements

Godot 4.3 or later: earlier versions don't import glTF `extras` as metadata, so entities stay plain `Node3D` and materials aren't remapped (collision suffixes still work).
