using System.Diagnostics;
using System.Numerics;

using Microsoft.Extensions.Logging;

using Paradise.Diagnostics;
using Paradise.Features;
using Paradise.Rendering.Pbr;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using Paradise.Ui.ImGui;
using Paradise.Windowing;
using Paradise.Windowing.Sdl;

using Zio.FileSystems;

using SafariPark;

namespace SafariPark;

/// <summary>Host entry point: SDL-windowed interactive run, or
/// <c>--headless N --screenshot out.png</c> for an offscreen smoke render.</summary>
internal static class Program
{
    private static uint _width = 1280;
    private static uint _height = 760;
    private static ILoggerFactory s_log = ParadiseConsole.CreateFactory(new ParadiseConsoleOptions());
    private static Vector2? s_teleport;
    private static bool s_dumpAnimals;

    private static int Main(string[] args)
    {
        if (ParseSize(args) is { } size) (_width, _height) = size;
        var headless = ParseInt(args, "--headless");
        var screenshot = ParseValue(args, "--screenshot");
        s_dumpAnimals = args.Contains("--dump-animals");
        SafariPark.KittyGame.NoSkinning = args.Contains("--no-skinning");
        if (ParseValue(args, "--teleport") is { } tp &&
            tp.Split(',') is [var tx, var tz] &&
            float.TryParse(tx, out var fx) && float.TryParse(tz, out var fz))
        {
            s_teleport = new Vector2(fx, fz);
        }

        var features = BuildFeatures(args);
        if (features is null) return 1;

        try
        {
            return headless is { } frames
                ? RunHeadless(frames, screenshot, features)
                : RunWindowed(features);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SafariPark failed: {ex}");
            return 1;
        }
    }

    private static int RunWindowed(FeatureSwitches features)
    {
        using var platform = new SdlWindowPlatform();
        using var window = platform.CreateWindow(new WindowOptions("小猫跳树  Kitty Hop", _width, _height));
        using var renderer = new WebGpuRenderer(window.CreateSurface(), logger: s_log.CreateLogger("WebGPU"));
        using var game = new KittyGame(renderer, features, window.Width, window.Height, s_log.CreateLogger("PbrRenderer"));

        // CJK font for the Chinese HUD; falls back to ImGui's ASCII default.
        var host = new PhysicalFileSystem();
        var fonts = UiFonts.MountSystemFonts(host);
        var core = new ImGuiUiCore(window.Width, window.Height, UiFonts.FindCjkFont(fonts, 16f));
        core.DisableIniFile();
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        var hud = new HudPanel(game);
        core.AddDraw(hud.Draw);

        window.Resized += (w, h) => { renderer.Resize(w, h); game.Resize(w, h); };

        var pending = new List<ImGuiTextureOp>();
        var clock = Stopwatch.StartNew();
        double previous = clock.Elapsed.TotalSeconds;
        Vector2? lastPointer = null;

        while (!window.CloseRequested)
        {
            platform.Pump();
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(now - previous, 0.1);
            previous = now;

            while (window.TryReadEvent(out var input))
            {
                var ev = input.Event;
                if (ev.Kind == WindowEventKind.Button && ev.Source == EventSource.Keyboard &&
                    ev.KeyboardKey == KeyboardKey.Escape && ev.Pressed)
                {
                    window.RequestClose();
                    continue;
                }

                bool consumedByUi = core.Input.Handle(ev);
                if (!consumedByUi)
                {
                    game.OnEvent(ev);
                    if (ev.Kind == WindowEventKind.PointerMove)
                    {
                        if (lastPointer is { } lp) game.OnDrag(ev.X - lp.X, ev.Y - lp.Y);
                        lastPointer = new Vector2(ev.X, ev.Y);
                    }
                }
                else if (ev.Kind == WindowEventKind.PointerMove)
                {
                    lastPointer = new Vector2(ev.X, ev.Y);
                }
            }

            core.Input.Tick(now);
            game.Update(dt);

            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, window.Width, window.Height, snapshot);
            };
            game.RenderFrame();
        }

        fonts.Dispose();
        host.Dispose();
        core.Dispose();
        return 0;
    }

    private static int RunHeadless(int frames, string? screenshot, FeatureSwitches features)
    {
        // SdlWindowPlatform initializes SDL video; no window is created in headless mode.
        using var platform = new SdlWindowPlatform();
        using var renderer = WebGpuRenderer.CreateHeadless(_width, _height, s_log.CreateLogger("WebGPU"));
        using var game = new KittyGame(renderer, features, _width, _height, s_log.CreateLogger("PbrRenderer"));
        if (s_teleport is { } tp) game.DebugTeleport(tp.X, tp.Y);

        var host = new PhysicalFileSystem();
        var fonts = UiFonts.MountSystemFonts(host);
        var hudHeadless = new HudPanel(game);
        var core = new ImGuiUiCore(_width, _height, UiFonts.FindCjkFont(fonts, 16f));
        core.DisableIniFile();
        using var overlay = new ImGuiWebGpuRenderer(renderer.NativeDevice, renderer.NativeColorFormat);
        core.AddDraw(hudHeadless.Draw);
        var pending = new List<ImGuiTextureOp>();

        const float dt = 1f / 60f;
        for (int i = 0; i < frames; i++)
        {
            core.Input.Tick(i * dt);
            game.Update(dt);
            var snapshot = core.AcquireSnapshotForRender(pending, out _);
            renderer.OverlayPass = (encoder, view) =>
            {
                overlay.ApplyTextureOps(pending);
                if (snapshot is not null) overlay.Render(encoder, view, _width, _height, snapshot);
            };
            game.RenderFrame();
        }

        if (screenshot is not null)
        {
            var pixels = renderer.ReadbackColor(out var w, out var h);
            using var file = File.Create(screenshot);
            PngWriter.Write(file, new ColorReadback(pixels, w, h), renderer.ColorFormat);
            Console.WriteLine($"Screenshot written to {screenshot} ({w}x{h}).");
        }
        Console.WriteLine($"state: {game.DebugCatState()}");
        if (s_dumpAnimals)
        {
            Console.WriteLine(game.DebugAnimalPositions());
            Console.WriteLine(game.DebugRigPalettes());
        }
        Console.WriteLine($"Headless mode: rendered {frames} frames.");

        fonts.Dispose();
        host.Dispose();
        core.Dispose();
        return 0;
    }

    // ------------------------------------------------------------------ args

    private static FeatureSwitches? BuildFeatures(string[] args)
    {
        var configuration = EngineConfiguration.Empty;
        try
        {
            if (ParseValue(args, "--config") is { } path)
            {
                using var file = File.OpenRead(path);
                configuration = TomlEngineConfiguration.Read(file);
            }
            configuration = configuration.Merge(new EngineConfiguration
            {
                Features = FeatureOverrides.FromEnvironment(),
            }).Merge(new EngineConfiguration
            {
                Features = FeatureOverrides.Parse(ParseValue(args, "--features")),
            });
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Feature configuration: {error.Message}");
            return null;
        }

        var switches = new FeatureSwitches(configuration);
        PbrFeatures.DeclareAll(switches);
        foreach (var name in switches.Unknown)
            Console.Error.WriteLine($"Feature configuration: no feature named '{name}' in this build.");
        return switches;
    }

    private static string? ParseValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? ParseInt(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0) return null;
        // --control is a roster index, so 0 is valid.
        if (flag == "--control" && index + 1 < args.Length && int.TryParse(args[index + 1], out var zero))
            return Math.Max(0, zero);
        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var n) && n > 0)
            return n;
        Console.Error.WriteLine($"Usage: {flag} <positive integer>");
        Environment.Exit(1);
        return null;
    }

    private static (uint W, uint H)? ParseSize(string[] args)
    {
        if (ParseValue(args, "--size") is { } s && s.Split('x') is [var sw, var sh]
            && uint.TryParse(sw, out var w) && uint.TryParse(sh, out var h) && w > 0 && h > 0)
            return (w, h);
        return null;
    }
}
