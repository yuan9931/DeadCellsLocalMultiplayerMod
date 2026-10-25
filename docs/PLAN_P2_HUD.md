# P2 HUD 与装备独立 — 实施计划

> 本文件记录「给 P2 加完整 HUD」这一工作的目标、已确认的设计决策、分阶段步骤与风险。
> 完成后随最后一步提交，并在 `AGENTS.md` / `AGENTS_zh.md` 里留出指向。

**状态（2026-10-18）**：A1–A3、B1、B2、B4、C1–C3 已完成；**B3（真实图标）按预案退化并延期**，
原因见第五节 B3 与第八节。下面的勾选反映实际结果，不改写当初的计划文字。

## 一、目标

在现有 P2 血条之上，为二号英雄补上：武器、道具（技能）、血瓶、卷轴数、疫病等级。
按已确认的方向，装备先做成**两人可独立**（P2 有自己的背包），否则这些数值与 P1 完全相同，
面板没有意义。

## 二、已确认的三项决策

| 项 | 选择 |
| --- | --- |
| 装备语义 | **改成装备独立**，但做成可切换开关 `SharedEquipment`，**默认共享**（保留回退路径） |
| 图标 | **试真实图标图集**；取不到则退化为几何色块 + 名称文字 |
| 显示时机 | **始终显示**（共享血量与独立血量两种模式都画） |

## 三、现状（实施前）

- `Coop/LocalCoop.P2Hud.cs`：已有 P2 血条。技法 = 新建 `h2d.Object` 面板挂在
  `HUD.heroLifeBar` 的 parent 下做**兄弟节点**，内部 `Graphics` 自绘外壳/暗底/变色填充，
  `Assets.makeText` 居中画 `P2 120 / 200`；每帧重锚到 P1 右侧；出错即 `_p2HudBroken = true`
  永久降级。文本必须用 `set_text()` 而非 `.text =`（Haxe 代理只暴露方法）。
- 仅 `SharedHealth == false` 时才显示。
- `hero2.inventory = hero1.inventory`（同一实例，每帧重新断言），因此武器/道具/血瓶/卷轴/
  疫病/诅咒全部与 P1 相同；冷却也经 `SyncSkillCooldowns` 有意共享。

## 四、已探明的游戏 API（实测 `GameProxy.dll`，非猜测）

> 探测方式：用 `MetadataLoadContext` 加载 `dccm/core/mdk/ref/GameProxy.dll`（引用程序集不能
> `LoadFrom` 执行），枚举类型与成员。下列结论均已由编译通过验证。

### `dc.ui.HUD`
- 字段：`heroLifeBar`（`hud.LifeBar`）、`skillWeapons` / `skillPowers` / `skills`
  （`ArrayObj<hud.Skill>`）、`skillPotion`（血瓶，`hud.Skill`）、`skillBackpack`、
  `tierBar`（`hud.TierBar`）、`heartCounter`、`cellsCount` / `moneyCount` / `killCount`
  （`hud.Count`）、`leftFlow` / `rightFlow` / `perkStock` 等
- 方法：`initItems(hero)`、`refreshItems()`、`setHealings(cur,max)`、
  `updateInfection(v,max,tierCount)`、`setBrutalityTier` / `setTacticTier` /
  `setSurvivalTier`、`updateAmmo(i)`、`updateIcon(i,tile)`、`updateLifeSize()`
- `hud.LifeBar`：`setInfection(v,max,tierCount,isAlwaysShown)`、`fInfection`、`label`、
  `curState`、`setSize(w,h)`、`updateContent()`
- `hud.Skill`（继承 `h2d.Layers`）：`ii`、`icon`（`h2d.Bitmap`）、`setCooldown(t,r)`、
  `setCount(i,max)`、`ammoIcons`、`cptVal` / `cptMax`、`lvlTxt`、`setBmpIcon(bmp)`
- `hud.TierBar`：`brutalityTier` / `tacticTier` / `survivalTier`
- **注意**：本 MDK 没有 `Hook_HUD`，`HUD` 的方法挂不上钩子。

### 装备相关
- `dc.tool.Inventory`：**有 `clone()`**（Haxe 侧克隆，C# 不需要构造）、`items`、`nbActives`、
  `nbWeapons`、`getActiveOn(i)`、`getEquippedWeaponOn(i)`、`equip(i)`、`add(i)`、
  `countBrutalityUp` / `countTacticUp` / `countSurvivalUp`、`swapWeapons()`、`swapSkills()`
- `dc.tool.hero.HeroActiveSkillsManager`：`init()`、`initSkills()`、`killEntities()`、
  `getActiveSkill(item)`、`hudGetSkillPower(idx)`（**会写全局 HUD，禁止用于 P2**）
- `dc.tool.InventItem`：`ammo`、`getMaxAmmo()`、`getUpgradeLevel()`、`getOnlyName()`、
  `_itemData`（含 `icon` / `id` / `name` / `tier1` / `tier2` / `group`）
- `dc.en.Hero`：`cells`、`addMoney()` → **金币与细胞挂在 Hero 上，不在 Inventory 里**，
  所以拆背包不会拆散经济；另有 `infectionMax`、`infectionTierCount`、`curseCounter`、
  `computeTiers()`

### 实施中新增探明（本轮实测，B1/B2 就靠这些）
- **血瓶次数不在 `Inventory` 里**：`dc.tool.mainSkills.Heal` 持有 `get_healings()` /
  `get_maxHealings()`，从 `hero.mainSkillsManager.skills`（`ArrayObj`）里找那个 `Heal` 实例。
- **冷却**：`dc.tool.hero.HeroActiveSkill` 有 `getNextUseCooldownRatio()`（0–1）、
  `get_usesLeft()`、`hasMultipleUses`；`OldSkill` 有 `coolDownF`（剩余秒）与 `coolDownMaxF`
  （总时长）。非充能型技能 `getNextUseCooldownRatio()` 返回 0，需回退到
  `coolDownF / coolDownMaxF`。
- **卷轴 / 属性层数**：`Inventory.countBrutalityUp / countTacticUp / countSurvivalUp`
  （参数是 `Ref<bool> withAffix`，传一次性局部变量的 `ref`）。
- **疫病**：`Hero` 上只有 `infectionMax`（`Int32`）与 `infectionTierCount`（`Int32`）；
  **当前疫病值在代理里没有任何可读来源**。全局系统是 `dc.pr.Game.infection`
  （`dc.pr.Infection`），它有 `infectionTier`（`Int32`，当前层级）。因此面板的疫病条只能
  **按层级**填充，无法显示「距离下一层还差多少」。已排除的来源：`Hero`（含其基类
  `dc.Entity`）没有 `infection*Value` 字段、`Game` 没有数值型疫病字段、
  `Hashlink.Virtuals` 里只有关卡设置 `carriedInfection`。
- **图标**：`InventItem._itemData.icon` 是匿名结构
  `{file, x, y, width, height, size}`（精灵图描述符），**不是 `h2d.Tile`**。全程序集里返回
  `Tile` 的方法只有 `dc.libs.heaps.slib.SpriteLib.getTile(group, frame, …)`、
  `dc.hxd.res.Image.toTile()` 等，都需要一个「物品美术 group 名」，而代理没有暴露
  `file → SpriteLib` 的映射；唯一的 item 工具类 `dc.tool.utils.ItemUtils` 只有
  `dropAndUnlockItem`。见 B3。
- **静态成员的位置**：Haxe 的静态成员挂在 `_Xxx` 伴生类型上，通过 `Xxx.Class` 访问
  （与 `Game.Class.ME`、`Assets.Class.makeText`、`Data.Class.skin` 同一套代理模式）。

## 五、分阶段步骤

### A. 装备独立（数据 / 规则层）

- [x] **A1** 新增 `SharedEquipment` 开关（默认 `true`）
  - `Coop/LocalCoop.cs`：属性 + `SetSharedEquipment()`（存盘 + `ApplyEquipmentMode()` + 日志）
  - `Coop/LocalCoop.Settings.cs`：`CoopSettingsData.SharedEquipment`（默认 `true`）存取
  - `ModEntry.cs` Rules 页加 `Equipment: shared / independent  < Toggle >`；Summary 拆成
    「相机 | 血量」与「装备 | 友伤」两行（页面仍是 6 行，与根页面同高）
- [x] **A2** 新文件 `Coop/LocalCoop.Equipment.cs`
  - `ApplyEquipmentMode()` / `ShareInventory()` / `GiveOwnInventory()` /
    `RebuildSecondHeroLoadout()`
  - `GiveOwnInventory` 用 `hero1.inventory.clone()`；`_hero2OwnInventory` 记住克隆结果保证幂等
  - `RebuildSecondHeroLoadout`：`killEntities()` → `initSkills()` → `RefreshSecondHeroSkills()`
    → `weaponsManager.onEquippedItemsUpdated(Ref<bool>.DontCare)` → `computeTiers()`
    （**刻意不走 `Hero.onEquipedItemsChange`**，那会写全局 HUD）
  - `Coop/LocalCoop.Spawning.cs` `ShareProgression()`：共享模式照旧赋实例，独立模式走克隆
  - `Coop/LocalCoop.cs` `OnFrameUpdate`：背包重新断言改为**仅共享模式**下执行
  - `ResetRunState()` 清 `_hero2OwnInventory`
- [x] **A3** 钩子改为受开关控制
  - `Coop/LocalCoop.Hooks.cs` `OnEquipedItemsChange`：独立模式下不再把装备变更镜像给伙伴
  - `Coop/LocalCoop.Hooks.cs` 诅咒 / 感染重定向（`curse` / `endCurse` / `addInfection` /
    `removeInfection` / `setInfection`）：独立模式下不再重定向到 P1（否则 P2 的疫病等级
    永远等于 P1）。**细胞与金币仍重定向到 P1**（保持共享经济）
  - `Coop/LocalCoop.SharedState.cs` `SyncSkillCooldowns`：独立模式下不再收敛冷却
  - **额外（计划外但必需）**：`curseCounter` 的每帧复制原本写在 `if (SharedHealth)` 块里，
    在共享血量 + 独立装备时会把 P2 的诅咒数每帧压回 P1 的值，正好抵消上面的重定向。已把它
    移出该块、改为受 `SharedEquipment` 控制——诅咒跟随**背包**模式，而不是血量模式。

### B. P2 面板（展示层）

- [x] **B1** 新文件 `Coop/LocalCoop.P2HudData.cs`：纯读取函数，从 `hero2` 抓出
  血量、主/副武器（名 + 等级 + 冷却 + 弹药）、技能槽 ×2、血瓶 `cur/max`、
  三属性层数、疫病 `value/max/tier`、诅咒计数。全程 try/catch + null 守卫。
  - 实现为一组 `P2SlotInfo` + `P2HudSnapshot` 记录结构（纯 C# 数据，绝不回传 Haxe），
    快照带一个 `Signature` 字符串用于变更检测
  - 疫病的 `value` 取不到（见第四节），改为只读 `infectionMax` / `infectionTierCount` /
    `Game.infection.infectionTier`
- [x] **B2** 扩展 `Coop/LocalCoop.P2Hud.cs`（沿用已验证的 Graphics + `set_text()` 技法）
  - 锚点仍是 `HUD.heroLifeBar` 的兄弟节点，每帧重锚；**不进 `FlowBox`**（布局会覆盖 x/y）
  - 一个 `Graphics` 画全部几何，文本叠在其上；`Signature` 不变就整帧不重绘
  - 入口改名 `UpdateP2HealthBar` → `UpdateP2Panel`、`RemoveP2HealthBar` → `RemoveP2Panel`
    （`LocalCoop.cs` 的调用点与 `ResetRunState` 同步更新）
  - **首版布局**：血条 + 疫病条一行、武器槽 ×2 一行、技能槽 ×2 一行、血瓶 + 三属性 + 诅咒一行，
    固定 210px 宽、文字缩到 0.6~0.7。
- [x] **B2b** 面板改版（用户实测后反馈「UI 太小」+ 指定版式，2026-10-18）
  - **版式（自上而下）**：`FLASK` 一行 → 武器 `W1`/`W2` → 技能 `S1`/`S2` → **血条（疫病条贴在下方）**
    → `CURSE` + `B`/`T`/`S` 一行。装备/技能各占一条**整宽**槽带（不是并排半宽），
    这样原版字号下的名字才有地方放。
  - **尺寸与对齐**：面板宽高直接取 `heroLifeBar.curState.outerWid` / `outerHei`，
    放在 P1 生命条右侧 `+6`，且**血条行与 P1 生命条同一条水平线**（上方各行向上延伸）。
    不再用固定像素，随分辨率 / HUD 缩放自动跟住 P1。
  - **字号**：`customScale = 1.0`（原版字号，不再缩小）。为了不溢出，每行靠
    `set_text` + 读真实 `textWidth` 反复适配；装备行上**只有物品名会缩短**，
    `P2SlotInfo.Detail`（等级 / 弹药 / 层数 / 冷却）先测量并始终保留。
  - 重绘键同时包含 P1 的几何尺寸（`WxH|Signature`），所以尺寸变化也会重绘。
- [x] **B2c** 版式再改：按 P1 的真实行距对齐（用户实测反馈「P2 面板把 P1 的 UI 顶上去了」+
      「P2 武器格子要和 P1 武器格子对齐」，2026-10-18）
  - **根因**：面板的父级是 `h2d.Flow`。往 Flow 里加一个比兄弟节点高的子节点会改变整行高度，
    Flow 随即重新居中其他节点 → **P1 整套 HUD 被顶上移**。修法：
    `parent.getProperties(panel).set_isAbsolute(true)` + `set_needReflow(true)`，面板不再参与该布局。
  - **探针**：新增一次性诊断 `LogHudLayoutOnce`（仅日志开启时运行），打出 P1 各 HUD 元素的绝对坐标。
    1080p 实测：
    `lifeBar=(222,1012) tierBar=(48,1012) potion=(48,862) backpack=(603,862)`、
    `weapons[0]=(165,862) weapons[1]=(267,862) powers[0]=(486,862) powers[1]=(384,862)`、
    `lifeSize=477x42 parentType=Flow`
  - **结论**：P1 把所有道具槽排在**一行**（y=862），属性条与血条在**下一行**（y=1012），
    两行相距 150px，槽位间距 102px。原先「5 行文字堆叠、血条与 P1 同线」的版式与真实布局完全不符，
    且总高约 330px 已超出 1080 屏幕——这正是它看起来别扭的原因。
  - **新版式**：面板 = 一行五个格子（`FLASK`/`W1`/`W2`/`S1`/`S2`，宽 = P1 血条宽/3 = 159，
    高 = 槽位间距 102）落在 **P1 的道具行**；血条（P1 的 477×42）落在 **P1 的血条行**；
    疫病条贴在其下；`CURSE`/`B`/`T`/`S` 同行放在血条**右侧**（即 P1 放属性条的位置）。
  - **不再写死任何行距**：道具行偏移取 `weapons[0].absY - lifeBar.absY`、槽位间距取
    `weapons[1].absX - weapons[0].absX`，每帧重读；HUD 尚未布局时用一套回退形状兜底。
  - 字号：血量数字 `1.0`，槽位两行 `0.8`（实测字号 1.0 时文字高约 36px，102px 高的格子里放名字只能用 0.8）。
- [ ] **B3** 真实图标 —— **延期，按预案退化**
  - 已探明：`_itemData.icon` 是精灵图描述符而非 `h2d.Tile`；返回 `Tile` 的入口
    （`SpriteLib.getTile`、`hxd.res.Image.toTile`）都需要代理未暴露的「物品美术 group 名」；
    `dc.tool.utils.ItemUtils` 只有 `dropAndUnlockItem`
  - 结论：**取不到**，因此按预案退化为几何色块 + 名称文字（B2 已实现）。若将来要真图标，
    先找到 `file → SpriteLib` 的映射，再走 `h2d.Bitmap` + `hud.Skill.setBmpIcon` 那条路
- [x] **B4** 始终显示：去掉 `UpdateP2HealthBar()` 里的 `if (SharedHealth) return;`

### C. 收尾

- [x] **C1** 同步 `AGENTS.md` / `AGENTS_zh.md`（文件布局表加 `LocalCoop.Equipment.cs`、
      `LocalCoop.P2HudData.cs`；新增「Shared vs independent equipment」与「Player two's panel」
      两节；改写装备镜像 / 诅咒感染重定向 / 冷却收敛 / 菜单 / 持久化 / 局限 / 验证清单）
- [x] **C2** 修两份 README 里「没有二号英雄的 HUD」的过时描述，补上独立装备特性
- [x] **C3** 提交 + `build.cmd` 构建，报告两者结果

## 六、保持不变的部分（写在这里防误改）

- `blueprints`：两种模式都共享（解锁是整局进度，不是装备）
- 金币、细胞：挂在 `Hero` 上，继续重定向到 P1，共享
- 共享血量（`SharedHealth`）时的血量：与装备模式无关，独立判断

## 七、风险与对策

| 风险 | 对策 |
| --- | --- |
| `Inventory.clone()` 后 P2 技能管理器仍绑着旧物品实例 | `RebuildSecondHeroLoadout` 走 `killEntities()` + `initSkills()` 重建；需进游戏实测 |
| 独立装备 = 独立卷轴 = 两人属性层级不同，连带影响伤害与结算 | 预期效果，但必须进游戏验证 HUD 与结算不炸 |
| 在 C# 里构造 Haxe 对象 / `new ArrayObj()` + `push()` 会 `0xC0000005` 崩游戏 | 一律用 Haxe 侧方法（`clone()`），不构造；图标取不到就几何退化 |
| P2 面板每帧重绘开销 | `Signature` 不变就整帧不重绘；位置重锚每帧只写两个 double |
| 动 HUD 有崩游戏风险 | 面板代码整体包 try/catch，出错即 `_p2HudBroken = true` 永久降级 |
| 构建成功 ≠ 行为正确 | A3、B2、B3 之后都要进游戏验证（见 `AGENTS.md` 的验证清单） |

## 八、遗留与后续

1. **B3 真实图标**：需要先找到 `_itemData.icon.file` 到美术 `SpriteLib` 的映射（可能要从
   `dc.Data` 的物品表或 `Res` 的资源加载路径入手），否则只能停在几何退化。
2. **疫病进度**：当前疫病值在代理里不可读，面板只能显示层级。若要显示进度，需要另找来源
   （例如挂钩子追踪 `setInfection` 的绝对值调用，或从 `Hero.refreshInfectionHud` 反推）。
3. **P2 换装会写全局 HUD**：独立模式下 P2 自己换装备仍会走游戏原版
   `Hero.onEquipedItemsChange`，其中 `updateHUD` 会刷新**全局**技能槽（也就是 P1 的 HUD）。
   本轮未处理——P1 的 HUD 会在 P2 换装后短暂显示 P2 的技能，直到 P1 自己也换装。下一步可
   在独立模式下把那个 `ref` 标记压掉。
4. **进游戏实测**：A3 / B2 / B4 之后都还没有在 Dead Cells 里验证过；构建通过只代表能编译。