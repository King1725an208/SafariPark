using System.Numerics;
using System.Text.Json;

using Paradise.Animation;
using Paradise.Animation.Offline;
using Paradise.Assets.Gltf;
using Paradise.BLOB;
using Paradise.Rendering.Pbr;

namespace SafariPark;

/// <summary>A parsed GLB prepared for runtime use: the skeleton over the whole node tree
/// (the depth-first order <see cref="GltfCook"/> establishes), clips compiled to animation
/// blobs, and per-scene-node meshes uploaded to the renderer.</summary>
/// <remarks>Built once per GLB at startup; every animal shares the skeleton, clips and
/// primitive buffers — only the <see cref="AnimationPlayer"/> and its joint-palette slice
/// are per instance. Clip names keep the source's last "|" segment
/// ("CharacterArmature|Walk" → "Walk") so the species table names a canonical key per state.</remarks>
public sealed class GltfRig : IDisposable
{
    /// <summary>One scene node that carries a mesh: rigid (world transform baked into the
    /// vertices, SkinIndex −1) or skinned against <see cref="SkinIndex"/> through the shared
    /// skeleton.</summary>
    public sealed class Part
    {
        public required PbrMesh Mesh;
        public required int SkinIndex;      // −1 rigid
    }

    /// <summary>Skin data for palette computation: skeleton-space joint indices plus the
    /// inverse-bind matrices the vertex shader expects the palette multiplied by.</summary>
    public sealed class Skin
    {
        public required int[] Joints;               // skin's gltf joints remapped to skeleton order
        public required Matrix4x4[] InverseBinds;
    }

    public required Part[] Parts;
    public required Skin[] Skins;
    public required NativeBlobAssetReference<SkeletonBlob>? Skeleton;
    public required (string Name, NativeBlobAssetReference<AnimationBlob> Clip)[] Clips;
    /// <summary>World-space AABB at rest pose (bind pose for skinned parts).</summary>
    public required Vector3 Min, Max;
    public required string Path;
    public bool IsSkinned => Skeleton is not null && Parts.Any(p => p.SkinIndex >= 0);

    /// <summary>Reads a GLB from disk, builds skeleton/clips, uploads draws.</summary>
    /// <summary>Reads a GLB from disk, normalizing and building skeleton/clips + draws.</summary>
    public static GltfRig Load(PbrRenderer pbr, string path) =>
        Load(pbr, File.ReadAllBytes(path), path);

    /// <summary>Same build from in-memory bytes — the browser host's path.</summary>
    public static GltfRig Load(PbrRenderer pbr, ReadOnlyMemory<byte> bytes, string path) =>
        Build(pbr, GltfSceneReader.Read(NormalizeGlb(bytes)), path);

    /// <summary>Some pack exports double-stash their buffer: a base64 data-URI in the JSON
    /// AND the real bytes in the BIN chunk. The engine contract wants BIN only, so strip
    /// the stale uri field and repack — no data changes hands.</summary>
    private static ReadOnlyMemory<byte> NormalizeGlb(ReadOnlyMemory<byte> glb)
    {
        const uint JsonChunk = 0x4E4F534A;
        var span = glb.Span;
        if (span.Length < 20) return glb;
        var jsonLen = BitConverter.ToInt32(span.Slice(12, 4));
        if (BitConverter.ToUInt32(span.Slice(16, 4)) != JsonChunk) return glb;
        var jsonEnd = 20 + jsonLen;

        using var doc = JsonDocument.Parse(glb.Slice(20, jsonLen));
        var buffers = doc.RootElement.TryGetProperty("buffers", out var b) && b.ValueKind == JsonValueKind.Array
            ? b : (JsonElement?)null;
        if (buffers is not { } bufArr) return glb;
        var stale = false;
        foreach (var buf in bufArr.EnumerateArray())
            if (buf.TryGetProperty("uri", out _)) { stale = true; break; }
        if (!stale) return glb;

        // Re-emit the JSON minus buffers[].uri; pad to 4 bytes with spaces per glTF.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteStripped(doc.RootElement, writer);
        }
        var newJson = ms.ToArray();
        while (newJson.Length % 4 != 0)
        {
            Array.Resize(ref newJson, newJson.Length + 1);
            newJson[^1] = (byte)' ';
        }

        var outLen = 12 + 8 + newJson.Length + (span.Length - jsonEnd);
        var output = new byte[outLen];
        span.Slice(0, 8).CopyTo(output); // magic + version
        BitConverter.GetBytes(outLen).CopyTo(output, 8);
        BitConverter.GetBytes(newJson.Length).CopyTo(output, 12);
        BitConverter.GetBytes(JsonChunk).CopyTo(output, 16);
        newJson.CopyTo(output, 20);
        span.Slice(jsonEnd).CopyTo(output.AsSpan(20 + newJson.Length));
        return output;
    }

    private static void WriteStripped(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("uri") && prop.Value.ValueKind == JsonValueKind.String &&
                        prop.Value.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                        continue;
                    w.WritePropertyName(prop.Name);
                    WriteStripped(prop.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteStripped(item, w);
                w.WriteEndArray();
                break;
            default: el.WriteTo(w); break;
        }
    }

    private static GltfRig Build(PbrRenderer pbr, GltfAsset asset, string path)
    {
        var materialIds = new int[asset.Materials.Length];
        for (var i = 0; i < asset.Materials.Length; i++)
        {
            try
            {
                materialIds[i] = pbr.Materials.AddMaterial(asset.Materials[i], asset.Images);
            }
            catch (DllNotFoundException)
            {
                // No native libktx (browser wasm): drop image refs so materials degrade to
                // their authored factors — Quaternius rigs are mostly solid-color anyway.
                var m = asset.Materials[i];
                materialIds[i] = pbr.Materials.AddMaterial(m with
                {
                    BaseColorImage = -1, MetallicRoughnessImage = -1, NormalImage = -1,
                    OcclusionImage = -1, EmissiveImage = -1,
                }, []);
            }
        }
        var fallback = -1;
        int MatOf(int index) => index >= 0 && index < materialIds.Length
            ? materialIds[index]
            : fallback >= 0 ? fallback : fallback = pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f));

        var jointOf = JointOrder(asset.Nodes);
        var skeleton = asset.Skins.Length > 0 || asset.Animations.Length > 0
            ? BuildSkeleton(asset.Nodes, jointOf) : null;

        var skins = asset.Skins.Select(s => new Skin
        {
            Joints = s.JointNodes.Select(n => jointOf[n]).ToArray(),
            InverseBinds = s.InverseBindMatrices,
        }).ToArray();

        var parts = new List<Part>();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        // Skinned primitives cull against the WHOLE rig's skeleton-space AABB (poses can swing
        // past any one primitive's rest extent), shared per instance since every skinned part
        // shares the skeleton.
        var skinnedMin = new Vector3(float.PositiveInfinity);
        var skinnedMax = new Vector3(float.NegativeInfinity);

        foreach (var instance in asset.Instances)
        {
            var skinned = instance.SkinIndex >= 0 && instance.SkinIndex < skins.Length;
            var meshData = asset.Meshes[instance.MeshIndex];
            var primitives = new PbrPrimitive[meshData.Primitives.Length];

            for (var p = 0; p < primitives.Length; p++)
            {
                var primitive = meshData.Primitives[p];
                var material = MatOf(primitive.MaterialIndex);
                if (skinned && primitive.JointsWeights is { } jw)
                {
                    primitives[p] = pbr.UploadSkinnedPrimitive(primitive.Vertices, jw, primitive.Indices, material);
                    AccumulateBindBounds(asset, instance, primitive, jointOf, ref min, ref max);
                    AccumulateBindBounds(asset, instance, primitive, jointOf, ref skinnedMin, ref skinnedMax);
                }
                else
                {
                    var baked = BakeTransform(primitive.Vertices, instance.WorldTransform);
                    primitives[p] = pbr.UploadPrimitive(baked, primitive.Indices, material);
                    AccumulateBounds(baked, ref min, ref max);
                }
            }
            parts.Add(new Part
            {
                Mesh = new PbrMesh(primitives),
                SkinIndex = skinned ? instance.SkinIndex : -1,
            });
        }
        if (parts.Count == 0) { min = Vector3.Zero; max = Vector3.Zero; }

        // Back-fill the skeleton-space AABB onto skinned primitives (upload-time vertex
        // bounds are mesh-local centimeters — invisible to the frustum).
        if (float.IsFinite(skinnedMin.X))
            foreach (var part in parts)
                if (part.SkinIndex >= 0)
                    part.Mesh = new PbrMesh(part.Mesh.Primitives.Select(pr =>
                        pr with { LocalMin = skinnedMin, LocalMax = skinnedMax }).ToArray());

        var clips = new List<(string, NativeBlobAssetReference<AnimationBlob>)>();
        if (skeleton is { } sk)
        {
            ref var skv = ref sk.Value;
            foreach (var clip in asset.Animations)
            {
                var data = ToClipData(clip, jointOf, clips.Count);
                var raw = ClipConverter.ToRaw(data, ref skv);
                clips.Add((data.Name, AnimationBuilder.Build(raw, iframeInterval: 1f)));
            }
        }

        return new GltfRig
        {
            Parts = [.. parts], Skins = skins, Skeleton = skeleton,
            Clips = [.. clips], Min = min, Max = max, Path = path,
        };
    }

    /// <summary>Clip by normalized name ("Walk"/"Idle"); falls back to substring, then null.</summary>
    public NativeBlobAssetReference<AnimationBlob>? Clip(string name)
    {
        foreach (var (n, c) in Clips)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var (n, c) in Clips)
            if (n.Contains(name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public void Dispose()
    {
        foreach (var (_, c) in Clips) c.Dispose();
        Skeleton?.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Depth-first joint index per glTF node — the same canonical order
    /// <see cref="GltfCook"/> establishes, so skeleton/clips/skins agree.</summary>
    internal static int[] JointOrder(GltfNodeData[] nodes)
    {
        var children = new List<int>[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) children[i] = [];
        var roots = new List<int>();
        for (var i = 0; i < nodes.Length; i++)
        {
            var parent = nodes[i].ParentIndex;
            if (parent < 0) roots.Add(i);
            else if (parent < nodes.Length) children[parent].Add(i);
        }
        var jointOf = new int[nodes.Length];
        Array.Fill(jointOf, -1);
        var next = 0;
        var stack = new Stack<int>();
        for (var r = roots.Count - 1; r >= 0; r--) stack.Push(roots[r]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            jointOf[node] = next++;
            for (var c = children[node].Count - 1; c >= 0; c--) stack.Push(children[node][c]);
        }
        return jointOf;
    }

    private static NativeBlobAssetReference<SkeletonBlob> BuildSkeleton(GltfNodeData[] nodes, int[] jointOf)
    {
        var count = nodes.Length;
        var names = new string[count];
        var parents = new short[count];
        var poses = new JointPose[count];
        for (var node = 0; node < count; node++)
        {
            var joint = jointOf[node];
            names[joint] = nodes[node].Name ?? "";
            parents[joint] = nodes[node].ParentIndex < 0 ? SkeletonBlob.NoParent : (short)jointOf[nodes[node].ParentIndex];
            poses[joint] = new JointPose(nodes[node].RestTranslation, nodes[node].RestRotation, nodes[node].RestScale).WithNormalizedRotation();
        }
        return SkeletonBlob.Create(names, parents, poses);
    }

    private static ClipData ToClipData(GltfAnimationData clip, int[] jointOf, int index)
    {
        var name = clip.Name ?? $"clip_{index}";
        var bar = name.LastIndexOf('|');
        if (bar >= 0) name = name[(bar + 1)..];
        var channels = clip.Channels.Select(c => new ClipChannelData(
            c.NodeIndex >= 0 ? jointOf[c.NodeIndex] : 0,
            c.Path switch
            {
                GltfAnimationPath.Translation => ChannelPath.Translation,
                GltfAnimationPath.Rotation => ChannelPath.Rotation,
                _ => ChannelPath.Scale,
            },
            c.Step, c.Times, c.Values)).ToList();
        return new ClipData(name, channels);
    }

    /// <summary>Rest-pose AABB over a skinned primitive: per-vertex skinning against the
    /// bind matrices times the skeleton's rest world transforms.</summary>
    private static void AccumulateBindBounds(GltfAsset asset, GltfMeshInstance instance,
        GltfPrimitive primitive, int[] jointOf, ref Vector3 min, ref Vector3 max)
    {
        var skin = asset.Skins[instance.SkinIndex];
        // World transforms MUST be evaluated parent-first: glTF node arrays routinely list
        // children before their parents (Quaternius packs), so index order leaves parents at
        // Matrix4x4's zero default — every bind matrix collapses to 0 and the bounds report a
        // 5-centimeter deer. `jointOf` is already depth-first.
        var nodes = asset.Nodes;
        // Inverse of jointOf: node whose DFS index is j.
        var nodesByJoint = new int[nodes.Length];
        for (var node = 0; node < nodes.Length; node++) nodesByJoint[jointOf[node]] = node;
        var world = new Matrix4x4[nodes.Length]; // indexed by JOINT, not glTF node
        for (var j = 0; j < nodesByJoint.Length; j++)
        {
            var n = nodes[nodesByJoint[j]];
            var local = TRS(n.RestTranslation, n.RestRotation, n.RestScale);
            world[j] = n.ParentIndex >= 0 ? local * world[jointOf[n.ParentIndex]] : local;
        }
        var bind = new Matrix4x4[skin.JointNodes.Length];
        for (var j = 0; j < bind.Length; j++)
            bind[j] = skin.InverseBindMatrices[j] * world[jointOf[skin.JointNodes[j]]];
        var jw = primitive.JointsWeights!;
        for (var v = 0; v < primitive.VertexCount; v++)
        {
            var vo = v * GltfPrimitive.FloatsPerVertex;
            var p = new Vector3(primitive.Vertices[vo], primitive.Vertices[vo + 1], primitive.Vertices[vo + 2]);
            var so = v * GltfPrimitive.SkinFloatsPerVertex;
            var acc = Vector3.Zero;
            for (var k = 0; k < 4; k++)
            {
                var w = jw[so + 4 + k];
                if (w <= 0f) continue;
                acc += Vector3.Transform(p, bind[(int)jw[so + k]]) * w;
            }
            min = Vector3.Min(min, acc);
            max = Vector3.Max(max, acc);
        }
    }

    private static void AccumulateBounds(float[] vertices, ref Vector3 min, ref Vector3 max)
    {
        for (var i = 0; i + 2 < vertices.Length; i += GltfPrimitive.FloatsPerVertex)
        {
            var p = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }

    private static Matrix4x4 TRS(Vector3 t, Quaternion r, Vector3 s) =>
        Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

    /// <summary>Positions through the matrix, normals/tangents through its cofactor matrix —
    /// the same bake <see cref="GltfCook"/> runs for rigid glTF draws.</summary>
    private static float[] BakeTransform(float[] vertices, in Matrix4x4 transform)
    {
        if (transform.IsIdentity) return vertices;
        var m = transform;
        var nm = PbrMath.NormalMatrix(in m);
        var baked = new float[vertices.Length];
        for (var i = 0; i + 11 < vertices.Length; i += GltfPrimitive.FloatsPerVertex)
        {
            var pos = Vector3.Transform(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]), m);
            var nrm = Vector3.TransformNormal(new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]), nm);
            var nl = nrm.LengthSquared(); if (nl > 1e-12f) nrm /= MathF.Sqrt(nl); else nrm = Vector3.UnitY;
            var tng = Vector3.TransformNormal(new Vector3(vertices[i + 8], vertices[i + 9], vertices[i + 10]), m);
            var tl = tng.LengthSquared(); if (tl > 1e-12f) tng /= MathF.Sqrt(tl);
            baked[i] = pos.X; baked[i + 1] = pos.Y; baked[i + 2] = pos.Z;
            baked[i + 3] = nrm.X; baked[i + 4] = nrm.Y; baked[i + 5] = nrm.Z;
            baked[i + 6] = vertices[i + 6]; baked[i + 7] = vertices[i + 7];
            baked[i + 8] = tng.X; baked[i + 9] = tng.Y; baked[i + 10] = tng.Z;
            baked[i + 11] = vertices[i + 11];
        }
        return baked;
    }

}

/// <summary>One placed rig: PBR instances for its parts (each skinned part owns a joint-palette
/// slice), the <see cref="AnimationPlayer"/> and the model transform every part shares.</summary>
public sealed class RigVisual : IDisposable
{
    public sealed class Part
    {
        public required PbrInstance Instance;
        public required int JointOffset;   // palette base, −1 rigid
        public required int JointCount;    // slots in the palette slice, 0 rigid
        public required int SkinIndex;
    }

    public required GltfRig Rig;
    public required AnimationPlayer? Player;
    public required Part[] Parts;
    public required float Scale;
    /// <summary>Extra yaw that turns the model's authored forward axis to face −Z.</summary>
    public float YawOffset;
    /// <summary>Ground contact correction: −rig.Min.Y × Scale, so feet land on the terrain.</summary>
    public float GroundOffset;
    public string CurrentClip = "";
    public string? OneShot;

    private Matrix4x4 _model = Matrix4x4.Identity;

    /// <summary>Pose + placement for all parts; call once per frame after animating.</summary>
    /// <param name="pitch">Pre-yaw pitch around the model's right axis — a negative value
    /// tips the nose upward (the cat's climbing pose, belly against the trunk).</param>
    public void SetModel(Vector3 position, float yaw, float pitch = 0f)
    {
        _model = Matrix4x4.CreateScale(Scale)
            * (pitch != 0f ? Matrix4x4.CreateRotationX(pitch) : Matrix4x4.Identity)
            * Matrix4x4.CreateRotationY(yaw + YawOffset)
            * Matrix4x4.CreateTranslation(position + new Vector3(0f, GroundOffset, 0f));
        foreach (var part in Parts) part.Instance.Model = _model;
    }

    /// <summary>Advances the player and pushes every skinned part's palette to the renderer.</summary>
    public void AdvanceAndPose(PbrRenderer pbr, float dt)
    {
        if (Player is null || Rig.Skeleton is null) return;
        Player.Advance(dt);
        Player.Evaluate();
        var models = Player.ModelMatrices;
        foreach (var part in Parts)
        {
            if (part.SkinIndex < 0) continue;
            var skin = Rig.Skins[part.SkinIndex];
            // One scratch buffer per visual, grown on demand — stackalloc in a loop
            // accumulates until the method returns.
            if (_paletteScratch.Length < part.JointCount)
                _paletteScratch = new Matrix4x4[part.JointCount];
            var palette = _paletteScratch.AsSpan(0, part.JointCount);
            // meshJoint -1: a glTF skinned mesh ignores its own node transform, and our
            // instance Model is the placement transform — not the node's authored world.
            SkinningPalette.Compute(models, skin.Joints, skin.InverseBinds, -1, palette);
            pbr.SetJointPalette(part.JointOffset, palette);
        }
    }

    private Matrix4x4[] _paletteScratch = [];

    public void Dispose() => Player?.Dispose();

    /// <summary>Creates the instances (one per rig part) and the player for a skinned rig.</summary>
    /// <param name="paletteNext">Bump allocator into the renderer's joint palette buffer.</param>
    public static RigVisual Create(GltfRig rig, float scale, ref int paletteNext)
    {
        var player = rig.Skeleton is { } sk ? new AnimationPlayer(sk) : null;
        var parts = new Part[rig.Parts.Length];
        var i = 0;
        foreach (var part in rig.Parts)
        {
            var joints = part.SkinIndex >= 0 ? rig.Skins[part.SkinIndex].Joints.Length : 0;
            var offset = -1;
            if (joints > 0 && !KittyGame.NoSkinning)
            {
                offset = paletteNext;
                paletteNext += joints;
            }
            parts[i++] = new Part
            {
                Instance = new PbrInstance { Mesh = part.Mesh, JointOffset = offset },
                JointOffset = offset,
                JointCount = joints,
                SkinIndex = part.SkinIndex,
            };
        }
        return new RigVisual
        {
            Rig = rig, Player = player, Parts = parts, Scale = scale,
            GroundOffset = -rig.Min.Y * scale,
        };
    }
}
