---
name: chip
description: Chip level editor pipeline. Use when connecting a game or engine to Chip, defining its entity classes or surfaces (chip.project.json, the entity manifest), or editing and exporting .chip levels and the .glb files Chip exports.
---

# Chip

Chip is a level editor: designers build geometry and place entities, and Chip exports a `.glb` that the game loads.
Every game connects to it by the same **round trip**:

```
game's entity definitions ──generate──▶ entity manifest ──▶ Chip (designer places entities)
          ▲                                                        │
          └──────────── game's loader ◀── level.glb (glTF + chip_* extras)
```

The game's own entity definitions are the **source of truth**. The manifest is generated from them, and the loader
builds them back from the export. Work is done when the round trip carries a value from the level into the running
game. Godot projects get both ends from Chip's import addon (`godot/addons/chip_import` in the Chip repository); every
other engine provides the generator and the loader itself.

Chip runs headless from the command line ([`reference/levels.md`](reference/levels.md), Commands). Find `Chip.exe` on
the PATH, in the `CHIP_EXE` environment variable, or at `artifacts/publish/win-x64/Chip.exe` in a Chip checkout
(`powershell -File scripts/publish.ps1` builds it); otherwise ask the user where it is. Scripts and tests that run Chip
read its path from `CHIP_EXE`.

## Connect a game

1. **Find the source of truth**: where the game declares its entity types, their fields and their defaults (component
   classes, prefab files, a data table). Done when you can name the files and how a field's type and default are declared.
2. **Write `chip.project.json`** at the game's root ([`reference/manifest.md`](reference/manifest.md), Project file).
3. **Generate the manifest** from the source of truth with a small tool or build step in the game's own language, and
   commit its output ([`reference/manifest.md`](reference/manifest.md), Format and Generate it from the game).
4. **Check it**: `Chip.exe --entities-check <game root>` exits 0.
5. **Write the loader** against [`reference/export.md`](reference/export.md): entities by `chip_class` with their
   values over the game's defaults, references resolved by `chip_id`, colliders, triggers, clip.
6. **Prove the round trip** with a test level ([`reference/levels.md`](reference/levels.md)) holding one entity of every
   class, each with at least one value changed from its default, plus a reference, a trigger and a floor. Export it with
   `Chip.exe --export` and load it with the game's loader, ideally from an automated test. Done when every class
   arrives with its changed values, the reference points at the right entity, and the floor collides.

## Change entity classes

1. Edit the source of truth, regenerate the manifest, and run `--entities-check` until it exits 0.
2. A renamed class or property orphans the values levels already hold. Rename the keys (`className`, or the key
   inside `properties`) in every `.chip` file that uses them. Done when a search of the levels finds no old name.
3. A property type the loader hasn't read before needs a loader change. Repeat the round trip for that class.

## Add a surface or mesh class

A **surface** changes what faces do (monster clip, water, a screen the game swaps); a **mesh class** is an entity made
of level geometry with settings of its own (a door, a platform, a screen with its video). Read
[`reference/surfaces.md`](reference/surfaces.md) to choose between them.

1. Declare it: a surface under `"surfaces"` in `chip.project.json`, a mesh class with `"mesh": true` from the generator.
   `--entities-check` exits 0 and lists it.
2. Teach the loader what it means: a surface by `chip_surface` (on colliders, volumes and materials), a mesh class by
   its entity node's children ([`reference/export.md`](reference/export.md)).
3. Prove it in the round-trip level: faces painted with the surface, or meshes tied to the class with a value changed.
   Done when the game tells the surface apart, and the mesh entity arrives owning its geometry.

## Edit or export levels

Read [`reference/levels.md`](reference/levels.md) for the `.chip` format, the commands and dressing. Done when
`--export` exits 0 without warnings and a `--shot` capture of the level, which you have looked at, shows the change.

## Reference

- [`reference/manifest.md`](reference/manifest.md): `chip.project.json`, the manifest format, property types and
  values, generating the manifest, `--entities-check`.
- [`reference/export.md`](reference/export.md): what the `.glb` holds and how a loader reads it: space and units, node
  kinds, entities, references, materials.
- [`reference/surfaces.md`](reference/surfaces.md): surfaces (built-in tool materials and the game's own) and mesh
  entities.
- [`reference/levels.md`](reference/levels.md): the `.chip` format, every headless command, dressing rules.
- `samples/toy-game` in the Chip repository is a worked example: entity classes in C# (`game/Entities.cs`, including
  a `screen` mesh class), the manifest generator (`game/Manifest.cs`), a loader (`game/Level.cs`), surfaces in
  `chip.project.json`, and a test level (`levels/demo.chip`).
