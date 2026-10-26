<div align="center">

[English](README.md) • 中文

</div>
# DeadCellsLocalMultiplayerMod

**DeadCellsLocalMultiplayerMod** 是一个《死亡细胞》（Dead Cells）的**本地同屏双人 mod**，基于 **DCCM（Dead Cells Core Modding API）** 制作。

它在一台电脑上添加**第二名英雄**，并给每位玩家**独立的输入设备**；两名英雄**共用一个相机窗口**（不分屏）。

---

## 🎮 功能

- ✅ 标题菜单新增 **Local Co-op（本地同屏）** 入口；菜单界面已本地化（含简体中文，跟随游戏语言显示）
- ✅ 每位玩家自选输入设备：键盘或手柄 1–4。手柄可以重复选——一个手柄挂两个摇杆位（两个 pad）是合法玩法（此时两名英雄都响应该设备；隔离按 Controller 对象而非设备）
- ✅ 确认后会话保持激活，之后从原版菜单开的每一局都是两名英雄同屏，直到手动关闭
- ✅ 两名英雄默认**共享血量、背包与属性**，**蓝图始终共享**；血量 / 装备 / 友伤各可在规则（Rules）菜单里切换。共享血量时任一英雄死亡即结束本局；**独立血量**下 0 血英雄**倒地**（队友还站着就只是隐藏并停用操作），走到有锻造所（选变异 NPC）的关卡复活
- ✅ **P2 有自己的 HUD，合并进原版 HUD。** 装备独立时它的血瓶、武器、主动技能和背包接在 P1 图标排后面，卷轴等级与血条放在下面第二行，变异图标与 P1 同一行——全部由游戏自己的 HUD 排版，所以换装备、换关卡、改 HUD 大小都不会让整列移位。共享装备时原版 HUD 本来就同时描述两人，不做改动。
- ✅ 相机跟随两名英雄，拉开距离时自动缩小视野保持同屏——不分屏
- ✅ **手动传送汇合**：被困的玩家**按住 T（键盘）或手柄左摇杆按下（L3）一秒**，该英雄直接跳到同伴身边——关门、空气墙都拦不住，因为这是重定位而不是走路；10 秒内再按一次，**该英雄自己**跳回原来的位置（返回不会移动另一名玩家）。独立血量下**倒地的队友也是目的地**：落到它倒下的地点。踩关卡内的**传送板时搭档会跟着一起传送**，两人同步移动。跟门、拽进 Boss 场的自动带过仍然没有——这个键和传送板就是全部的汇合手段，英雄头顶会显示「按住 T / L3 键传送」提示
- ✅ 陷阱、怪物与交互物对**两名英雄**都生效：食人花咬站在它嘴里的任何人，地刺 / 碎裂地面 / 摆动刀刃会响应 P2，怪物以自己的目标索敌受击，泉水 / 电梯 / 钥匙孔 / 商店 / 锻造所谁用都认谁
- ✅ **两名玩家可以同时把头（断头）放出来。** 每颗头完全独立——各自的手柄、各自的身体锁定、各自的召回——一方投掷或召回不再影响另一方。之前的阻塞点是技能侧 `onEnd()`：它在投掷动画结束（比按键晚几百毫秒）时把关卡里**所有**头全部杀掉再创建新的，所以只在 `prepare()` 期间过滤没有用；现在 `onEnd` 也套上了同一层过滤。
- ✅ **PVP 竞技场**：模组菜单首行的本地双人对战——每名英雄各自拥有血量、强制友伤，致死一击为对手输掉一回合；竞技场封闭、无怪物（先赢下 N 回合者获胜，用暂停菜单离开）。**第一回合是热身局**（不计分，结束后显示「比赛开始」），比赛规则页还提供**血量倍率**（1× / 2× / 5× / 10×），让一回合足够长、真正打得起来。详见 `docs/PVP_ARENA.md`
- ✅ 设置（设备、相机、规则里的血量 / 装备 / 友伤、PVP 模式与夺冠回合数及血量倍率、P2 皮肤）**跨游戏重启保留**

## ⚠️ 已知问题

- **共享相机关闭时**，视野只跟随 P1，P2 可能走出屏幕。
- 仍然**不分屏**（共享相机让两名英雄留在同一窗口）。P2 的 HUD 就在原版 HUD 内部，图标和血条与 P1 共用一列而不是单独一块；**共享血量**时只有图标排和等级条属于 P2（原版血条本来就说两人），且 P2 的护符不显示。
- 两名玩家**可以共用一个设备**（同一手柄的两个 pad，甚至同一块键盘）——此时两名英雄都响应该设备；隔离按 Controller 对象划分。
- 独立手柄行为、换关与子关卡仍需更多进游戏验证。

## 🗺️ 未来功能

以下为可能的方向（尚未承诺）：

- 可选的分屏 / 第二窗口，作为共享相机的替代。
- 支持超过两名玩家。

---

## ⭐ 支持项目

如果觉得这个项目有意思：
- ⭐ 给仓库点个 Star
- 🍴 Fork 后自己折腾

任何反馈都有助于改进《死亡细胞》的本地同屏体验。

---

## 🧰 环境要求

- **死亡细胞（PC）**——DCCM 对应游戏的 **v35** 分支（如 2026-06-16 的 `8b5231c` 构建）
- **DCCM（Dead Cells Core Modding API）**
- **[.NET 10 SDK / Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **[Microsoft Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)**（DCCM 运行需要）

---

## 📦 安装

两种方式：Steam 创意工坊订阅，或手动放入文件。带截图的分步教程见 [bilibili](https://www.bilibili.com/opus/1263440955984314368)，文字版如下。

### 通过 Steam 创意工坊

1. 订阅 [DCCM Core Modding API](https://steamcommunity.com/sharedfiles/filedetails/?id=3633185550)
2. 订阅 [DeadCellsLocalMultiplayerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3820679837)
3. 按 [创意工坊安装指南](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop) 替换 `deadcells.exe`。

### 手动安装（GitHub Releases）

1. 从 [DCCM Releases](https://github.com/dead-cells-core-modding/core/releases) 下载最新版 `win-x64.zip`，解压到游戏目录，并把解压出的 `core` 文件夹重命名为 `coremod`——布局变成 `Dead Cells\coremod\core\...`。
2. 从 [本仓库 Releases](https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/releases) 下载最新版 `DeadCellsLocalMultiplayerMod-v*.zip`，解压到 `coremod\mods\`，使模组位于 `coremod\mods\DeadCellsLocalMultiplayerMod\`。
3. 按 [创意工坊安装指南](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop) 替换 `deadcells.exe` ,通过 Steam 启动游戏。若不想通过 Steam 启动：在 `coremod` 里新建 `config` 文件夹，把 [modcore.json](https://github.com/yuan9931/DeadCellsLocalMultiplayerMod/releases/download/dccm/modcore.json) 放进去，然后双击 `Dead Cells\coremod\core\host\startup\DeadCellsModding.exe` 启动。
4. 进入游戏后，左下角会显示 DCCM 已加载的版本号；模组随之加载。

---

## 🕹️ 如何游玩

> 🌐 菜单界面已本地化：游戏语言设为**简体中文**时显示中文，设为英文时显示英文（其余语言回退英文）。

1. 通过steam启动游戏。
2. 标题菜单选择 **Local Co-op（本地同屏）**。
3. 为 **玩家 1（Player 1）** 和 **玩家 2（Player 2）** 各选一个设备（键盘 / 手柄 1–4，手柄可重复选）。
4. 按需调整相机（跟随两人并缩小 / 只跟随 P1）和二号英雄皮肤（Player 2 skin）。
5. 选择 **Start local co-op（开始本地同屏）**（会话激活）。
6. 从标题菜单正常开始一局——两名英雄同屏出现，各自用自己的设备操作。
7. 要汇合时，被困的玩家**按住 T（键盘）或 L3（手柄左摇杆按下）一秒**——英雄头顶的提示写明了该按哪个——该英雄直接跳到同伴身边；10 秒内再按一次跳回原位。独立血量下队友倒地时，长按会把你送到**它倒下的地点**。

> 💡 提示：两名英雄默认共享血量与背包——血量 / 装备 / 友伤各是**规则（Rules）**页上的一个开关。共享血量时任一英雄死亡本局结束；独立血量时倒地的英雄会留在倒落点等同伴，直到两人走到锻造所（选变异的 NPC）。会话保持激活，直到在菜单里 **禁用本地同屏（Disable local co-op）**。

### ⚔️ PVP 竞技场

1. 标题菜单 → **Local Co-op（本地同屏）** → **PVP 竞技场**（首行）。
2. 可选：切换 **PVP 模式**，进入 **比赛规则（Match rules）** 设定夺冠回合数与**血量倍率**（1×–10×，倍率越高回合越长）。
3. 选择 **立即开始一场比赛**——会直接启动一局训练模式的竞技场（必要时自动用已存设备激活双人会话），无需先回标题正常开局。
4. 在竞技场里各自拥有血量且必定互相伤害：**首回合为热身**（不计分，结束后显示「比赛开始」）；此后把对手打死即为对手输掉一回合，双方回位、满血，进入下一回合；先赢下目标回合数的一方获胜，比分与结果显示在屏幕顶部。
5. 用**暂停菜单**离开；再来一场从菜单的 **开始一场比赛** 发起。比赛本身不会结束这一局（竞技场复用游戏自带的训练模式）。

> ⚠️ 竞技场内已关闭怪物、掉落、支线房间、原版陪练 NPC 与手动传送（L3/T），这些都会破坏对决。

---

## 🔨 构建

仓库根目录一键构建（自动下载 DCCM MDK 并设置 `DCCM_MDK_ROOT`）：

```powershell
build.cmd
```

或已设置 `DCCM_MDK_ROOT` 时直接：

```powershell
dotnet build -c Release
```

产物在 `bin/Release/net10.0/win-x64/`，打包好的 mod 布局在 `output/DeadCellsLocalMultiplayerMod/`。

> 构建成功不等于游戏内生效——进游戏验证清单见 `AGENTS.md`。

---

## 📝 日志

日志**默认关闭**。启动游戏前设置环境变量 `DEADCELLSLOCALMULTIPLAYERMOD_LOG`（任意非 `0` 值）即可打开 mod 日志：

```powershell
$env:DEADCELLSLOCALMULTIPLAYERMOD_LOG = "1"   # 然后启动游戏
```

遇到问题？见[排错指南](docs/TROUBLESHOOTING_zh.md)。

---

## 📜 致谢

- **Dead Cells Core Modding API (DCCM)** — https://github.com/dead-cells-core-modding/core

---

<!--
关键词: 死亡细胞 双人 mod, 死亡细胞 同屏, Dead Cells local co-op, DCCM mod, Dead Cells 本地多人
-->
