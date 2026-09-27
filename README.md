# 小猫跳树 Kitty Hop — kitty-lite 分支

V1 完整动物园版在 `main` / `v1` tag。这个分支是给 10 岁小朋友的轻量化版本：
一片小树丛 + 一只会爬树的猫，没有其它动物、拾取投掷、ECS 和 AI。

- 地图从 160×160 m 缩到 ~46×46 m
- 只有 `cat.glb` 一个模型资产；树是程序化盒子网格（两种网格、~100 实例）
- 关掉 SSAO / 接触阴影，阴影贴图 1024，雾距更近
- 撞树干自动抱住；空中贴近树干自动抓住——树冠间跳跃很宽容

## 玩法

| 输入 | 行为 |
| --- | --- |
| `WASD` / 方向键 | 跑动（相对镜头方向） |
| 撞到树干 | 自动抱住开始爬 |
| 树上 `W`/`S` | 上下爬；`A`/`D` 绕树干转 |
| `Space` | 跳跃；树上向外跳；爬到顶自动站上树冠 |
| 空中贴树干 | 自动抓住（跳偏了也能接住） |
| 按住左键拖动 | 环绕镜头 |
| 滚轮 | 缩放 |
| `Esc` | 退出（桌面版） |

HUD 显示跳到不同树冠的次数——玩法就是爬上去、从这棵树跳到那棵树。

## 仓库布局

| 目录 | 说明 |
| --- | --- |
| `ParadiseEngine/` | 引擎 git 子模块（pin 在 `b646e51`） |
| `SafariPark.Core/` | 共享模拟层：`KittyGame.cs`（全部玩法）、`GltfRig`（GLB 蒙皮装配）、程序化地形/树 |
| `SafariPark/` | 桌面宿主：SDL 窗口 / headless、ImGui HUD |
| `SafariPark.Web/` | 浏览器宿主：WebAssembly + WebGPU canvas、DOM HUD、JS 输入桥 |
| `tools/` | `ConvertGlb`、`RigCheck`、`RigView` 资产工具 |

## 构建

```bash
git clone --recurse-submodules <repo>
cd SafariPark && dotnet run                     # 窗口模式
dotnet run -- --headless 60 --screenshot out.png # 离屏渲染 + 截图
```

需要 .NET SDK 10.0.400+（`global.json`）。
