# 排错指南 — DeadCellsLocalMultiplayerMod

<div align="center">

[English](TROUBLESHOOTING.md) • 中文

</div>

常见问题与排查方法。先打开日志——它能回答大部分问题：

```powershell
$env:DEADCELLSLOCALMULTIPLAYERMOD_LOG = "1"   # 然后启动游戏
```

## mod 不出现 / 不加载

- 游戏根目录的文件夹必须严格叫 `coremod`（单数、小写）。叫 `coremods` 会让启动器静默退出。
- `coremod/mods/DeadCellsLocalMultiplayerMod/` 里的 `modinfo.json` 必须直接在该文件夹内（不能在子文件夹里）。
- 用 `coremod/core/host/startup/DeadCellsModding.exe` 启动游戏，而不是 `deadcells.exe`。
- 查 DCCM 日志里提到 `DeadCellsLocalMultiplayerMod` 的加载错误。

## 构建失败

- **「The installed Dead Cells Core Modding MDK was not found」**——`DCCM_MDK_ROOT` 没设。跑 `build.cmd`（它会下载 MDK 并设置），或把 `DCCM_MDK_ROOT` 指向含 `build\build.props` 的文件夹。
- **`dotnet` 找不到 / SDK 版本不对**——装 .NET 10 SDK。
- 下载 MDK 时被限流——设 `GH_TOKEN`（GitHub PAT）重试，或在仓库根放一个本地 `dccm.zip`。

## 二号英雄不生成

- 会话必须激活：标题菜单 → **Local Co-op** → 选两个不同设备 → **Start local co-op**。
- 设备不能冲突：键盘只能用一次，两人不能共用一个手柄。菜单会强制这一点。
- 若开局后 ~300 帧 P1 仍无有效格，放置会被**有意跳过**。查日志里的 `Second hero spawned`。
- **从存档恢复的局**经 `AdoptExistingHeroes` 收养，而非 `initHero`；查日志里的 `adopted the second hero`。

## 二号英雄隐形或卡在 -1,-1

- 关卡流式加载时可能把它踢出；运行时每 0.5s 重新放置（`RepairSecondHero`）。稍等片刻。
- 若一直不回，查日志里的 `hero two was off level, placed back`。

## 一个手柄驱动两名英雄

- 每名英雄必须有自己 `mode`/`gc` 钉定的 `Controller`。mod 每帧重新钉定（`MirrorGating`），但确认菜单里两名玩家在**不同**槽位。
- 索引异常的手柄（Steam Remote Play）按连接顺序匹配，而非 `device.index`。

## 共享 UI（地图/背包/暂停）忽略手柄

- `MirrorGlobalDevice` 让全局控制器同时响应键盘与手柄。若仍忽略，查日志里的 `global controller mode ->` 行。

## 相机不跟随两名英雄

- 共享相机默认开；在菜单 `Camera` 页切换。日志行 `[SharedCamera] zoom ...` 显示当前缩放与间距。
- 相机关闭时视野只跟随 P1。

## 设置重启后不保留

- 设备/相机经 DCCM `Config<T>`（DCCM 配置目录里的 JSON）持久化。皮肤 id 在游戏皮肤数据加载完后解析（`TryResolvePendingSkin`）；若菜单仍显示「same as P1」，在标题屏加载好后进一次菜单即可。

## 进细胞门/子关卡崩溃

- 这是 `lastRoomId` 过期的 bug，已通过重定位时重置 `hero2.lastRoomId = -1` 修复。若仍见 `Null access .rType`，请贴出完整日志（设 `DEADCELLSLOCALMULTIPLAYERMOD_LOG=1`）与崩溃位置。

## 没有日志输出

- 日志默认关闭。在**启动游戏前**把 `DEADCELLSLOCALMULTIPLAYERMOD_LOG` 设为任意非 `0` 值（它在启动时只读一次）。
