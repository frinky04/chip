# Toy game

A stand-in for a game on a custom engine, showing how any engine connects to Chip ([docs/engines.md](../../docs/engines.md)).
It never references Chip: its entity classes are C# code, and it reads Chip's `.glb` with its own loader.

```
dotnet run --project game -- manifest                 # entity classes (game/Entities.cs) -> chip/entities.json
Chip.exe --entities-check .                           # Chip reads the manifest: 4 classes, exit 0
Chip.exe --export levels/demo.chip                    # -> levels/demo.glb
dotnet run --project game -- play levels/demo.glb     # what the game sees: entities, colliders, triggers, the spawn
```

Open `levels/demo.chip` in Chip to place entities from the game's classes. `chip.project.json` makes this folder the
project, so the Place tab lists them.
