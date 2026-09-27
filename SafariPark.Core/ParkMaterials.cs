using System.Numerics;

using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;

namespace SafariPark;

/// <summary>Shared flat-color material palette for the grove.</summary>
/// <remarks>Scenery uses the terrain/vegetation entries directly. One <see cref="PbrRenderer"/>
/// owns material IDs, so everything is created once at startup.</remarks>
public sealed class ParkMaterials
{
    // Scenery
    public int ForestFloor { get; }
    public int Dirt { get; }
    public int Water { get; }
    public int Trunk { get; }
    public int Canopy { get; }
    public int CanopyAutumn { get; }
    public int Pine { get; }
    public int Rock { get; }
    public int Stump { get; }
    public int Mushroom { get; }
    public int MushroomStem { get; }

    // Throwables
    public int Cone { get; }
    public int Stick { get; }

    // Coat palette for the procedural duck (GLB species carry their own materials).
    public int[] DuckCoat { get; }

    public ParkMaterials(PbrRenderer renderer)
    {
        static Vector4 Rgb(float r, float g, float b, float a = 1f) => new(r, g, b, a);

        // Forest floor: deep mossy green — darker than savanna grass so under-canopy
        // shade reads as dense woods rather than lawn.
        ForestFloor = renderer.Materials.AddDefaultMaterial(Rgb(0.16f, 0.28f, 0.12f), roughness: 0.98f);
        Dirt = renderer.Materials.AddDefaultMaterial(Rgb(0.34f, 0.26f, 0.17f), roughness: 1f);
        Trunk = renderer.Materials.AddDefaultMaterial(Rgb(0.30f, 0.21f, 0.13f), roughness: 0.95f);
        Canopy = renderer.Materials.AddDefaultMaterial(Rgb(0.12f, 0.30f, 0.12f), roughness: 0.92f);
        CanopyAutumn = renderer.Materials.AddDefaultMaterial(Rgb(0.58f, 0.40f, 0.13f), roughness: 0.92f);
        Pine = renderer.Materials.AddDefaultMaterial(Rgb(0.07f, 0.19f, 0.12f), roughness: 0.95f);
        Rock = renderer.Materials.AddDefaultMaterial(Rgb(0.40f, 0.40f, 0.38f), roughness: 0.85f);
        Stump = renderer.Materials.AddDefaultMaterial(Rgb(0.40f, 0.30f, 0.19f), roughness: 0.95f);
        Mushroom = renderer.Materials.AddDefaultMaterial(Rgb(0.72f, 0.16f, 0.12f), roughness: 0.8f);
        MushroomStem = renderer.Materials.AddDefaultMaterial(Rgb(0.82f, 0.76f, 0.62f), roughness: 0.9f);
        Cone = renderer.Materials.AddDefaultMaterial(Rgb(0.42f, 0.26f, 0.12f), roughness: 0.95f);
        Stick = renderer.Materials.AddDefaultMaterial(Rgb(0.35f, 0.24f, 0.13f), roughness: 0.95f);

        // Water: translucent green-blue, low roughness — the only alpha-blend material in
        // the forest, created through AddMaterial because AddDefaultMaterial pins Opaque.
        Water = renderer.Materials.AddMaterial(new GltfMaterialData(
            Name: "pond-water",
            BaseColorFactor: Rgb(0.10f, 0.30f, 0.34f, 0.60f),
            MetallicFactor: 0f,
            RoughnessFactor: 0.06f,
            EmissiveFactor: Vector3.Zero,
            NormalScale: 1f,
            OcclusionStrength: 1f,
            TransmissionFactor: 0f,
            AlphaMode: GltfAlphaMode.Blend,
            AlphaCutoff: 0.5f,
            DoubleSided: true,
            BaseColorImage: -1,
            MetallicRoughnessImage: -1,
            NormalImage: -1,
            OcclusionImage: -1,
            EmissiveImage: -1,
            BaseColorUvTransform: GltfUvTransform.Identity), []);

        int DuckBody = renderer.Materials.AddDefaultMaterial(Rgb(0.50f, 0.47f, 0.44f), roughness: 0.9f);
        int MallardGreen = renderer.Materials.AddDefaultMaterial(Rgb(0.08f, 0.32f, 0.20f), roughness: 0.7f);
        int CreamWhite = renderer.Materials.AddDefaultMaterial(Rgb(0.92f, 0.87f, 0.74f), roughness: 0.9f);
        int BeakOrange = renderer.Materials.AddDefaultMaterial(Rgb(0.85f, 0.55f, 0.12f), roughness: 0.7f);
        DuckCoat = [DuckBody, MallardGreen, CreamWhite, BeakOrange];
    }
}
