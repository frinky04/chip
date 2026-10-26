---
name: chip
description: Chip level editor pipeline. Use when connecting a game or engine to Chip, defining entity classes for it (chip.project.json, the entity manifest), or editing and exporting .chip levels and the .glb files Chip exports.
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

## Edit or export levels

Read [`reference/levels.md`](reference/levels.md) for the `.chip` format, the commands and dressing. Done when
`--export` exits 0 without warnings and a `--shot` capture of the level, which you have looked at, shows the change.

## Reference

- [`reference/manifest.md`](reference/manifest.md): `chip.project.json`, the manifest format, property types and
  values, generating the manifest, `--entities-check`.
- [`reference/export.md`](reference/export.md): what the `.glb` holds and how a loader reads it: space and units, node
  kinds, entities, references, materials.
- [`reference/levels.md`](reference/levels.md): the `.chip` format, every headless command, dressing rules.
- `samples/toy-game` in the Chip repository is a worked example: entity classes in C# (`game/Entities.cs`), the
  manifest generator (`game/Manifest.cs`), a loader (`game/Level.cs`) and a test level (`levels/demo.chip`).
