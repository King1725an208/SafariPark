using System.Numerics;

using Paradise.Ui.ImGui;

using Hexa.NET.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace SafariPark;

/// <summary>The desktop HUD panel: cat status and a kid-simple controls cheat-sheet.</summary>
/// <remarks>Drawn on the sim thread between ImGui NewFrame/Render; free to read and mutate
/// <see cref="KittyGame"/> directly.</remarks>
public sealed class HudPanel
{
    private readonly KittyGame _game;

    public HudPanel(KittyGame game) => _game = game;

    public void Draw()
    {
        ImGuiApi.SetNextWindowPos(new Vector2(16, 16), ImGuiCond.FirstUseEver);
        ImGuiApi.SetNextWindowSize(new Vector2(260, 0), ImGuiCond.FirstUseEver);
        ImGuiApi.Begin("小猫跳树  Kitty Hop", ImGuiWindowFlags.AlwaysAutoResize);

        ImGuiText.Colored(new Vector4(1f, 0.85f, 0.3f, 1f), _game.HudStatus());
        ImGuiApi.Separator();

        ImGuiText.Disabled("WASD/方向键 跑动 · 撞到树干就抱住");
        ImGuiText.Disabled("树上 W/S 上下爬 · A/D 绕树干转");
        ImGuiText.Disabled("Space 跳/跳下树 · 空中能抓住树干");
        ImGuiText.Disabled("按住左键拖转视角 · 滚轮缩放 · Esc 退出");
        ImGuiApi.End();
    }
}
