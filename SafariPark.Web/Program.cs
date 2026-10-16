using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using Paradise.Assets.Packing;
using Paradise.Diagnostics;
using Paradise.Features;
using Paradise.Rendering.Browser;
using Paradise.Rendering.Pbr;
using Paradise.Windowing;

using SafariPark;

using Zio;

namespace SafariPark.Web;

/// <summary>Browser host: drives <see cref="SafariGame"/> from requestAnimationFrame and DOM
/// input events, and mirrors the HUD into the page. All gameplay lives in the page's JS bridge;
/// this class only translates between DOM and engine vocabulary.</summary>
[SupportedOSPlatform("browser")]
public static partial class Program
{
    private const string HostModule = "safari-host";

    /// <summary>Frames rendered without a GPU error before the page reports SAFARI-OK —
    /// long enough for WebGPU async validation and the interpreter's tier-up to surface bugs.</summary>
    private const int FramesForSuccess = 300;

    private static BrowserRenderer? s_renderer;
    private static SafariGame? s_game;
    private static int s_frames;
    private static bool s_reported;
    private static bool s_failed;
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static double s_windowStartMs;
    private static int s_framesInWindow;
    private static double s_lastFps = -1;
    private static double s_updateMs;
    private static double s_renderMs;

    /// <summary>GLBs fetched by main.js before <see cref="InitAsync"/> — the simulation's
    /// readAsset callback is synchronous, so the browser pre-buffers every asset.</summary>
    private static readonly Dictionary<string, byte[]> s_assets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers one fetched asset ("cat.glb") for the game's readAsset callback.</summary>
    [JSExport]
    internal static void ProvideAsset(string name, byte[] bytes) => s_assets[name] = bytes;

    private static AssetPackFileSystem? s_pack;

    /// <summary>Mounts the compressed download before startup; bytes are retained by the mount.</summary>
    [JSExport]
    internal static void ProvideAssetPack(byte[] bytes)
    {
        if (s_game is not null) throw new InvalidOperationException("Provide the pack before InitAsync.");
        var next = new AssetPackFileSystem(bytes);
        s_pack?.Dispose();
        s_pack = next;
        s_assets.Clear();
    }

    /// <summary>Never invoked — the WebAssembly SDK requires an entry point, but the app is
    /// driven through its [JSExport] surface.</summary>
    public static void Main()
    {
    }

    /// <summary>Create the renderer and the park. Called once from main.js after the runtime
    /// is up; rejects (and writes SAFARI-FAIL) when WebGPU is unavailable or setup throws.</summary>
    /// <param name="hostModuleUrl">Absolute URL of safari-host.js — JSHost.ImportAsync resolves
    /// dynamic imports inside _framework/, so the page must pass an absolute URL.</param>
    /// <param name="width">Canvas width in device pixels.</param>
    /// <param name="height">Canvas height in device pixels.</param>
    /// <param name="logLevel"><c>?log=</c> value; browser wasm has no environment to read.</param>
    [JSExport]
    internal static async Task InitAsync(string hostModuleUrl, int width, int height, string logLevel)
    {
        try
        {
            await JSHost.ImportAsync(HostModule, hostModuleUrl).ConfigureAwait(false);

            s_renderer = await BrowserRenderer.CreateAsync("#gpu-canvas", (uint)width, (uint)height)
                .ConfigureAwait(false);
            Console.WriteLine($"[safari] adapter: {s_renderer.AdapterInfo}");

            var switches = new FeatureSwitches();
            PbrFeatures.DeclareAll(switches);
            s_game = new SafariGame(s_renderer, switches, (uint)width, (uint)height,
                ParadiseConsole.CreateLogger("PbrRenderer",
                    new ParadiseConsoleOptions { MinLevel = ParseLevel(logLevel) }),
                readAsset: name => s_pack is not null ? s_pack.ReadAllBytes("/" + name) : s_assets.TryGetValue(name, out var b)
                    ? b
                    : throw new FileNotFoundException($"asset '{name}' was not pre-fetched by main.js"));

            PushRoster();
            s_game.ControlledChanged += _ => PushRoster();
            SetStatusJs($"running · adapter={s_renderer.AdapterInfo}");
            s_windowStartMs = s_clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>One animation frame, called by the page's rAF pump with the rAF timestamp.</summary>
    [JSExport]
    internal static void OnAnimationFrame(double timestampMs)
    {
        if (s_failed || s_renderer is null || s_game is null) return;
        try
        {
            var startMs = s_clock.Elapsed.TotalMilliseconds;
            s_game.Update(1f / 60f);
            var updateMs = s_clock.Elapsed.TotalMilliseconds;
            s_game.RenderFrame();
            var renderMs = s_clock.Elapsed.TotalMilliseconds;
            s_updateMs += updateMs - startMs;
            s_renderMs += renderMs - updateMs;
            s_frames++;
            s_framesInWindow++;
            // Async WebGPU validation turns silent frame drops into a visible failure.
            var error = s_renderer.TakeGpuError();
            if (error.Length > 0) throw new InvalidOperationException($"WebGPU error: {error}");

            var windowMs = s_clock.Elapsed.TotalMilliseconds - s_windowStartMs;
            if (windowMs >= 1000.0)
            {
                s_lastFps = s_framesInWindow * 1000.0 / windowMs;
                s_windowStartMs = s_clock.Elapsed.TotalMilliseconds;
                s_framesInWindow = 0;
                SetStatsJs($"frame {s_frames} · {s_lastFps:F1} fps");
                SetStatusJs(s_game.HudStatus());
            }

            if (!s_reported && s_frames >= FramesForSuccess)
            {
                s_reported = true;
                SetStatusJs($"SAFARI-OK frames={s_frames} fps={s_lastFps:F1} adapter={s_renderer.AdapterInfo}");
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>Keyboard transition. <paramref name="code"/> is a DOM KeyboardEvent.code
    /// ("KeyW", "ShiftLeft", …) — semantic names survive layout differences.</param>
    [JSExport]
    internal static void OnKey(string code, bool pressed)
    {
        if (s_game is null) return;
        if (MapKey(code) is { } key)
            s_game.OnEvent(WindowEvent.Keyboard(key, pressed));
    }

    [JSExport]
    internal static void OnPointerDown(float x, float y, int button)
    {
        if (s_game is null) return;
        if (MapButton(button) is { } b)
            s_game.OnEvent(WindowEvent.Mouse(b, pressed: true, x, y));
    }

    [JSExport]
    internal static void OnPointerUp(float x, float y, int button)
    {
        if (s_game is null) return;
        if (MapButton(button) is { } b)
            s_game.OnEvent(WindowEvent.Mouse(b, pressed: false, x, y));
    }

    /// <summary>Pointer drag delta in CSS pixels — DOM movementX/movementY.</summary>
    [JSExport]
    internal static void OnPointerDrag(float dx, float dy) => s_game?.OnDrag(dx, dy);

    /// <summary>Wheel input; dy is DOM deltaY (positive = zoom out).</summary>
    [JSExport]
    internal static void OnScroll(float x, float y, float dy) =>
        s_game?.OnEvent(WindowEvent.Scroll(0f, -dy * 0.01f));

    /// <summary>Canvas size change in device pixels (DPR already applied page-side).</summary>
    [JSExport]
    internal static void OnResize(int width, int height)
    {
        s_renderer?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
        s_game?.Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    /// <summary>Roster click — index into <see cref="SafariGame.Roster"/>.</summary>
    [JSExport]
    internal static void OnSelectAnimal(int index) => s_game?.ControlIndex(index);

    [JSExport]
    internal static void OnCycleAnimal(int delta) => s_game?.Switch(delta);

    // ------------------------------------------------------------------ test hooks

    /// <summary>Spawns a wanderer of the given species at a treeline point — browser QA.</summary>
    [JSExport]
    internal static void DebugSpawnWanderer(int species) =>
        s_game?.SpawnWanderer((Species)species);

    /// <summary>Drops a throwable at the player's feet — browser QA.</summary>
    [JSExport]
    internal static void DebugSpawnItem(int type) =>
        s_game?.DebugSpawnItemAtPlayer(type);

    /// <summary>Moves the controlled animal near a world point — QA shortcut.</summary>
    [JSExport]
    internal static void DebugTeleport(float x, float z) => s_game?.DebugTeleport(x, z);

    /// <summary>Spawns a passer-by a few meters ahead of the player — browser QA.</summary>
    [JSExport]
    internal static void DebugSpawnNear(int species) =>
        s_game?.DebugSpawnNear((Species)species);

    /// <summary>Teleports the cat beside the nearest climbable trunk — browser QA.</summary>
    [JSExport]
    internal static void DebugTeleportToTree() => s_game?.DebugTeleportToTree();

    /// <summary>Spawns a passer-by ahead of the camera view — browser QA.</summary>
    [JSExport]
    internal static void DebugSpawnAhead(int species) =>
        s_game?.DebugSpawnAhead((Species)species);

    /// <summary>Frame CPU split (sim vs renderer), averaged since last call — browser QA.</summary>
    [JSExport]
    internal static string DebugFrameStats()
    {
        if (s_framesInWindow <= 0) return "{}";
        var u = s_updateMs / s_framesInWindow;
        var r = s_renderMs / s_framesInWindow;
        s_updateMs = 0; s_renderMs = 0; s_framesInWindow = 0;
        return $"{{\"updateMs\":{u:F2},\"renderMs\":{r:F2}}}";
    }
    /// <summary>JSON snapshot of the controlled animal's physics state — browser QA.</summary>
    [JSExport]
    internal static string DebugCatState() => s_game?.DebugCatState() ?? "{}";

    // ------------------------------------------------------------------ HUD bridge

    /// <summary>Posts the roster to the DOM list. Called once at init and on every control
    /// change — the roster itself is static, only the selection marker moves.</summary>
    private static void PushRoster()
    {
        if (s_game is null) return;
        var roster = s_game.Roster;
        var flat = new string[roster.Count * 2];
        for (int i = 0; i < roster.Count; i++)
        {
            flat[i * 2] = roster[i].Label;
            flat[i * 2 + 1] = roster[i].Species;
        }
        SetRosterJs(flat, s_game.ControlledIndex);
    }

    private static KeyboardKey? MapKey(string code) => code switch
    {
        "KeyW" => KeyboardKey.W, "KeyA" => KeyboardKey.A, "KeyS" => KeyboardKey.S,
        "KeyD" => KeyboardKey.D, "KeyQ" => KeyboardKey.Q, "KeyE" => KeyboardKey.E,
        "KeyR" => KeyboardKey.R, "KeyF" => KeyboardKey.F,
        "ArrowUp" => KeyboardKey.Up, "ArrowDown" => KeyboardKey.Down,
        "ArrowLeft" => KeyboardKey.Left, "ArrowRight" => KeyboardKey.Right,
        "Space" => KeyboardKey.Space, "Tab" => KeyboardKey.Tab,
        "ShiftLeft" => KeyboardKey.LeftShift, "ShiftRight" => KeyboardKey.RightShift,
        "Escape" => KeyboardKey.Escape,
        _ => null,
    };

    private static PointerButton? MapButton(int button) => button switch
    {
        0 => PointerButton.Left,
        1 => PointerButton.Middle,
        2 => PointerButton.Right,
        _ => null,
    };

    private static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;

    private static void Fail(Exception ex)
    {
        s_failed = true;
        Console.Error.WriteLine(ex);
        try
        {
            SetStatusJs($"SAFARI-FAIL: {ex.GetType().Name}: {ex.Message}");
        }
        catch (JSException)
        {
            // Host module never loaded; main.js reports the rejection itself.
        }
    }

    [JSImport("setStatus", HostModule)]
    private static partial void SetStatusJs(string text);

    [JSImport("setStats", HostModule)]
    private static partial void SetStatsJs(string text);

    [JSImport("setRoster", HostModule)]
    private static partial void SetRosterJs(string[] labelsAndSpecies, int selected);
}
