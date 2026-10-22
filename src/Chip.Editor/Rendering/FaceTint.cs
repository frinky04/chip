using System.Numerics;

namespace Chip.Editor.Rendering;

/// <summary>A blockout face drawn tinted over the scene (<see cref="Face"/> -1: every face of the mesh), optionally striped.</summary>
public readonly record struct FaceTint(int Mesh, int Face, Vector4 Color, bool Striped = false);
