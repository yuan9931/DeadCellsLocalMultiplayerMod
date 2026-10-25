<div align="center">

[English](CONTRIBUTING.md) • 中文

</div>
# 参与贡献 DeadCellsLocalMultiplayerMod

感谢你有兴趣改进 DeadCellsLocalMultiplayerMod。本文档说明如何构建项目，以及我们对贡献的期望。

## 前置条件

- **Windows**（项目目标为 `net10.0`，`SupportedOSPlatform` 为 Windows）。
- 与 **.NET 10** 兼容的 **.NET SDK**（见 `DeadCellsLocalMultiplayerMod.csproj` 里的 `TargetFramework`）。
- 本地测试需要安装 **DCCM（Dead Cells Core Modding API）** 的《死亡细胞》。
- **`DCCM_MDK_ROOT`** 环境变量，指向你的 DCCM MDK 文件夹（含 `build\build.props` 的那个）。`build.cmd` / `build.ps1` 会自动下载并设置好。

## 构建

仓库根目录最简单的方式：

```powershell
build.cmd
```

它会自动下载 DCCM MDK（`dead-cells-core-modding/core` 最新 release 的 `win-x64.zip`，或本地已有的 `dccm.zip`），设置 `DCCM_MDK_ROOT`，添加本地 NuGet 源，然后执行 `dotnet build -c Release`。

若 `DCCM_MDK_ROOT` 已设置，也可直接：

```powershell
dotnet build -c Release
```

产物在 `bin/Release/net10.0/win-x64/`，打包好的 mod 布局在 `output/DeadCellsLocalMultiplayerMod/`（由 DCCM MDK 目标配置）。

## 项目结构（概览）

- `ModEntry.cs` — mod 入口、标题菜单流程、会话激活、每帧转发。
- `Coop/LocalCoop*.cs` — 二号英雄运行时（一个 `partial` 类）：生成、每玩家输入、共享状态、断头、皮肤、钩子、诊断、持久化设置。
- `SharedCamera.cs` — 共享相机（跟随两名英雄并缩小视野）。
- `AGENTS.md` — 架构、构建、日志与游戏内验证说明（建议先读）。
- `PLAN.md` — 实现计划与所依据的核心 API。

## 如何贡献

1. **开 issue**，或在大改之前先讨论一个**小而聚焦**的改动。
2. **Fork** 仓库，并创建**只做一个功能或修复**的分支。
3. **保持 PR 聚焦**——不要夹带无关的格式化、重命名或顺手清理。
4. **匹配现有风格**——命名、模式、注释密度与周围代码一致。
5. 提交前 **Release 构建**；修掉与你改动相关的新编译器警告。
6. 在 PR 描述里**说清改了什么、为什么**（平实语言、完整句子）。

## 代码评审期望

- 改动应**最小**且**直接服务于既定目标**。
- 不要删除无关注释，或无故重写大段代码。
- 尽量**一条清晰代码路径**，而非许多特例分支。

## 测试

本仓库没有自动化测试。玩法改动请：

- 通过 DCCM 加载 mod 运行游戏。
- 验证**两名英雄**、每玩家输入隔离、**共享相机**、**关卡切换**（含细胞门与隐藏关），以及**任一英雄死亡结束本局**。

## 问题

DCCM 安装与 API 文档见官方：

- [DCCM 安装（Steam 创意工坊）](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/)
- [dead-cells-core-modding/core](https://github.com/dead-cells-core-modding/core)
