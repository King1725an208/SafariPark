# 小猫跳树 Kitty Hop — 桌面宿主

轻量化版（kitty-lite 分支）：一片小树丛 + 一只猫，给 10 岁小朋友玩。
完整动物园版见 `main` / `v1` tag。

## 操作

| 输入 | 行为 |
| --- | --- |
| `WASD` / 方向键 | 跑动（相对镜头方向） |
| 撞到树干 | 自动抱住开始爬 |
| 树上 `W`/`S` | 上下爬；`A`/`D` 绕树干转 |
| `Space` | 跳跃；树上向外跳；爬到顶自动站上树冠 |
| 空中贴树干 | 自动抓住 |
| 按住左键拖动 | 环绕镜头；滚轮缩放 |
| `Esc` | 退出 |

## 构建与运行

需要 .NET SDK 10.0.400+ 和一块支持 WebGPU 的显卡。引擎以 `../ParadiseEngine` 的项目引用方式引入。

```bash
dotnet run --project SafariPark.csproj            # 窗口模式
dotnet run -- --headless 60 --screenshot out.png  # 离屏渲染 + 截图
dotnet run -- --size 1920x1080                    # 窗口尺寸
dotnet run -- --features +rendering.ssao          # 特性开关
dotnet run -- --teleport 3,4 --headless 60        # 传送到世界坐标
```

`--headless` 路径同样渲染 HUD（ImGui overlay 合成在帧尾），适合 CI 冒烟。

## 结构

- `Program.cs` — SDL 窗口 / headless、事件分发、ImGui overlay
- `HudPanel.cs` — 状态行 + 操作提示（无动物名册，只有一只猫）
