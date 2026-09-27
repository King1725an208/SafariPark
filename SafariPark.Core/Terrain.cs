using System.Numerics;

namespace SafariPark;

/// <summary>Gently rolling grass field for the lite build — a much smaller playground than
/// the V1 forest. A single analytic height function keeps rendering, gameplay and the
/// camera sampling the same ground. RH, Y-up, −Z forward, meters.</summary>
public static class Terrain
{
    /// <summary>Half-extent of the playable area: a ~46×46 m grove.</summary>
    public const float HalfSize = 23f;

    /// <summary>Ground height: soft mounds only — no pond, no steep slopes.</summary>
    public static float Height(float x, float z)
    {
        return
            0.55f * MathF.Sin(x * 0.11f + 1.2f) * MathF.Cos(z * 0.09f - 0.5f) +
            0.22f * MathF.Sin(x * 0.26f - 0.8f) * MathF.Sin(z * 0.23f + 1.9f);
    }

    public static float Height(Vector3 p) => Height(p.X, p.Z);

    /// <summary>Surface normal from central differences.</summary>
    public static Vector3 Normal(float x, float z)
    {
        const float e = 0.35f;
        var dx = Height(x + e, z) - Height(x - e, z);
        var dz = Height(x, z + e) - Height(x, z - e);
        return Vector3.Normalize(new Vector3(-dx / (2f * e), 1f, -dz / (2f * e)));
    }

    /// <summary>Ground triangle mesh in the PBR vertex layout (pos3/normal3/uv2/tangent4).</summary>
    /// <returns>Interleaved floats and triangle indices ready for UploadPrimitive.</returns>
    public static (float[] Vertices, uint[] Indices) BuildMesh(int resolution = 48)
    {
        int vertsPerSide = resolution + 1;
        var vertices = new float[vertsPerSide * vertsPerSide * 12];
        var indices = new uint[resolution * resolution * 6];

        var w = 0;
        for (int iz = 0; iz < vertsPerSide; iz++)
        {
            for (int ix = 0; ix < vertsPerSide; ix++)
            {
                float x = -HalfSize + HalfSize * 2f * ix / resolution;
                float z = -HalfSize + HalfSize * 2f * iz / resolution;
                float y = Height(x, z);
                var n = Normal(x, z);

                vertices[w++] = x; vertices[w++] = y; vertices[w++] = z;
                vertices[w++] = n.X; vertices[w++] = n.Y; vertices[w++] = n.Z;
                vertices[w++] = ix * 0.25f; vertices[w++] = iz * 0.25f;
                // Tangent along +X (east) — consistent with the heightfield's XZ grid.
                vertices[w++] = 1f; vertices[w++] = 0f; vertices[w++] = 0f; vertices[w++] = 1f;
            }
        }

        var i = 0;
        for (int iz = 0; iz < resolution; iz++)
        {
            for (int ix = 0; ix < resolution; ix++)
            {
                uint a = (uint)(iz * vertsPerSide + ix);
                uint b = a + 1;
                uint c = a + (uint)vertsPerSide;
                uint d = c + 1;
                indices[i++] = a; indices[i++] = c; indices[i++] = b;
                indices[i++] = b; indices[i++] = c; indices[i++] = d;
            }
        }
        return (vertices, indices);
    }
}
