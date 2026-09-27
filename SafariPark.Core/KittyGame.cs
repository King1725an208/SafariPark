using System.Numerics;

using Paradise.Features;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Windowing;

namespace SafariPark;

/// <summary>The lite build: one kitten in a small grove of climbable, platform-topped trees.
/// Made for kids — bump a trunk to grab it, W/S to climb, Space to leap; airborne cats
/// auto-catch trunks so platform hops are forgiving. Every crown is a walkable platform,
/// some trees stack two. Stamina drains on running and jumping, berries restore it.
/// No ECS, no extra animals, no SSAO — one skinned cat and instanced box trees.</summary>
public sealed class KittyGame : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly ParkMaterials _materials;
    private readonly Func<string, byte[]> _readAsset;

    /// <summary>QA kill switch: force the skinned cat onto the rigid path (bind pose).</summary>
    public static bool NoSkinning;

    private uint _width;
    private uint _height;
    private float _elapsed;
    private readonly Random _rand = new(20260927);

    // ------------------------------------------------------------- cat state (no ECS)

    private GltfRig? _catRig;
    private RigVisual? _cat;
    private int _paletteNext;
    private string _clip = "";
    private Vector3 _pos;
    private float _yaw;
    private float _pitch;          // model pitch: ~−86° hugging a trunk, 0 elsewhere
    private Vector3 _vel;          // horizontal velocity (kept while airborne)
    private float _vy;             // vertical velocity
    private bool _grounded = true;
    private bool _moving;
    private int _standingPerch = -1; // platform index we're standing on, −1 = ground
    private int _treeHops;           // platforms landed on after a jump — the score
    private float _stamina = 1f;     // 0..1: running and jumping drain it
    private float _eatTimer;         // short window after a berry: play the eat clip

    // ------------------------------------------------------------- grove

    /// <summary>A climbable trunk. Every crown platform above it is a <see cref="Perch"/>.</summary>
    private sealed class Tree
    {
        public required float X;
        public required float Z;
        public required float TrunkTop;    // highest cling height (world Y)
    }

    /// <summary>Flat standable platform — a tree crown top. Several per tree is fine.</summary>
    private sealed class Perch
    {
        public required float X;
        public required float Z;
        public required float Radius;
        public required float Top;
        public required int TreeIndex;
    }

    /// <summary>A berry pickup: spin/bob visual; eating it restores stamina.</summary>
    private sealed class Berry
    {
        public required PbrInstance Instance;
        public required Vector3 Pos;
        public float Spin;
        public float Phase;
    }

    private readonly List<Tree> _trees = [];
    private readonly List<Perch> _perches = [];
    private readonly List<Berry> _berries = [];
    private PbrMesh _berryMesh = new([]);
    private float _berryTimer = 2f;
    private const int MaxBerries = 10;
    private const float EatRadius = 0.55f;
    private int _cling = -1;               // trunk index while hugging a tree

    // ------------------------------------------------------------- camera + input

    private float _camYaw = 0.8f;
    private float _camPitch = 0.30f;
    private float _camDistance = 5.0f;
    private Vector3 _camEye;
    private bool _camInitialized;
    private Matrix4x4 _view, _proj;

    private readonly HashSet<KeyboardKey> _keys = [];
    private Vector2 _moveInput;
    private bool _jumpQueued;
    private bool _lmbDown;

    // Cat feel: brisk run, floaty-forgiving jump, snappy turn.
    private const float RunSpeed = 5.2f;
    private const float JumpSpeed = 7.2f;
    private const float Gravity = 14.5f;
    private const float AirAccel = 22f;
    private const float ClimbSpeed = 2.9f;
    private const float ClingPitch = -1.5f;   // ~−86°: nose up, belly against the bark
    private const float GrabRadius = 0.58f;   // air auto-grab distance to a trunk
    private const float BumpRadius = 0.33f;   // ground contact grab distance

    // Stamina: running drains 12%/s, a jump costs 15%; resting recovers slowly.
    private const float RunDrain = 0.12f;
    private const float JumpCost = 0.15f;
    private const float IdleRegen = 0.10f;
    private const float ClingRegen = 0.06f;
    private const float EatGain = 0.40f;

    public KittyGame(IRenderer renderer, FeatureSwitches switches, uint width, uint height,
        Microsoft.Extensions.Logging.ILogger? logger = null, Func<string, byte[]>? readAsset = null)
    {
        _renderer = renderer;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, switches, _width, _height, logger: logger);
        _materials = new ParkMaterials(_pbr);
        _readAsset = readAsset ?? (name =>
        {
            var besideBinary = Path.Combine(AppContext.BaseDirectory, "Assets", name);
            return File.ReadAllBytes(File.Exists(besideBinary) ? besideBinary : Path.Combine("Assets", name));
        });

        BuildWorld();
        BuildCat();
    }

    // ------------------------------------------------------------------ world

    private void BuildWorld()
    {
        // Ground.
        var (gv, gi) = Terrain.BuildMesh();
        _scene.Instances.Add(new PbrInstance
        {
            Mesh = new PbrMesh([_pbr.UploadPrimitive(gv, gi, _materials.ForestFloor)]),
            Model = Matrix4x4.Identity,
        });

        // Three procedural tree meshes shared by every instance — each crown is a
        // walkable platform, and pine/tall trees carry two tiers.
        var pine = BuildPineMesh();
        var round = BuildRoundMesh();
        var tall = BuildTallMesh();
        _berryMesh = BuildBerryMesh();
        var rand = _rand;

        void Plant(float x, float z, float scale)
        {
            float roll = (float)rand.NextDouble();
            int kind = roll < 0.40f ? 0 : roll < 0.70f ? 1 : 2; // pine / round / tall
            float y = Terrain.Height(x, z);
            var model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY((float)rand.NextDouble() * MathF.Tau)
                * Matrix4x4.CreateTranslation(new Vector3(x, y, z));
            _scene.Instances.Add(new PbrInstance
            {
                Mesh = kind == 0 ? pine : kind == 1 ? round : tall,
                Model = model,
            });

            int ti = _trees.Count;
            // Local tops (see the builders): pine platforms 2.90/5.00, round 4.70,
            // tall 3.70/6.10 — all scaled; cling tops sit just below the highest platform.
            float trunkTop = kind == 0 ? 4.85f : kind == 1 ? 4.55f : 5.95f;
            _trees.Add(new Tree { X = x, Z = z, TrunkTop = y + trunkTop * scale });
            void P(float top, float r) => _perches.Add(new Perch
            {
                X = x, Z = z, Top = y + top * scale, Radius = r * scale, TreeIndex = ti,
            });
            switch (kind)
            {
                case 0: P(2.90f, 1.25f); P(5.00f, 0.90f); break;
                case 1: P(4.70f, 1.40f); break;
                default: P(3.70f, 1.30f); P(6.10f, 0.90f); break;
            }
        }

        // Jittered grid: ~3.0 m spacing and wide crowns form a dense vertical forest —
        // neighbouring platforms overlap in jump range at staggered heights.
        for (float gx = -9.5f; gx <= 9.5f; gx += 3.0f)
        {
            for (float gz = -9.5f; gz <= 9.5f; gz += 3.0f)
            {
                float x = gx + ((float)rand.NextDouble() - 0.5f) * 1.1f;
                float z = gz + ((float)rand.NextDouble() - 0.5f) * 1.1f;
                if (x * x + z * z < 2.8f * 2.8f) continue; // keep the spawn clearing open
                if (MathF.Abs(x) > Terrain.HalfSize - 1.5f || MathF.Abs(z) > Terrain.HalfSize - 1.5f) continue;
                Plant(x, z, 0.75f + (float)rand.NextDouble() * 0.45f);
            }
        }

        // Tall boundary ring so the world edge reads as a wall of trees.
        for (int i = 0; i < 14; i++)
        {
            float a = i / 14f * MathF.Tau + (float)rand.NextDouble() * 0.12f;
            float r = Terrain.HalfSize - 1.1f - (float)rand.NextDouble() * 0.8f;
            Plant(MathF.Cos(a) * r, MathF.Sin(a) * r, 1.1f + (float)rand.NextDouble() * 0.25f);
        }

        // Starter berries so the first climb already pays off.
        for (int i = 0; i < 7; i++) SpawnBerry();

        // Bright friendly daylight — one 1024 shadow map, fog closes in early.
        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.55f, 1.0f, 0.35f)),
            Color = new Vector3(1.0f, 0.96f, 0.85f),
            Intensity = 1.5f,
            CastsShadows = true,
            ShadowResolution = 1024,
            ShadowAngularDiameter = 0.5f,
        });
        _scene.Ambient = new PbrAmbient
        {
            Sky = new Vector3(0.28f, 0.34f, 0.40f),
            Equator = new Vector3(0.20f, 0.22f, 0.15f),
            Ground = new Vector3(0.09f, 0.10f, 0.07f),
        };
        _scene.Fog = new PbrFog
        {
            Enabled = true,
            Density = 0.018f,
            Color = new Vector3(0.45f, 0.55f, 0.48f),
            StartDistance = 10f,
            MaxDistance = 45f,
        };
        _scene.HasSkyBackground = true;
        _scene.SkyTopColor = new Vector3(0.25f, 0.45f, 0.62f);
        _scene.SkyHorizonColor = new Vector3(0.55f, 0.65f, 0.55f);
        _scene.SkyGroundHorizon = new Vector3(0.20f, 0.22f, 0.14f);
        _scene.SkyGroundBottom = new Vector3(0.08f, 0.09f, 0.06f);
        _scene.SkyReflections = true;
        _scene.SkySunEnabled = true;
        _scene.SkySunDirection = _scene.Lights[0].Direction;
        _scene.SkySunColorEnergy = new Vector3(1.0f, 0.96f, 0.85f) * 3f;
        _scene.Tonemap = new PbrTonemap { Exposure = 1.1f };
        _scene.Fxaa = new PbrFxaa { Enabled = true };
        // Perf: SSAO and contact shadows stay off — the V1 grove spent real ms there.
        _scene.Ssao = new PbrSsao { Enabled = false };
        _scene.ContactShadows = new PbrContactShadows { Enabled = false };
    }

    /// <summary>Tiered pine: two wide platforms far apart at 2.90 and 5.00 (local units).</summary>
    private PbrMesh BuildPineMesh()
    {
        var b = new MeshBuilder();
        b.AddBox(0, new Vector3(0.14f, 2.40f, 0.14f), new Vector3(0f, 2.40f, 0f));  // trunk 0..4.8
        b.AddBox(1, new Vector3(1.35f, 0.45f, 1.35f), new Vector3(0f, 2.45f, 0f));  // platform 2.00..2.90
        b.AddBox(1, new Vector3(0.95f, 0.45f, 0.95f), new Vector3(0f, 4.55f, 0f));  // platform 4.10..5.00
        b.AddBox(1, new Vector3(0.45f, 0.35f, 0.45f), new Vector3(0f, 5.35f, 0f));  // tip 5.00..5.70
        return Upload(b, _materials.Trunk, _materials.Pine);
    }

    /// <summary>Lollipop broadleaf: single wide flat platform at 4.70 (local units).</summary>
    private PbrMesh BuildRoundMesh()
    {
        var b = new MeshBuilder();
        b.AddBox(0, new Vector3(0.15f, 1.90f, 0.15f), new Vector3(0f, 1.90f, 0f));  // trunk 0..3.8
        b.AddBox(1, new Vector3(1.55f, 0.55f, 1.55f), new Vector3(0f, 4.15f, 0f));  // canopy 3.60..4.70
        return Upload(b, _materials.Trunk, _materials.Canopy);
    }

    /// <summary>Two-storey broadleaf: mid platform at 3.70, top platform at 6.10 —
    /// tiers separated by ~2.4 so they read as two distinct floors.</summary>
    private PbrMesh BuildTallMesh()
    {
        var b = new MeshBuilder();
        b.AddBox(0, new Vector3(0.15f, 3.00f, 0.15f), new Vector3(0f, 3.00f, 0f));  // trunk 0..6.0
        b.AddBox(1, new Vector3(1.45f, 0.50f, 1.45f), new Vector3(0f, 3.20f, 0f));  // platform 2.70..3.70
        b.AddBox(1, new Vector3(1.00f, 0.50f, 1.00f), new Vector3(0f, 5.60f, 0f));  // platform 5.10..6.10
        return Upload(b, _materials.Trunk, _materials.CanopyAutumn);
    }

    /// <summary>Small berry lump — the red mushroom palette reads as fruit.</summary>
    private PbrMesh BuildBerryMesh()
    {
        var b = new MeshBuilder();
        b.AddBox(0, new Vector3(0.07f, 0.06f, 0.07f), Vector3.Zero);
        b.AddBox(1, new Vector3(0.02f, 0.03f, 0.02f), new Vector3(0f, 0.08f, 0f)); // stem
        return Upload(b, _materials.Mushroom, _materials.MushroomStem);
    }

    /// <summary>Uploads each MeshBuilder material group and merges into one PbrMesh.</summary>
    private PbrMesh Upload(MeshBuilder builder, params int[] slots)
    {
        var parts = builder.Build();
        var primitives = new PbrPrimitive[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            primitives[i] = _pbr.UploadPrimitive(parts[i].Vertices, parts[i].Indices, slots[parts[i].Material]);
        return new PbrMesh(primitives);
    }

    // ------------------------------------------------------------------ cat

    private void BuildCat()
    {
        _catRig = GltfRig.Load(_pbr, _readAsset("cat.glb"), "cat.glb");
        float scale = 0.55f / Math.Max(0.05f, _catRig.Max.Y - _catRig.Min.Y);
        _cat = RigVisual.Create(_catRig, scale, ref _paletteNext);
        _cat.YawOffset = MathF.PI; // authored Quaternius rigs face +Z; the game faces −Z
        _pos = new Vector3(0f, Terrain.Height(0f, 0f), 0f);
        foreach (var part in _cat.Parts)
            _scene.Instances.Add(part.Instance);
        PlayClip("Idle", fade: 0f);
    }

    private void PlayClip(string name, float fade = 0.15f)
    {
        if (_cat?.Player is null || name == _clip) return;
        var clip = _cat.Rig.Clip(name);
        if (clip is null) return;
        _cat.Player.Play(clip, fadeSeconds: fade, loop: true);
        _clip = name;
    }

    // ------------------------------------------------------------------ input

    /// <summary>Forwards a window event to the game.</summary>
    public void OnEvent(in WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Button when e.Source == EventSource.Keyboard:
                if (e.Pressed) _keys.Add(e.KeyboardKey);
                else _keys.Remove(e.KeyboardKey);
                if (e.Pressed && e.KeyboardKey == KeyboardKey.Space) _jumpQueued = true;
                break;

            case WindowEventKind.Button when e.Source == EventSource.Mouse:
                if (e.PointerButton == PointerButton.Left) _lmbDown = e.Pressed;
                break;

            case WindowEventKind.Scroll:
                _camDistance = Math.Clamp(_camDistance * (1f - e.Y * 0.12f), 2.5f, 14f);
                break;
        }
    }

    /// <summary>Pointer drag in pixels — hosts forward the per-event delta.</summary>
    public void OnDrag(float dx, float dy)
    {
        if (!_lmbDown) return;
        _camYaw += dx * 0.012f;
        _camPitch = Math.Clamp(_camPitch + dy * 0.009f, -0.1f, 1.2f);
    }

    // ------------------------------------------------------------------ update

    public void Update(float dt)
    {
        _elapsed += dt;
        dt = MathF.Min(dt, 0.05f);
        if (_eatTimer > 0f) _eatTimer -= dt;

        // WASD and arrow keys both move — kids grab whichever.
        _moveInput = new Vector2(
            ((_keys.Contains(KeyboardKey.D) || _keys.Contains(KeyboardKey.Right)) ? 1f : 0f)
            - ((_keys.Contains(KeyboardKey.A) || _keys.Contains(KeyboardKey.Left)) ? 1f : 0f),
            ((_keys.Contains(KeyboardKey.W) || _keys.Contains(KeyboardKey.Up)) ? 1f : 0f)
            - ((_keys.Contains(KeyboardKey.S) || _keys.Contains(KeyboardKey.Down)) ? 1f : 0f));

        if (_cling >= 0) TickCling(dt);
        else TickMove(dt);

        // Stamina follows what the cat is doing.
        if (_grounded)
            _stamina += (_moving ? -RunDrain : IdleRegen) * dt;
        else if (_cling >= 0)
            _stamina += ClingRegen * dt;
        else
            _stamina += 0.02f * dt; // barely resting mid-air
        _stamina = Math.Clamp(_stamina, 0f, 1f);

        TickBerries(dt);

        // Animation + model pitch follow physics state.
        string clip = _cling >= 0 ? "Idle"
            : !_grounded ? "Jump_Loop"
            : _moving ? "Run"
            : _eatTimer > 0f ? "Idle_Eating"
            : "Idle";
        PlayClip(clip);

        float pitchTarget = _cling >= 0 ? ClingPitch : 0f;
        _pitch += (pitchTarget - _pitch) * MathF.Min(1f, 12f * dt);
        if (MathF.Abs(_pitch) < 0.01f && pitchTarget == 0f) _pitch = 0f;

        _cat?.AdvanceAndPose(_pbr, dt);
        var modelPos = _pos + new Vector3(0f, _pitch * 0.13f, 0f); // hang the body low on the trunk
        _cat?.SetModel(modelPos, _yaw, _pitch);

        _jumpQueued = false;
        UpdateCamera(dt);
    }

    /// <summary>Camera-relative move direction in world space, or zero.</summary>
    private Vector3 MoveDir()
    {
        if (_moveInput.LengthSquared() == 0f) return Vector3.Zero;
        var dir = Vector2.Normalize(_moveInput);
        var fwd = new Vector3(MathF.Sin(_camYaw), 0f, MathF.Cos(_camYaw));
        var right = new Vector3(-fwd.Z, 0f, fwd.X);
        return Vector3.Normalize(fwd * dir.Y + right * dir.X);
    }

    /// <summary>Stamina-scaled top speed: 100% at full, 35% when exhausted.</summary>
    private float SpeedNow => RunSpeed * (0.35f + 0.65f * _stamina);

    /// <summary>Stamina-scaled jump launch speed: 100% at full, 40% when exhausted.</summary>
    private float JumpNow => JumpSpeed * (0.40f + 0.60f * _stamina);

    /// <summary>Free movement: run on ground/platforms, steer in the air, land on supports.</summary>
    private void TickMove(float dt)
    {
        var move = MoveDir();
        _moving = move.LengthSquared() > 0f;

        if (_grounded)
        {
            if (_moving)
            {
                _yaw = TurnToward(_yaw, MathF.Atan2(-move.X, -move.Z), 16f * dt);
                _vel = move * SpeedNow;
            }
            else _vel = Vector3.Zero;
            _pos += _vel * dt;

            // Bump a trunk while heading at it → grab and climb.
            if (_moving) TryGroundGrab(move);

            if (_jumpQueued && _cling < 0)
            {
                _vy = JumpNow;
                _grounded = false;
                _standingPerch = -1;
                _stamina = MathF.Max(0f, _stamina - JumpCost);
            }
        }
        else
        {
            // Air control: steer toward the next crown, but keep launch momentum.
            if (_moving)
                _vel += move * AirAccel * dt;
            var flat = new Vector3(_vel.X, 0f, _vel.Z);
            float speed = flat.Length();
            if (speed > SpeedNow * 1.1f) _vel = flat / speed * SpeedNow * 1.1f;
            _pos += _vel * dt;

            _vy -= Gravity * dt;
            _pos.Y += _vy * dt;
            TryAirGrab();
        }

        // Clamp to the grove.
        float lim = Terrain.HalfSize - 0.8f;
        _pos.X = Math.Clamp(_pos.X, -lim, lim);
        _pos.Z = Math.Clamp(_pos.Z, -lim, lim);

        if (_cling >= 0) return; // a grab this frame pinned us to the trunk

        float support = SupportHeight(_pos.X, _pos.Y, _pos.Z, out int supportPerch);
        if (_grounded)
        {
            if (_pos.Y > support + 0.15f)
            {
                _grounded = false;      // walked off a platform edge
                _vy = 0f;
            }
            else
            {
                _pos.Y = support;
                _standingPerch = supportPerch;
            }
        }
        else if (_vy <= 0f && _pos.Y <= support) // only land while falling — a rising
        {                                        // jump is still climbing away
            _pos.Y = support;
            _vy = 0f;
            _grounded = true;
            if (supportPerch >= 0 && supportPerch != _standingPerch)
                _treeHops++;
            _standingPerch = supportPerch;
            _vel = Vector3.Zero;
        }
    }

    /// <summary>Highest standable surface at a column: terrain or a crown platform.</summary>
    private float SupportHeight(float x, float y, float z, out int perchIndex)
    {
        float top = Terrain.Height(x, z);
        int best = -1;
        for (int i = 0; i < _perches.Count; i++)
        {
            var p = _perches[i];
            float dx = x - p.X, dz = z - p.Z;
            if (dx * dx + dz * dz <= p.Radius * p.Radius &&
                y >= p.Top - 0.35f && p.Top > top)
            {
                top = p.Top;
                best = i;
            }
        }
        perchIndex = best;
        return top;
    }

    /// <summary>Grounded cat runs into a trunk while heading at it → start clinging.</summary>
    private void TryGroundGrab(Vector3 move)
    {
        for (int i = 0; i < _trees.Count; i++)
        {
            var t = _trees[i];
            if (_pos.Y > t.TrunkTop - 0.2f) continue; // above the cling band (on a top crown)
            float dx = t.X - _pos.X, dz = t.Z - _pos.Z;
            float distSq = dx * dx + dz * dz;
            if (distSq > BumpRadius * BumpRadius) continue;
            float dist = MathF.Sqrt(distSq);
            if (dist < 1e-4f || (dx * move.X + dz * move.Z) / dist < 0.35f) continue; // grazing, not charging
            Grab(i);
            return;
        }
    }

    /// <summary>Airborne cat near a trunk catches it — forgiving platform-hop misses.</summary>
    private void TryAirGrab()
    {
        if (_vy > 3f) return; // let a fresh leap clear the bark first
        for (int i = 0; i < _trees.Count; i++)
        {
            var t = _trees[i];
            float dx = t.X - _pos.X, dz = t.Z - _pos.Z;
            if (dx * dx + dz * dz > GrabRadius * GrabRadius) continue;
            float ground = Terrain.Height(t.X, t.Z);
            if (_pos.Y < ground + 0.15f || _pos.Y > t.TrunkTop) continue;
            Grab(i);
            return;
        }
    }

    /// <summary>Pins the cat to a trunk's bark at its current height.</summary>
    private void Grab(int index)
    {
        _cling = index;
        _grounded = false;
        _vy = 0f;
        _vel = Vector3.Zero;
        _standingPerch = -1;
        var t = _trees[index];
        var outDir = new Vector3(_pos.X - t.X, 0f, _pos.Z - t.Z);
        if (outDir.LengthSquared() > 1e-5f) outDir = Vector3.Normalize(outDir);
        else outDir = Vector3.UnitX;
        _pos.X = t.X + outDir.X * 0.30f;
        _pos.Z = t.Z + outDir.Z * 0.30f;
        _pos.Y = Math.Clamp(_pos.Y, Terrain.Height(t.X, t.Z) + 0.15f, t.TrunkTop);
        _yaw = MathF.Atan2(_pos.X - t.X, _pos.Z - t.Z); // face the trunk (forward −Z)
    }

    /// <summary>On the trunk: W/S slides up/down, A/D orbits the bark, Space leaps off.
    /// Cling top pops the cat onto that tree's highest platform.</summary>
    private void TickCling(float dt)
    {
        int index = _cling;
        var t = _trees[index];
        _moving = _moveInput.LengthSquared() > 0f;

        // Orbit the trunk with A/D.
        float angle = MathF.Atan2(_pos.X - t.X, _pos.Z - t.Z) + _moveInput.X * 1.9f * dt;
        _pos.X = t.X + MathF.Sin(angle) * 0.30f;
        _pos.Z = t.Z + MathF.Cos(angle) * 0.30f;
        _yaw = angle;

        float ground = Terrain.Height(t.X, t.Z);
        _pos.Y = Math.Clamp(_pos.Y + _moveInput.Y * ClimbSpeed * dt, ground + 0.15f, t.TrunkTop);

        // Bottom + pressing down = hop off; top = pop onto the highest platform.
        if (_pos.Y <= ground + 0.16f && _moveInput.Y < 0f)
        {
            _cling = -1;
            _grounded = true;
            _standingPerch = -1;
            return;
        }
        if (_pos.Y >= t.TrunkTop - 0.01f)
        {
            _cling = -1;
            _grounded = true;
            // Highest platform on this tree — the crown the trunk dies into.
            int top = -1;
            for (int i = 0; i < _perches.Count; i++)
                if (_perches[i].TreeIndex == index &&
                    (top < 0 || _perches[i].Top > _perches[top].Top))
                    top = i;
            var p = top >= 0 ? _perches[top] : null;
            _pos = new Vector3(t.X, p?.Top ?? t.TrunkTop, t.Z);
            _standingPerch = top;
            return;
        }

        if (_jumpQueued)
        {
            _cling = -1;
            _grounded = false;
            var outDir = new Vector3(_pos.X - t.X, 0f, _pos.Z - t.Z);
            _vel = outDir * 2.4f;
            _vy = 4.4f;
            _pos += outDir * 0.08f;
            _stamina = MathF.Max(0f, _stamina - JumpCost * 0.8f);
        }
    }

    // ------------------------------------------------------------------ berries

    /// <summary>Spawns a berry: ~half on a random crown platform, half on the grass.</summary>
    private void SpawnBerry()
    {
        Vector3 pos;
        if (_perches.Count > 0 && _rand.NextDouble() < 0.5)
        {
            var p = _perches[_rand.Next(_perches.Count)];
            float a = (float)_rand.NextDouble() * MathF.Tau;
            float r = (float)_rand.NextDouble() * p.Radius * 0.6f;
            pos = new Vector3(p.X + MathF.Cos(a) * r, p.Top + 0.06f, p.Z + MathF.Sin(a) * r);
        }
        else
        {
            float x = ((float)_rand.NextDouble() * 2f - 1f) * (Terrain.HalfSize - 1.5f);
            float z = ((float)_rand.NextDouble() * 2f - 1f) * (Terrain.HalfSize - 1.5f);
            pos = new Vector3(x, Terrain.Height(x, z) + 0.06f, z);
        }
        var berry = new Berry
        {
            Instance = new PbrInstance { Mesh = _berryMesh },
            Pos = pos,
            Spin = (float)_rand.NextDouble() * MathF.Tau,
            Phase = (float)_rand.NextDouble() * MathF.Tau,
        };
        berry.Instance.Model = Matrix4x4.CreateTranslation(pos);
        _berries.Add(berry);
        _scene.Instances.Add(berry.Instance);
    }

    /// <summary>Berry respawns, idle spin/bob, and eating on touch (+35% stamina).</summary>
    private void TickBerries(float dt)
    {
        _berryTimer -= dt;
        if (_berryTimer <= 0f && _berries.Count < MaxBerries)
        {
            _berryTimer = 2.5f + (float)_rand.NextDouble() * 3f;
            SpawnBerry();
        }

        for (int i = _berries.Count - 1; i >= 0; i--)
        {
            var b = _berries[i];
            if (Vector3.DistanceSquared(b.Pos, _pos) < EatRadius * EatRadius)
            {
                _scene.Instances.Remove(b.Instance);
                _berries.RemoveAt(i);
                _stamina = MathF.Min(1f, _stamina + EatGain);
                _eatTimer = 0.9f;
                continue;
            }
            b.Spin += dt * 2.5f;
            float bob = MathF.Sin(_elapsed * 3f + b.Phase) * 0.03f;
            b.Instance.Model = Matrix4x4.CreateRotationY(b.Spin)
                * Matrix4x4.CreateTranslation(b.Pos + new Vector3(0f, bob, 0f));
        }
    }

    // ------------------------------------------------------------------ camera

    private void UpdateCamera(float dt)
    {
        var target = _pos + new Vector3(0f, 0.4f, 0f);
        var eye = target + new Vector3(
            _camDistance * MathF.Cos(_camPitch) * -MathF.Sin(_camYaw),
            _camDistance * MathF.Sin(_camPitch),
            _camDistance * MathF.Cos(_camPitch) * -MathF.Cos(_camYaw));

        float minY = Terrain.Height(eye.X, eye.Z) + 0.4f;
        if (eye.Y < minY) eye.Y = minY;

        float k = 1f - MathF.Exp(-14f * dt);
        _camEye = _camInitialized ? Vector3.Lerp(_camEye, eye, k) : eye;
        _camInitialized = true;

        _view = PbrMath.LookAt(_camEye, target, Vector3.UnitY);
        _proj = PbrMath.Perspective(MathF.PI / 3.2f, _width / (float)_height, 0.08f, 160f);
        _scene.Camera = new PbrCamera { View = _view, Projection = _proj, Position = _camEye };
    }

    // ------------------------------------------------------------------ render + HUD

    /// <summary>Record one frame: pushes timing into the scene and submits.</summary>
    public void RenderFrame()
    {
        _scene.DeltaSeconds = 1f / 60f;
        _scene.ElapsedSeconds = _elapsed;
        _pbr.RenderFrame(_scene);
    }

    /// <summary>Short status line for the HUD header.</summary>
    public string HudStatus()
    {
        int filled = (int)MathF.Round(_stamina * 10f);
        var bar = new string('#', filled).PadRight(10, '-');
        var s = $"小猫 · 体力 [{bar}] {(int)(_stamina * 100f)}% · 跳了 {_treeHops} 次";
        if (_cling >= 0) s += " · 爬树中";
        else if (_standingPerch >= 0) s += " · 在树顶";
        return s;
    }

    /// <summary>Resize plumbing shared by windowed and headless paths.</summary>
    public void Resize(uint width, uint height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr.Resize(_width, _height);
    }

    public void Dispose()
    {
        _cat?.Dispose();
        _catRig?.Dispose();
        _pbr.Dispose();
    }

    // ------------------------------------------------------------------ QA hooks

    /// <summary>Moves the cat to a world XZ point — QA hook behind --teleport.</summary>
    public void DebugTeleport(float x, float z)
    {
        _cling = -1;
        _pos.X = x;
        _pos.Z = z;
        _pos.Y = SupportHeight(x, Terrain.Height(x, z) + 1f, z, out _);
        _grounded = true;
        _vy = 0f;
        _vel = Vector3.Zero;
    }

    /// <summary>Teleports the cat beside the nearest trunk — QA hook.</summary>
    public void DebugTeleportToTree()
    {
        if (_trees.Count == 0) return;
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < _trees.Count; i++)
        {
            float d = (_trees[i].X - _pos.X) * (_trees[i].X - _pos.X)
                + (_trees[i].Z - _pos.Z) * (_trees[i].Z - _pos.Z);
            if (d < bestD) { bestD = d; best = i; }
        }
        DebugTeleport(_trees[best].X + 0.45f, _trees[best].Z);
    }

    /// <summary>JSON snapshot for QA: cat position, cling/hop/stamina state.</summary>
    public string DebugCatState() =>
        $"{{\"x\":{_pos.X:F2},\"y\":{_pos.Y:F2},\"z\":{_pos.Z:F2}," +
        $"\"cling\":{(_cling >= 0).ToString().ToLowerInvariant()}," +
        $"\"perch\":{_standingPerch},\"hops\":{_treeHops},\"stamina\":{_stamina:F2}," +
        $"\"berries\":{_berries.Count}}}";

    /// <summary>Cat position line — parity with the V1 dump flag.</summary>
    public string DebugAnimalPositions() => $"0 小猫 (Cat) @ ({_pos.X:F1},{_pos.Y:F1},{_pos.Z:F1})";

    /// <summary>Cat skinned palette sample — parity with the V1 dump flag.</summary>
    public string DebugRigPalettes()
    {
        if (_cat is null) return "";
        var part = System.Linq.Enumerable.FirstOrDefault(_cat.Parts, p => p.SkinIndex >= 0);
        return part is null ? "" : $"0 Cat joints={part.JointCount} offset={part.JointOffset} scale={_cat.Scale:F3}";
    }

    // ------------------------------------------------------------------ math

    private static float AngleDelta(float from, float to)
    {
        float d = (to - from) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    private static float TurnToward(float yaw, float target, float maxStep) =>
        yaw + Math.Clamp(AngleDelta(yaw, target), -maxStep, maxStep);
}
