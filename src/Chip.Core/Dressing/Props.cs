using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Dressing;

/// <summary>
/// Prop meshes for <c>repeat</c> and <c>scatter</c>, loaded once and cached by path and file stamp (so an edited
/// <c>.glb</c> reloads). Built-in shapes need no files. Thread-safe: dressing jobs run in parallel.
/// </summary>
sealed class PropLibrary(IDressingFiles files)
{
    /// <summary>Material for built-in shapes and file props without one.</summary>
    public const string DefaultMaterial = "dev_measurewall01d";

    readonly Dictionary<PropSpec, (long Stamp, PolyMesh? Mesh, string? Error)> _cache = new();

    /// <summary>A number that changes when any of <paramref name="props"/> changes on disk (for job keys).</summary>
    public long Stamp(IEnumerable<PropSpec> props) =>
        props.Where(p => p.Shape == PropShape.File).Aggregate(17L, (h, p) => h * 31 + files.Stamp(p.File!));

    /// <summary>The prop's mesh in its own space, or null with a reason it can't be loaded.</summary>
    public (PolyMesh? Mesh, string? Error) Get(PropSpec spec)
    {
        long stamp = spec.Shape == PropShape.File ? files.Stamp(spec.File!) : 0;
        lock (_cache)
            if (_cache.TryGetValue(spec, out var c) && c.Stamp == stamp) return (c.Mesh, c.Error);
        var (mesh, error) = Load(spec);
        lock (_cache) _cache[spec] = (stamp, mesh, error);
        return (mesh, error);
    }

    (PolyMesh?, string?) Load(PropSpec spec)
    {
        var s = spec.Size;
        switch (spec.Shape)
        {
            case PropShape.Box:
                return (MeshOps.CreateBox(new Vector3(-s.X / 2, 0, -s.Z / 2), new Vector3(s.X / 2, s.Y, s.Z / 2), DefaultMaterial, TextureMapping.Default), null);
            case PropShape.Cylinder:
            {
                var frame = PrimitiveFrame.FromBox(new Vector3(-s.X / 2, 0, -s.X / 2), new Vector3(s.X / 2, s.Y, s.X / 2), Vector3.UnitY, Vector3.UnitX);
                return (Primitive.Build(PrimitiveShape.Cylinder, new PrimitiveOptions { Sides = spec.Sides }, frame, DefaultMaterial, TextureMapping.Default), null);
            }
        }
        var bytes = files.ReadBytes(spec.File!);
        if (bytes == null) return (null, $"can't read prop \"{spec.File}\"");
        try
        {
            var result = GltfImporter.Parse(bytes, Path.GetDirectoryName(spec.File), new MaterialLibrary(), DefaultMaterial);
            if (result.Meshes.Count == 0) return (null, $"prop \"{spec.File}\" has no meshes");
            var merged = result.Meshes[0].Clone();
            MeshOps.MergeMeshes(merged, result.Meshes.Skip(1));
            return (merged, null);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            return (null, $"prop \"{spec.File}\" isn't a readable glTF file: {e.Message}");
        }
    }

    /// <summary>
    /// Appends <paramref name="prop"/> to <paramref name="into"/>, placed by <paramref name="transform"/> (prop space to
    /// world). Faces keep explicit UVs (they're glued to the prop); faces without are world-aligned afterwards by the caller.
    /// <paramref name="material"/> replaces every material when given.
    /// </summary>
    public static void Place(PolyMesh into, PolyMesh prop, Matrix4x4 transform, string? material)
    {
        int offset = into.Positions.Count;
        foreach (var p in prop.Positions) into.Positions.Add(Vector3.Transform(p, transform));
        bool mirrored = transform.GetDeterminant() < 0;
        foreach (var f in prop.Faces)
        {
            var face = f.Clone();
            face.Verts = f.Verts.Select(v => v + offset).ToArray();
            if (mirrored) { Array.Reverse(face.Verts); if (face.UVs != null) Array.Reverse(face.UVs); }
            if (material != null) face.Material = material;
            into.Faces.Add(face);
        }
        foreach (var e in prop.SoftEdges) into.SoftEdges.Add(new Edge(e.A + offset, e.B + offset));
    }

    /// <summary>Prop space (X along, Y up, Z out) to world, at <paramref name="at"/>, scaled uniformly.</summary>
    public static Matrix4x4 Frame(Vector3 at, Vector3 along, Vector3 up, Vector3 outward, float scale = 1) =>
        new(along.X * scale, along.Y * scale, along.Z * scale, 0,
            up.X * scale, up.Y * scale, up.Z * scale, 0,
            outward.X * scale, outward.Y * scale, outward.Z * scale, 0,
            at.X, at.Y, at.Z, 1);
}
