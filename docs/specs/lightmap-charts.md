# Spec: export lightmap charts

Add a project setting that makes `GlbExporter` write each drawn face's **chart**: a flat piece of a level, laid out in
real units, that a game's lightmap baker packs into its atlas. This brief is for the agent making the change; delete it
once the work is merged.

A **chart** is a set of drawn faces of one glTF mesh, connected through shared edges, lying on one plane and facing the
same way. Materials don't split a chart, so one wall painted in two materials is one chart.

Chip stops at charts. Packing, texel density, padding and baking belong to the game, which is why the UVs stay unpacked:
the game can rebake at a different density without a new export.

## The contract

With `"lightmapCharts": true` in `chip.project.json`, every primitive written with a material gains two attributes:

| Attribute | Accessor | Value at each corner |
|---|---|---|
| `TEXCOORD_1` | float `VEC2` | Position in the chart's plane, in the same units as `POSITION` (so the export scale applies). Each chart's minimum is exactly `(0, 0)`. |
| `_LM_CHART` | float `SCALAR` | The chart's ID, a whole number from 0, counted per glTF mesh across all its primitives. |

- **Same size as the world:** a triangle's area in `TEXCOORD_1` equals its area in `POSITION`, on any planar face.
- **Tight fit:** the chart's axes are the edge direction, among all its edges projected onto its plane, that gives the
  smallest bounding rectangle. The first such edge in face order wins a tie. A diagonal wall therefore packs as tightly
  as a straight one.
- **Unmirrored:** `v = normal × u`.
- **Deterministic:** the same document exports byte-identical files. The game hashes the `.glb` to detect stale bakes.
- **Instances:** linked copies keep sharing one glTF mesh, so their chart IDs repeat. The game keys charts by
  `(node, chart)` and gives each copy its own atlas space.
- **Everything drawn gets charts,** including sky faces, surfaces that draw without colliding, and mesh entities' meshes.
  The game decides which charts it bakes, from material names and `chip_*` extras.

**Coplanar test:** two faces sharing an edge join when their normals' dot product is at least `0.9999` and each lies
within 1 mm of the other's plane, measured in document units before the export scale. Faces joined only by a soft edge
stay in separate charts unless they pass this test, as Quake's and Half-Life's per-face lightmaps did. A non-planar face
is its own chart unless it passes too, projected onto the plane of its `FaceNormal`.

## Steps

1. **Add the setting.** Put `LightmapCharts` on `GameProject` (default `false`, `src/Chip.Core/Scene/GameProject.cs`),
   following `CollisionSuffixes`: parse `lightmapCharts`, add it to the unknown-field message's known list, and report
   it in `EntityManifestCheck`'s text and JSON output. Done when `--entities-check` prints the value and an existing
   project file without the key loads as before.
2. **Write the charts.** In `Primitives` (`src/Chip.Core/IO/GlbExporter.cs`), when the setting is on and `withMaterial`
   is true, group `faces` into charts before the per-material grouping. Then emit both attributes beside `TEXCOORD_0`.
   Lay charts out from the written positions (`Vector3.Transform(p, place) * s`), so origins, mesh entities and the
   export scale come out right. `Primitives` already writes corners per face, so each vertex belongs to one chart.
   A tessellated mesh goes through the same path, so a wall cut by the grid is still one chart. Done when every drawn
   primitive has both attributes at the same `count` as `POSITION`, and collider-only primitives have neither.
3. **Test it** in `tests/Chip.Core.Tests/`, reading files back through the `Glb` helper. Cover each distinct failure:
   - Setting off: neither attribute is present.
   - A box: 6 charts, every chart's minimum at `(0, 0)`, and each triangle's UV area matching its world area.
   - Two coplanar faces in different materials: one chart ID across both primitives.
   - A box rotated 45° about Y: each wall's UV extent matches its real width and height.
   - Linked copies: their nodes still reference one glTF mesh.
   - Export scale 100: UV extents scale by 100.
   - Tessellation on: all cells of one wall share one chart ID.
   - Exporting twice gives identical bytes.

   Done when `dotnet test` is green across the solution.
4. **Check a real export.** Copy `tests/fixtures/level.chip` into a scratch project with the setting on, and run
   `Chip.exe --export` on it. Done when it exits 0 with no new warnings, and the attributes pass step 2's check in the
   file.
5. **Update the docs.**
   - `skills/chip/reference/export.md`: add a "Lightmap charts" section holding this contract, and correct "Mesh nodes
     have no transform". `GlbExporter` places a mesh with its own origin at that origin, and linked copies share a glTF
     mesh, so a loader must apply the node's transform.
   - `skills/chip/reference/manifest.md`: add a `lightmapCharts` row beside `collisionSuffixes`.

   Done when a reader of `export.md` alone could write the game's chart loader.

Leave the work uncommitted for the developer to review.

## How the game will use this

HumanCompiler sets `lightmapCharts`, reads both attributes in `Level.AddRenderMesh`, and packs each `(node, chart)` at
the current bake's density: `atlasUv = (TEXCOORD_1 × density + chartOffset) / atlasSize`. Its preview and final bakes
differ only in density and sample count, using the same export.

The export stays free of baker concerns. Per-mesh bake options use the existing mesh tags (`chip_tags`), and
light-emitting materials and sky faces are identified by material name, so neither needs a change here.
