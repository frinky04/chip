namespace Chip.Core.Geometry.Primitives;

/// <summary>Shapes the block tool can draw (Hammer block tool shape list).</summary>
public enum PrimitiveShape { Box, Cylinder, Wedge, Stairs, Arch, Sphere, Torus, SpiralStairs }

/// <summary>
/// Per-shape parameters. Every shape fits the box the user drags, so sizes here are counts, angles and ratios;
/// the only length is <see cref="ArchThickness"/> (metres). Mutable fields so the inspector can edit them in place.
/// </summary>
public sealed class PrimitiveOptions
{
    /// <summary>Cylinder sides.</summary>
    public int Sides = 16;

    /// <summary>Straight and spiral stairs: number of steps.</summary>
    public int Steps = 8;

    public int ArchSegments = 8;
    /// <summary>Arc swept by the arch, 5..180 degrees (180 = semicircle).</summary>
    public float ArchDegrees = 180;
    /// <summary>Radial thickness of the arch band in metres; clamped to fit the box.</summary>
    public float ArchThickness = 0.25f;

    /// <summary>Latitude bands, from pole to pole.</summary>
    public int SphereRings = 8;
    public int SphereSegments = 16;

    /// <summary>Segments around the ring.</summary>
    public int TorusSegments = 16;
    /// <summary>Segments around the tube.</summary>
    public int TorusSides = 8;
    /// <summary>Tube radius as a fraction of the outer radius, 0.05..0.5.</summary>
    public float TorusTube = 0.25f;

    /// <summary>Inner radius as a fraction of the outer radius, 0 (solid centre) .. 0.9.</summary>
    public float SpiralInner = 0.25f;
    /// <summary>Total turn of a spiral staircase, 30..1080 degrees.</summary>
    public float SpiralDegrees = 360;
    /// <summary>Each spiral step reaches the floor instead of being a floating tread.</summary>
    public bool SpiralSolid;
    public bool SpiralClockwise;

    /// <summary>Curved surfaces (cylinder sides, arch, sphere, torus) get soft edges.</summary>
    public bool Smooth = true;

    public PrimitiveOptions Clone() => (PrimitiveOptions)MemberwiseClone();

    /// <summary>Brings every parameter into its valid range.</summary>
    public void Clamp()
    {
        Sides = Math.Clamp(Sides, 3, 128);
        Steps = Math.Clamp(Steps, 1, 256);
        ArchSegments = Math.Clamp(ArchSegments, 2, 128);
        ArchDegrees = Math.Clamp(ArchDegrees, 5, 180);
        ArchThickness = MathF.Max(ArchThickness, 1e-3f);
        SphereRings = Math.Clamp(SphereRings, 2, 128);
        SphereSegments = Math.Clamp(SphereSegments, 3, 128);
        TorusSegments = Math.Clamp(TorusSegments, 3, 128);
        TorusSides = Math.Clamp(TorusSides, 3, 64);
        TorusTube = Math.Clamp(TorusTube, 0.05f, 0.5f);
        SpiralInner = Math.Clamp(SpiralInner, 0, 0.9f);
        SpiralDegrees = Math.Clamp(SpiralDegrees, 30, 1080);
    }
}
