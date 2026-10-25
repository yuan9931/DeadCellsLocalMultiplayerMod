# DCCM 与新关卡创建（New Levels on Dead Cells Core Modding）

> 目的：说明 `C:\Games\Github\dccm`（Dead Cells Core Modding，下称 DCCM）是什么、
> 一个关卡在游戏里由哪几部分组成，以及**如何新建一个关卡**。
> 依据：DCCM 源码 + MDK 工具链 + v35 `data.cdb` + `C:\Games\Github\decompiled\src` 反编译游戏源码
> + 官方教程 [Chapter One: Level Structure](https://dead-cells-core-modding.github.io/docs/docs/dev/tutorial/add%20new%20levels/Chapter%201%EF%BC%9ALevel%20Structure/)
> （[中文](https://dead-cells-core-modding.github.io/docs/zh/docs/dev/tutorial/add%20new%20levels/Chapter%201%EF%BC%9ALevel%20Structure/)）。
>
> **教程是社区早期作品，本文标注了它已核实为错误的地方（见文末"教程勘误"）。**
> 除注明外，本文的机制结论都来自源码/元数据核实；标 *未实机验证* 的条目没有跑过游戏。

---

## 1. DCCM 是什么

DCCM 劫持 Dead Cells 的 Hashlink VM（游戏跑在 Haxe 编译的 HL 字节码上），把托管 .NET 代码注入运行时，
于是 mod 能用 C# 直接读写游戏对象。启动链：

```
DeadCellsModding.exe → DCCMShell.dll → Startup.StartGame() → Core.Initialize()
  → 加载 [CoreModule(Preload)] 模块 → 读 hlboot.dat → Native.InitializeGame()（起 VM）
  → 广播生命周期事件（IOnBeforeGameInit / IOnGameInit / IOnGameEndInit / IOnFrameUpdate …）
```

改游戏有三条正交的通道，新关卡会同时用到三条：

| 通道 | 载体 | 决定什么 |
| --- | --- | --- |
| **数据** | `data.cdb`（JSON 表） | 关卡户口、biome、怪物表、房间登记、掉落 |
| **资源** | `.pak`（`tiled/tmx/**`、图集、音频） | 房间的具体几何与贴图 |
| **代码** | `ModBase` + Hashlink hook | 关卡的**房间拓扑**（图结构）与一切行为 |

关键源码位置：

- 模块与事件：`sources/ModCore/Modules/`、`sources/ModCore/Events/Interfaces/`
- pak 挂载：`sources/ModCore/Modules/FsPak.cs`
- CDB 合并：`sources/ModCore/Modules/CDBManager.cs`
- mod 发现：`sources/ModCore/Modules/ModLoader.cs`、`sources/ModCore.ModLoader.Default/`
- MDK 构建开关：`mdk/MDK.Package/raw/build/`
- CLI 工具：`mdk/DCCMTool/`（cdb diff / pak pack / tmx expand|collapse / atlas / steam）

MDK 已随游戏目录分发在 `dccm/core/mdk/`，NuGet 源 `packages/DeadCellsCoreModding.MDK.1.0.1.nupkg`。

---

## 2. 一个关卡由三样东西组成（理解全部流程的前提）

### 2.1 `level` 表一行 —— 关卡的"户口"

v35 `dccm/core/mdk/databases/v35/data.cdb` 有 162 张 sheet，关卡相关：`level`(112 行) `biome`(63)
`room`(2191) `roomType`(55) `layer`(9) `level@mobs` `level@props` `level@nextLevels` `roomMarker` 等。

`level` 列（顺序即语义）：

```
icon, id, name, biome, transitionTo, worldDepth, mapDepth, mobs, props, specificSubBiome,
baseLootLevel, cellBonus, tripleUps, doubleUps, bonusTripleScrollAfterBC, quarterUpsBC3,
quarterUpsBC4, mobDensity, eliteWanderChance, eliteRoomChance, flagsProps, specificLoots,
minGold, parallax, loreDescriptions, nextLevels, dlc
```

- `id`：关卡的唯一标识，也是代码侧的挂钩点（见 §3.4）。
- `biome`：指向 `biome` 表；biome 带 `atlasName`/`layers`/`lightColors`/`vegetation` 等 29 列，决定美术与环境。
- `icon`：`{x,y,file,size}` —— 大地图图标在 `cardIcons.png` 上的格子坐标（也是 §3.5 那个报错的来源）。
- `nextLevels`：`[{level, gates}]`，决定通关后能去哪一关、要几个门钥匙。

### 2.2 一个 `LevelStruct` 子类 —— 关卡的房间拓扑

`dc.level.LevelStruct`（[LevelStruct.cs](../../decompiled/src/dc/level/LevelStruct.cs)）是关卡生成器基类。
子类重写 `buildMainRooms()` 返回入口节点，并可选重写 `buildSecondaryRooms` / `buildTriggeredDoors` /
`buildTimedDoors` / `addTeleports` / `finalize` / `applyDifficulty`。

驱动顺序在 `LevelGen.generateGraph`
（[LevelGen.cs:376-433](../../decompiled/src/dc/level/LevelGen.cs)）：

```
_LevelStruct.get(user, ldat, rng)   // 工厂，按 ldat.id 选子类
 → buildMainRooms()
 → buildSecondaryRooms()
 → buildTriggeredDoors(combatRooms)
 → buildEliteRooms()
 → buildTimedDoors()
 → buildZChallengeDoors()
 → addTeleports()
 → finalize()
 → buildLoreRooms() / buildEssentialLoreRooms()
 → applyDifficulty()
```

### 2.3 `room` 表条目 + `tiled/tmx/<group>/<roomId>.tmx` —— 房间的几何

房间文件路径由这条式子拼出
（[_RoomTemplate.cs:85-107](../../decompiled/src/dc/level/_RoomTemplate.cs)）：

```
"tiled/tmx/" + Data.room[<id>].group 的名字(Room_group_Impl_.NAMES[group]) + "/" + <id> + ".tmx"
```

**`room.group` 就是 `room` sheet 的 `separatorTitles` 下标，等于 Tiled 子目录名**（共 131 组），
例如 `0=Lab, 1=Common, 6=NeedLadder, 7=NeedTeleport, 17=PrisonRoof, 69=RoofCorrupt, 85=Tumulus`。

TMX 必须含：
- 图块层 `col`（碰撞，`ColType`）、`lnk`（连接口，决定房间间怎么对接）
- 对象层 `markers`（NPC/机关/门位点，例如 kind=`ZDoor`）

`RoomTemplate` 解析 `col`/`lnk` 自动推导 `links`（`RoomTemplateLink`，带 `Enter`/`Exit`/`EnterOrExit` 方向）；
对接处必须是 `ColType.Plain` 或 `Invisible`，否则抛
`"invalid horizontal link group … in room …"`（[RoomTemplate.cs:142-163](../../decompiled/src/dc/level/RoomTemplate.cs)）。

> **所以教程把 `createNode` 第三个参数说成"内部引用 id"是不准确的：那是房间分组（Tiled 目录）索引。**

---

## 3. 新建关卡：可操作步骤

以教程示例的关卡 `Out_Clock` 为例。

### 3.1 csproj

参照 `sample/SampleWeapon/SampleWeapon.csproj` 与 MDK targets：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <ModType>mod</ModType>
    <ModName>Outside_Clock</ModName>
    <ModMain>Outside_Clock.Mian</ModMain>
    <AutoInstallMod Condition="'$(Configuration)' == 'Debug'">true</AutoInstallMod>

    <GenerateDiffCDB>true</GenerateDiffCDB>   <!-- 与官方模板做 diff，只打包改动的行 -->
    <GameVersion>35</GameVersion>             <!-- 定位 databases/v35/data.cdb 模板 -->
    <GenerateSinglePakFile>true</GenerateSinglePakFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="DeadCellsCoreModding.MDK" Version="1.0.1" />
  </ItemGroup>

  <ItemGroup>
    <!-- 自定义房间 TMX 打进 pak；RootInPak 必须落到 tiled/tmx/<group>/ 下 -->
    <PackAssets Include="tmx/**/*" RootInPak="tiled/tmx/MyGroup" />
  </ItemGroup>
</Project>
```

`GenerateDiffCDB` 的实际动作（`DiffCDBGenerator.targets`）：

```
dotnet DCCMTool.dll cdb diff -i <项目>/data.cdb -t <mdk>/databases/v35/data.cdb -o obj/_diffCDB.pak
```

diff 只把**变动行**写成 `data.cdb_/<sheet>/<sep>/<row>.json`，并补三个元数据字段
`__separator_group_Name` / `__separator_group_ID` / `__original_Index`
（[DiffCdbCommand.cs:56-82](../../dccm/mdk/DCCMTool/Commands/Cdb/DiffCdbCommand.cs)）。

> ⚠️ **不要整文件覆盖 CDB**：只改你要动的行；新增行走 diff 的"line == null"分支追加。
> 运行时 `CDBManager.GetAlteredCDB()` 把 `data.cdb_` 合并回基线 CDB 再 `Data.loadFrom`。

### 3.2 在 `level` 表加一行

新行的 `id` 是关卡的唯一标识；教程约定它**与 §3.3 的 C# 类名一致**。
（严格说这不是硬性要求——真正的绑定发生在 §3.4 的 hook 里，`if (l.id == "...")` 想写什么就写什么；
同名只是让两边不容易对不上。）
同时填 `biome`、`worldDepth`/`mapDepth`、`icon`、`nextLevels`。

> `level` 表**没有** `active` 列（v35 实测 27 列里无此项）—— `active` 是 `room` 表的列，
> 用来开关单个房间模板。关卡能否进池子靠 `nextLevels`/`transitionTo` 的连通性与 `dlc` 字段，
> 不是靠一个 active 开关。要测试自己的关卡，最省事的办法是照 §3.8 用 script 模组接一条出口进去。

### 3.3 写 `LevelStruct` 子类

```csharp
using dc;
using dc.level;
using dc.libs;
using Hashlink.Virtuals;
using ModCore.Utilities;   // AsHaxeString 是这里的扩展方法，漏了会 11 处 CS1061

namespace Outside_Clock;

// 类名 == cdb 里 level.id
public class Out_Clock : LevelStruct
{
    public Out_Clock(User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ level,
        Rand rng) : base(user, level, rng) { }

    public override RoomNode buildMainRooms()
    {
        // 入口：给 templateId（type 传 null）= 强制指定房间文件
        RoomNode start = createNode(null, "TU_BasicEntrance".AsHaxeString(), null,
                                    "start".AsHaxeString());
        start.addFlag(new RoomFlag.Outside());
        start.addFlag(new RoomFlag.NoExitSizeCheck());
        start.setConstraint(new LinkConstraint.NeverUp());

        // 固定某组里的随机战斗房：第 1 参给 type，第 3 参给 group 下标
        RoomNode combat = createNode("Combat".AsHaxeString(), null, 17, null)
            .addFlag(new RoomFlag.Outside());
        combat.set_parent(start);

        // 挂 NPC（markers 层之外的便捷方式）
        RoomNode demon = createNode(null, "OutsideCross1".AsHaxeString(), null,
                                    "exit".AsHaxeString())
            .addFlag(new RoomFlag.Outside())
            .addNpc(new NpcId.CryptDemon());
        demon.set_parent(combat);

        // 通关出口：目标关卡 id + 出口房 templateId
        RoomNode exit = createExit("ClockTower".AsHaxeString(),
                                   "RoofEndExit".AsHaxeString(), null,
                                   "end".AsHaxeString())
            .addFlag(new RoomFlag.Outside());
        exit.set_parent(demon);

        // 必须返回名为 start 的 Entrance 节点
        return nodes.get("start".AsHaxeString());
    }

    // 支线房：塞进主线两个已命名节点之间
    public override void buildSecondaryRooms()
    {
        base.buildSecondaryRooms();
        RoomNode anchor = getId("exit".AsHaxeString());
        createNode("Combat".AsHaxeString(), null, 69, null)
            .addFlag(new RoomFlag.Outside())
            .addBefore(anchor, null);
    }

    // 留空 = 关掉本关的随机传送生成
    public override void addTeleports() { }
}
```

`createNode` 的两种用法（[LevelStruct.cs:46-71](../../decompiled/src/dc/level/LevelStruct.cs)）：

| 形式 | 含义 |
| --- | --- |
| `createNode(type, null, group, id)` | 从该 `roomType` + 该 `group` 里**随机**抽一间 |
| `createNode(null, templateId, null, id)` | **强制**用某个 `room` 表条目（某间具体房） |

`RoomNode` 链式 API —— 以下为**真实编译验证过**的可调用成员（`dc.level.RoomNode`）：

| 可用（编译通过） | 不存在（CS1061，教程里却用了） |
| --- | --- |
| `addFlag(RoomFlag)` | **`AddFlags(...)`**（大写 A 的复数形式） |
| `setConstraint(LinkConstraint)` | **`setName(String)`** |
| `addNpc(dc.NpcId)` | **`chain(RoomNode)`** |
| `addGenData(virtual_altarItemGroup_…_)` | |
| `set_parent(RoomNode)` | |
| `addBefore(RoomNode, String)` / `addAfter(RoomNode, String)` | |
| `setExit(String)` / `forceTemplate(String)` / `set_spawnDistance(int)` | |
| `addBetween` / `branchTo` / `branchBetween` / `addToList` / `getRoot` / `searchChildren` / `hasParent` / `isChild` / `addZChild` / `setChildPriority` | |

> `setName` / `chain` / `AddFlags` 只存在于**脚本模组的包装类** `dc.tool.mod.script.RoomNode`
> （[script/RoomNode.cs:105-176](../../decompiled/src/dc/tool/mod/script/RoomNode.cs)），
> 而 `dc.tool.mod.*` 整个命名空间没有出现在 GameProxy 里，所以 C# 侧调不到。
> 教程 §三 的大段示例满屏 `AddFlags(...)` 与 `setName(...)`，**照抄必然编译失败**；
> C# 侧要改写成逐个 `addFlag(...)`，用 `createNode(..., id)` 的第四参给节点命名，用 `set_parent` 代替 `chain`。
> （节点"名字"在 C# 侧即 `RoomNode.uid`，由 `createNode` 的 `id` 参数写入，之后用 `getId(uid)` 取回。）
>
> ⚠️ **`uid` 重名不报错，会静默加数字后缀**：`RoomNode` 构造里若 `nodes` 已存在同名 key，会把它改成
> `start1`、`start2`…（[RoomNode.cs:109-114](../../decompiled/src/dc/level/RoomNode.cs)）。
> 于是 `getId("start")` 拿到的可能不是你最后创建的那间。命名冲突请显式区分，别依赖覆盖。
> `getId` 找不到时抛 `"Node <id> not found"`（[LevelStruct.cs:144-155](../../decompiled/src/dc/level/LevelStruct.cs)）。

常用 `RoomFlag`（`RoomFlag.Outside()` 等是嵌套类，`new` 即可；`NpcId` / `LinkConstraint` 同理）：

| Flag | 作用 |
| --- | --- |
| `Outside` | 标为室外，影响光照/音效/环境。**cdb 勾了就必须每个房都打** |
| `Holes` | 允许房内生成深坑（坠落伤害/致死） |
| `NoExitSizeCheck` | 跳过出口尺寸合法性检查，用于特殊出口 |
| `NoLoot` | 该房不生成任何战利品 |
| `NoCritters` | 不生成中立小动物 |
| `ForceCollExtTop` / `ForceCollExtBottom` | 强制顶/底外部碰撞边界（封闭感） |
| `InsideOut` | 内外翻转 |
| `LockedNeedKey` / `LockedNeedCard` | 需钥匙/门禁卡 |
| `TemplateFlip` | 允许翻转摆放 |
| `DarkRoom` | 黑暗房间 |

给房间换 biome（教程的"环境模板"）需要构造 Haxe 结构体 virtual：

```csharp
var v = new virtual_specificBiome_ { specificBiome = "ClockTower".AsHaxeString() };
start.addGenData(v.ToVirtual<
    virtual_altarItemGroup_brLegendaryMultiTreasure_broken_cells_doorCost_doorCurse_flaskRefill_forcePauseTimer_forcedMerchantType_isCliffPath_itemInWall_itemLevelBonus_killsMultiTreasure_locked_maxPerks_mins_noHealingShop_shouldBeFlipped_specificBiome_subTeleportTo_timedMultiTreasure_zDoorLock_zDoorType_>());
```

> 注意目标类型名以本地 GameProxy 为准：`…flaskRefill_forcePauseTimer_forcedMerchantType_isCliffPath…`。
> 教程写的是 `…flaskRefill_forcedMerchantType_forcePauseTimer_isCliffPath…`（两个字段换了序），
> **照抄会 CS0246**。见 §5。

### 3.4 注册结构工厂：hook `Hook__LevelStruct.get`（教程漏掉了这一步）

这是最关键、也最容易漏的一环。`LevelStruct.get()` **没有反射、没有注册表**：它是一个手写 if-chain，
把 `l.id` 逐个字符串比较后 `new` 出对应内置类
（[_LevelStruct.cs:49-2311](../../decompiled/src/dc/level/_LevelStruct.cs)，约 120 个分支）。
未知 id 走到末尾：

```csharp
Unsafe.SkipInit(out LevelStruct result);
return result;      // 未初始化 → 随后 LevelGen 调 buildMainRooms() 直接炸
```

MDK 的 GameProxy 里存在对应的静态 hook（Cecil 核实）：

```
dc.level.Hook__LevelStruct.get(
    orig_get orig, User user, virtual_baseLootLevel_…_worldDepth_ l, Rand rng) -> dc.level.LevelStruct
```

所以必须自己把新 id 接上：

```csharp
Hook__LevelStruct.get += (orig, user, l, rng) =>
{
    if (l.id == "Out_Clock".AsHaxeString()) return new Out_Clock(user, l, rng);
    return orig(user, l, rng);
};
```

> *未实机验证*（本会话无游戏可跑）。结论来自 `_LevelStruct.get` 的静态分析 + `Hook__LevelStruct.get`
> 的存在性核实。若实测发现另有注册途径，请以实测为准并更新本文。

**C# 子类为什么能被游戏虚调用派发到**：DCCM 的代理层会在构造对象时给该 C# 类型伪造一个 HL type，
并把 `override` 的 virtual 方法写进它的 `vobj_proto` / `rt->methods`：
`CustomHaxeType` 收集 `DeclaredOnly` 的 virtual 方法 → `ProtoOverride` 用 `HlCallback` 把
`callvirt` 路由注册到对应 proto 槽位（[CustomHaxeType.cs:278-315](../../dccm/sources/HaxeProxy/Runtime/Internals/Inheritance/CustomHaxeType.cs)、
[ProtoOverride.cs](../../dccm/sources/HaxeProxy/Runtime/Internals/Inheritance/ProtoOverride.cs)）。
入口在 `HaxeProxyBase` 构造 → `HaxeProxyManager.CheckCustomProxy`
（[HaxeProxyManager.cs:90-101](../../dccm/sources/HaxeProxy/Runtime/Internals/HaxeProxyManager.cs)）。
这条链由仓库自带测试 `test/TestRunner/ObjectInheritanceTest.cs` 覆盖
（C# override 后，从 HL 侧 `dyn.getSign()` 拿到的是覆写值）。

### 3.5 大地图图标：hook `Hook_LevelLogos.getLevelLogo`

新关卡 id 不在 `LevelLogos.textureCoordinateByLevelKind` 里，`getLevelLogo` 会直接抛
（[LevelLogos.cs:54](../../decompiled/src/dc/ui/hud/LevelLogos.cs)）。教程用钟楼占位。

**但 `textureCoordinateByLevelKind` 在本地 GameProxy 里没有暴露**（`dc.ui.hud.LevelLogos` 只有
`levelLogoTexture` + 四个方法），所以教程那行 `self.textureCoordinateByLevelKind.exists.Invoke(...)`
**编译不过**。可用的替代是"对新 id 直接换成已有坐标"：

```csharp
Hook_LevelLogos.getLevelLogo += (orig, self, levelId) =>
{
    if (levelId == "Out_Clock".AsHaxeString())
        return orig(self, "ClockTower".AsHaxeString());
    return orig(self, levelId);
};
```

> *未实机验证*。要真做"存在性判断"得走 `dynamic`/`HashlinkMarshal` 反射字段，或改
> `initLogoTexture` / `getLevelBannerPixels`（这两个也在 `Hook_LevelLogos` 里）。

### 3.6 让 pak 与 CDB 生效

**`<ModType>mod</ModType>` 的 mod 不会自动装载 pak。** 两条自动路径都不覆盖它：

- DCCM 的 `SideResModLoader` 只处理 `type == "res"` 的 mod（按 `modinfo.json` 的 `"paks"` 列表
  在 `Boot.initRes` 之后 `loadPak` + 合并 CDB）
  （[SideResModLoader.cs:42-57](../../dccm/sources/ModCore.ModLoader.Default/SideResModLoader.cs)）。
- 游戏自带 `tool.mod.ModManager.activateMod` 只处理 **Steam 创意工坊订阅项**（需 `settings.json`
  + `res.pak` 或 `Scripts/`）（[ModManager.cs:468-514](../../decompiled/src/dc/tool/mod/ModManager.cs)）。
- DCCM 里**没有**任何扫描 `coremod/mods/<mod>/res.pak` 的代码
  （对整个 `ModCore.dll` 提取字符串常量核实）。

所以要二选一：

**A. 拆成两个 mod（推荐，最省事）**：C# mod 保持不变；再放一个纯资源 mod，其
`modinfo.json` 为 `{"type":"res","paks":["res.pak"]}`，里面是合并好的 pak
（`GenerateDiffCDB` + `GenerateSinglePakFile` 已经把 CDB diff 与 assets 合进同一个 `res.pak`）。
`FsPak` 在 Steam 版还把 `loadModPak` 换成 `loadPak`，即**取消了 mod 数量限制**
（[FsPak.cs:40-44](../../dccm/sources/ModCore/Modules/FsPak.cs)）。

**B. 在 C# mod 里手动加载**（pak 与 CDB 都要自己触发）：

```csharp
using ModCore.Modules;   // FsPak、CDBManager
using HaxeProxy.Runtime; // Ref<bool>
using ModCore.Utilities; // AsHaxeString

void IOnGameEndInit.OnGameEndInit()
{
    var pak = Info.ModRoot!.GetFilePath("res.pak");
    FsPak.Instance.FileSystem.loadPak(pak.AsHaxeString());

    // 装了 pak 之后必须再合并一次 CDB，否则 data.cdb_ 里的新关卡行还没进 Data
    CDBManager.Instance.LoadJsonData(CDBManager.Instance.GetAlteredCDB());
}
```

`FsPak` 与 `CDBManager` 都是 public 类，`Instance`/`FileSystem`/`GetAlteredCDB`/`LoadJsonData`
均可从 mod 调用（Cecil 核实）。合并顺序**必须**是"先 `loadPak`，再 `LoadJsonData(GetAlteredCDB())`" ——
`GetAlteredCDB` 要读 `data.cdb_` 目录，pak 没挂载时它会直接返回未合并的基线 CDB。

**不要照抄教程第 5 步的两行**：

- `CDBManager.Class.instance.getAlteredCDB()` —— `dc.tool.mod.*` 整个命名空间在本地 GameProxy
  里**没有任何代理类型**（0 个），这句编译不过。用框架的 `ModCore.Modules.CDBManager.Instance` 代替。
- `dc.Data.Class.loadJson(json, default)` —— proxy 上只有 `Data.Class.loadFrom(String, Ref<bool>)`，
  **没有 `loadJson`**（框架自己用的就是 `loadFrom`，[CDBManager.cs:38](../../dccm/sources/ModCore/Modules/CDBManager.cs)）。
  而且这里**不该**用 `loadFrom`：它会按假路径重新读取原始 `data.cdb`，把刚合并进来的结果**回退**掉。
  `CDBManager.LoadJsonData` 的做法是先挂 `Hook__MultifileLoadSave.readFile` 让那次读取返回合并后的
  JSON，再调 `loadFrom`，最后 `finally` 清掉覆盖 —— 所以只调框架的高层入口。

> `Hook__Data.loadFrom(dc.String path, Ref<bool> allowReload)` 这个 hook 也在 proxy 里，
> 需要介入游戏自身的 CDB 重载时可以用它。

> 时机选择：框架在 `IOnAfterLoadingAssets` 的 XML 注释里明确写 "You can load your custom res.pak here"
> （[IOnAfterLoadingAssets.cs](../../dccm/sources/ModCore/Events/Interfaces/IOnAfterLoadingAssets.cs)），
> 它在 `Boot.initRes` 结束时广播。用这个时机比 `IOnGameEndInit` 更早，但**仍然要在 `loadPak` 之后
> 自己触发 `CDBManager.Instance.LoadJsonData(...)`** —— 合并不会因为你挂了 pak 就自动发生。
> 这也是**优先选方案 A** 的另一个理由：自动路径里"挂 pak → 合并 CDB"的顺序由框架保证。

**默认情况下框架已经替你做了合并**：`CoreConfig.UseGameCDBManager` 默认 `true`，框架优先用游戏的
`tool.mod.CDBManager.getAlteredCDB()`（运行时反射查找该 HL 类型），找不到时回退框架实现
（[CDBManager.cs:50-66](../../dccm/sources/ModCore/Modules/CDBManager.cs)）。
上面的手动代码只在选 B 方案时才需要。**能选 A 就选 A**——自动路径的时序由框架保证。

### 3.7 房间美术：TMX 工作流

```powershell
# 反出可读 XML（内部调 hl tmxtool.hl -Expand）
DCCMTool tmx expand   -b <游戏 tmx 二进制目录> -x <xml 输出目录>
# 用 Tiled 编辑 / 复制现有房间改
DCCMTool tmx collapse -b <目标二进制目录>    -x <xml 目录>
```

然后把产出的 `.tmx` 通过 `<PackAssets ... RootInPak="tiled/tmx/<group>">` 打进 pak。
`tiled/tmx/<group>` 必须与 `room` 表里该条目 `group` 指向的目录名一致。

### 3.8 快速验证连通性：官方 script 模组

不必先做完整 CDB+TMX，可用游戏内置的 `ModedLevel`
（[ModedLevel.cs](../../decompiled/src/dc/level/struct/ModedLevel.cs)）跑通链路：
`Scripts/Struct/main.hx`

```js
function buildMainRooms() {
    Struct.createRoomWithType("Entrance")
        .setName("start")
        .chain(Struct.createExit("SampleLevel"))
        .chain(Struct.createExit("Out_Clock"))
        .chain(Struct.createExit("PrisonRoof"));
}
```

约束：入口节点必须命名 `start` 且类型为 `Entrance`，否则抛
`You have to create a RoomNode with type "Entrance" named "start".`
脚本侧可调用的类型/函数在 `StructInterpreter.exposeTypesAndFunctions`
（[StructInterpreter.cs:657-796](../../decompiled/src/dc/tool/mod/script/StructInterpreter.cs)）：
`Room` / `Mob` / `LevelInfo` / `Struct` / `Meta` / `DecisionHelper` / `Random`，
枚举 `MerchantType` / `RoomFlag` / `LinkConstraint` / `ZDoorContentClue`，
入口 `buildMainRooms` / `buildSecondaryRooms` / `buildTriggeredDoors` / `buildTimedDoors` /
`addTeleports` / `finalize` / `setLevelInfo` / `setLevelInfoFrom` / `setLevelPropsFrom` /
`addMobRosterFrom` / `buildMobRoster` / `isRandomBelow` / `shuffleArray` /
`callDefaultAddTeleport` / `setAutomaticMobScaling` / `setMobTier` / `applyDifficulty`。
`Struct.*` 的可用方法见 [_Struct.cs](../../decompiled/src/dc/tool/mod/script/_Struct.cs)
（注意 `createRoomFromGroup` 是**空实现返回 null**）。

---

## 4. 两条路线对比

| | core modding（C# `LevelStruct`） | 官方 script 模组（`.hx`） |
| --- | --- | --- |
| 关卡结构 | C# 子类 + `Hook__LevelStruct.get` | `Scripts/Struct/*.hx` 的 `buildMainRooms` |
| 关卡 id | 必须新增 `level` 行且 `id` == 类名 | 复用内置 `ModedLevel`；`createExit("<未注册 id>")` 会自动落到 `ModedLevel` |
| 灵活性 | 最高（可 hook 全部关卡生命周期方法，见 §2.2 与 §3.3–§3.6） | 受限于暴露给脚本的 `Struct.*` |
| 上手 | 需 MDK + CDB + TMX + 两个 hook | 只需脚本目录 |
| 适合 | 正式关卡 mod | 快速原型、验证连通性 |

补充：`ModedLevel` 的"未注册 id 落到 ModedLevel"行为在脚本侧可见——
`_Struct.createExit` 里 `Data.level.resolve(name)` 为 null 时 `dest` 取 `"ModedLevel"`
（[_Struct.cs:209-217](../../decompiled/src/dc/tool/mod/script/_Struct.cs)）。

---

## 5. 教程勘误（编译器实证）

下表 #1 来自源码分析；**#2–#9 都是把教程原文丢进 Roslyn 真编译后拿到的错误码**。
复现方式见 §5.1（一个只引用 MDK 参考集成的独立探针工程，**不需要游戏本体**）。

| # | 教程说法 | 实际 | 实证依据 |
| --- | --- | --- | --- |
| 1 | 只给了 `Hook_LevelLogos` 一个 hook | **必须有 `Hook__LevelStruct.get`**，否则新 id 拿到未初始化的 `LevelStruct` | `_LevelStruct.cs` 是无反射 if-chain，末尾 `SkipInit` 返回未初始化值；`Hook__LevelStruct.hook_get` 存在并经编译验证可挂 |
| 2 | `createNode` 第三参 = "内部引用 id" | 是 `room.group`，即 `tiled/tmx/<group>` **目录索引** | `_RoomTemplate.cs:85-107`；`separatorTitles` 131 项 |
| 3 | 第 5 步 `loadPak("Kaguya.pak")` | 名字是从别的 mod 抄来的残留；`mod` 类型 mod **不会被自动装载**，需 `type:"res"` 子 mod 或手动加载 | `SideResModLoader`（只认 res）；`ModManager.activateMod`（只认工坊）；整个 `ModCore.dll` 字符串常量扫描无 mods 目录 pak 扫描 |
| 4 | `CDBManager.Class.instance.getAlteredCDB()` | 编译不过：`error CS0234`（"命名空间 dc.tool 中不存在 mod"）。改用 `ModCore.Modules.CDBManager.Instance.GetAlteredCDB()` | 编译器；Cecil 枚举 GameProxy 确认 `dc.tool.mod.*` 代理类型数 = 0 |
| 5 | `dc.Data.Class.loadJson(json, default)` | 编译不过：`error CS1061`（"`_Data_` 未包含 `loadJson`"）。proxy 上只有 `loadFrom(String, Ref<bool>)`；且**不该**直接调它（会回退 CDB，见 §3.6） | 编译器；框架自身用的就是 `loadFrom`（`CDBManager.cs:38`） |
| 6 | `self.textureCoordinateByLevelKind.exists.Invoke(...)` | 编译不过：`error CS1061`（"`LevelLogos` 未包含 `textureCoordinateByLevelKind`"）。需改为按 id 映射（§3.5） | 编译器；Cecil 枚举 `dc.ui.hud.LevelLogos` |
| 7 | 环境模板 virtual 类型名 `…flaskRefill_forcedMerchantType_forcePauseTimer_isCliffPath…` | 编译不过：`error CS0246`。本地实际顺序是 `…flaskRefill_forcePauseTimer_forcedMerchantType_isCliffPath…` | 编译器；`RoomNode.addGenData` 的参数类型 |
| 8 | 大段 C# 示例用 `AddFlags(...)` / `setName(...)` / `chain(...)` | 三者均 `error CS1061`。它们只在脚本包装类 `dc.tool.mod.script.RoomNode` 上；C# 侧用逐个 `addFlag`、`createNode` 第 4 参命名、`set_parent` 串联 | 编译器；`script/RoomNode.cs:105-176` |
| 9 | `using ModCore.Utitities;` | 该命名空间在当前源码里**不存在**（Utitities 拼写错误，全仓库 0 处）。`AsHaxeString` 在 `ModCore.Utilities`；`FolderInfo.GetFilePath` 在 `ModCore.Storage`。教程的 C# 片段**缺 `using ModCore.Utilities`**，整段贴出实测得 6 处 `CS1061: "string"未包含"AsHaxeString"`（每个字面量一处） | 编译器；`sources/ModCore/Utilities/StringUtils.cs` |
| 10 | "在 cdb 勾选 Outside 后代码里可不写" | 每个房仍需 `addFlag(new RoomFlag.Outside())`，否则光照/音效不对 | 教程正文自身示例每房都加；`RoomFlag` 参与 biome 环境分支 |

> 另：教程"1.创建主要房间"那段代码本身就少一个分号（`.addFlag(...)` 换行直连 `exitNode`），
> 是 `error CS1002: 应输入 ;`，属于排版笔误。

### 5.1 复现这些错误码（探针工程）

只需要装了 MDK 的机器，不依赖游戏本体：引用 `mdk/ref/GameProxy.dll` 与 `core/host/*.dll` 即可编译。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="GameProxy"><HintPath>...\dccm\core\mdk\ref\GameProxy.dll</HintPath></Reference>
    <Reference Include="HaxeProxy"><HintPath>...\dccm\core\host\HaxeProxy.dll</HintPath></Reference>
    <Reference Include="HashlinkSharp"><HintPath>...\dccm\core\host\HashlinkSharp.dll</HintPath></Reference>
    <Reference Include="ModCore"><HintPath>...\dccm\core\host\ModCore.dll</HintPath></Reference>
    <Reference Include="ModCore.Common"><HintPath>...\dccm\core\host\ModCore.Common.dll</HintPath></Reference>
  </ItemGroup>
  <ItemGroup><Compile Include="Probe.cs" /></ItemGroup>
</Project>
```

实测结果：§3.3 + §3.4 + §3.5 + §3.6 的**推荐写法整体 0 错误 0 警告编译通过**（产出 `probe.dll`）；
把教程原片段逐个替换进去，即得上表 #4–#9 的错误码。这是本文所有 C# 断言的验证方式。

---

## 6. 常见报错与定位

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 进关卡即崩 / `Null access` | 忘挂 `Hook__LevelStruct.get`，工厂返回未初始化对象 | §3.4 |
| `getLevelLogo` 抛错 | 新 id 无大地图坐标 | §3.5 |
| `invalid horizontal link group … in room …` | TMX 的 `lnk` 对接位不是 `Plain`/`Invisible`，或边上一侧无 link | 检查 `col`/`lnk` 图层 |
| `not enough exits for template X` | 房间出口数少于请求的 `nexits` | 补 link 或换房 |
| `You have to create a RoomNode with type "Entrance" named "start"` | 脚本模组入口节点命名/类型不对 | §3.8 |
| `file main.hx is not found in mod path` | 脚本 mod 缺 `Scripts/Struct/main.hx` | §3.8 |
| 关卡空白 / 房间不出现 | pak 未装载（`mod` 类型不自动装） | §3.6 |
| CDB 改动不生效 | 只放了整份 `data.cdb` 而没走 diff；或 diff 模板版本不匹配 | `<GameVersion>` 与 `databases/v<N>/` 对齐 |
| 分支房间接不上 | 引用的房间文件没有对应方向的入口 | 换房或补 `lnk` |

---

## 7. 与本仓库（DeadCellsLocalMultiplayerMod）相关的提醒

- 工作区 `AGENTS.md` 写"读 `E:\Programs\Github\deadcells\decompiled`"，该路径已不存在；
  反编译源码实际在 **`C:\Games\Github\decompiled\src`**（v35 CDB 在
  `dccm/core/mdk/databases/v35/data.cdb`）。
- 与本仓库既有约定一致：**不要在 C# 里 `new ArrayObj()` 再 `push()`**
  （会在 `ArrayObj.push` 里 `0xC0000005`）。要构造子集用 `slice`/`filter` 等 Haxe 侧操作。
- hook 体内一律 try/catch + 记日志：hook 里抛异常会带崩游戏。
- 读 Haxe `ref` 参数前必须判空（`Ref<bool>.IsNull`），游戏侧会传真 null。

---

## 8. 参考文件索引

DCCM：
[FsPak.cs](../../dccm/sources/ModCore/Modules/FsPak.cs) ·
[CDBManager.cs](../../dccm/sources/ModCore/Modules/CDBManager.cs) ·
[ModLoader.cs](../../dccm/sources/ModCore/Modules/ModLoader.cs) ·
[SideResModLoader.cs](../../dccm/sources/ModCore.ModLoader.Default/SideResModLoader.cs) ·
[DiffCdbCommand.cs](../../dccm/mdk/DCCMTool/Commands/Cdb/DiffCdbCommand.cs) ·
[ExpandTmxCommand.cs](../../dccm/mdk/DCCMTool/Commands/Tmx/ExpandTmxCommand.cs) ·
[CdbSheet.cs](../../dccm/mdk/GameRes.Core/Cdb/CdbSheet.cs) ·
[DiffCDBGenerator.targets](../../dccm/mdk/MDK.Package/raw/build/utils/DiffCDBGenerator.targets) ·
[GenerateSinglePakFile.targets](../../dccm/mdk/MDK.Package/raw/build/utils/GenerateSinglePakFile.targets) ·
[mod/build.targets](../../dccm/mdk/MDK.Package/raw/build/types/mod/build.targets) ·
[CustomHaxeType.cs](../../dccm/sources/HaxeProxy/Runtime/Internals/Inheritance/CustomHaxeType.cs) ·
[ProtoOverride.cs](../../dccm/sources/HaxeProxy/Runtime/Internals/Inheritance/ProtoOverride.cs) ·
[ObjectInheritanceTest.cs](../../dccm/test/TestRunner/ObjectInheritanceTest.cs) ·
[dccm/AGENTS.md](../../dccm/AGENTS.md)

游戏（反编译）：
[LevelStruct.cs](../../decompiled/src/dc/level/LevelStruct.cs) ·
[_LevelStruct.cs](../../decompiled/src/dc/level/_LevelStruct.cs) ·
[LevelGen.cs](../../decompiled/src/dc/level/LevelGen.cs) ·
[RoomTemplate.cs](../../decompiled/src/dc/level/RoomTemplate.cs) ·
[_RoomTemplate.cs](../../decompiled/src/dc/level/_RoomTemplate.cs) ·
[ModedLevel.cs](../../decompiled/src/dc/level/struct/ModedLevel.cs) ·
[Custom.cs](../../decompiled/src/dc/level/struct/Custom.cs) ·
[ScriptManager.cs](../../decompiled/src/dc/tool/mod/script/ScriptManager.cs) ·
[StructInterpreter.cs](../../decompiled/src/dc/tool/mod/script/StructInterpreter.cs) ·
[_Struct.cs](../../decompiled/src/dc/tool/mod/script/_Struct.cs) ·
[LevelLogos.cs](../../decompiled/src/dc/ui/hud/LevelLogos.cs) ·
[ModManager.cs](../../decompiled/src/dc/tool/mod/ModManager.cs)

外部：官方教程 [EN](https://dead-cells-core-modding.github.io/docs/docs/dev/tutorial/add%20new%20levels/Chapter%201%EF%BC%9ALevel%20Structure/) ·
[中文](https://dead-cells-core-modding.github.io/docs/zh/docs/dev/tutorial/add%20new%20levels/Chapter%201%EF%BC%9ALevel%20Structure/)
（本系列仅第一章已发布；"构造地图环境""随机结构"两章尚未上线）
