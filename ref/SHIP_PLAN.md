# 舰船子系统：四路调研综合裁决 + 可执行方案

---

## 0. 先裁决：四路调研之间的 6 处矛盾

| # | 矛盾 | 采信 | 理由 |
|---|---|---|---|
| 1 | **`.jbp_patch` 是否真的死路**。`catalog` 说三条路全死、槽位只能 Harmony；`slots` + `hullswap` 独立反编译出 **PatchType 0 (Replace)** 是另一条完全不同的管线 | **采信 slots + hullswap** | 两路各自从 `RogueTrader.GameCore.dll` 反编译出同一套 `OwlcatModificationBlueprintPatcher.ApplyPatch` → `JObject.Merge` → `Json.Serializer` 往返，细节（`_#Entries` / `_#ArrayMergeSettings` / 文件名不拼扩展名 / Replace 压掉 Edit）完全吻合。上一轮"全死"的结论**只对 PatchType 1 成立**，结论应改成"换 PatchType 0"而不是"上 Harmony" |
| 2 | **"不能加第 3 个 Prow"** | **采信 slots：这条是错的** | `ShipUpgradeVm.cs:163-166` 的三元只做 Prow1/Prow2 命名，无数量上限。真限制是 `StarshipProwLimiter.cs:23-34`（同 WeaponType 的其它 Prow 槽开火后 Charges 清 0）——是机制浪费不是崩溃 |
| 3 | **"5 槽是全局硬约束"** | **采信 slots 的收窄版** | `ShipUpgradeVm.cs:143` 明确只取 `Game.Instance.Player.PlayerShip`。`PlayerWingman_Starship` 只有 2 槽、0 个 Arsenal，原版跑得好好的 ⇒ **5 槽下限与 2-Arsenal 只约束玩家主舰**，僚舰/AI 舰不受限 |
| 4 | **AbilitySettings 未登记 = 软降权 还是 硬禁用** | **采信 shipbrain：硬禁用** | `catalog` 只读到 `GetAbilityValue` 返回 0 就下了"软"的结论；`shipbrain` 读到消费端 `TaskNodeFindWhenToCastAbility.cs:79-82` 的 `if (num == 0) return;` ——该技能根本不进候选集。等价于步兵的 `m_UseOnlyListedAbilities=true`，且**常开无开关** |
| 5 | **"直接把 LaunchWingman 挂给玩家舰就行"** | **采信 wingman：不能照抄** | `catalog` / `shipbrain` 都推荐直接挂，但都没查 `GameOverController.cs:43-47` 与 `Player.cs:1307-1319`。`PlayerWingman_Starship` 的 `IsSoftUnit=false` 且**零回收链** ⇒ 会进 `AllStarships`/`party.json` 累积。**但**（见下）wingman 把判负问题说得比实际严重 |
| 6 | **判负是否真的会被僚舰卡住** | **两边都不完整，我的裁决：大概率自解，但必须实测** | `SummonedUnitsController.cs:27-38` 在召唤者失去意识时把召唤物 `MarkedForDeath`，且 `wingman` 自己确认基类不过滤舰船。玩家舰被击毁 ⇒ 下一 tick 僚舰被标记死亡 ⇒ 判负条件随即满足。所以「玩家舰爆了却不判负」很可能只延迟 1 帧而非永久卡死。**幽灵船问题是真的、判负问题是可疑待验** |

还有一处**数据冲突必须先解**：`LaunchWingman_LaunchAbility` 的 guid，`catalog` 的推荐段写 `28bb899d31334a77bf1c723f131028c3`，但它自己的蓝图表和 `shipbrain`、`wingman` 三处都写 `28bb899d01334a77bf1c723f131028c3`（第 9 位是 `0` 不是 `3`）。**以 `...d01334a77...` 为准**，但落地前必须用 ToyBox 蓝图浏览器搜一次确认——写错的症状是完全静默。

---

## 1. 优先级表

| | ① 换船模（视觉） | ② 扩武器槽（重分配） | ③ AI 僚舰 |
|---|---|---|---|
| **可行性** | 高。原版自己就在做 | 中高。5 槽内重分配安全；**扩到 6 槽不可行** | 中。机制齐全但生命周期要自己兜 |
| **实现路径** | 运行时 `PartUnitViewSettings.SetCustomPrefabGuid(vanilla_guid)`，不碰蓝图 | **PatchType 0** 数据补丁改 `HullSlots.Weapons`（不是 Harmony，不是 `.jbp_patch`） | Harmony：调 public static `SpawnStarship` + 自写回收 |
| **最大风险** | 自定义 prefab 的 bundle 不在 `PartHoldPrefabBundle` 的 hold 范围内（`PartHoldPrefabBundle.cs:30` 只 hold `Blueprint.Prefab.AssetId`），理论上可能被卸载 → 船消失 | 槽位实例进存档且**不可逆**（`HullSlots.WeaponSlots` 是 `[JsonProperty]`，`PrePostLoad` 不重读蓝图）；只对新建船生效 | 幽灵船累积进 `party.json`；`Player.cs:480` 的 `PlayerShip` 回退可能抓到僚舰 |
| **工作量** | 半天（含验证） | 1 天（0.5 天验管线 + 0.5 天做 + 新档实测） | 3–5 天（生命周期反复实测是大头） |
| **验收标准** | 三艘船模来回切换，改装界面大船图与 6 个岗位图标不丢；星系图上也换了（见未验项 G） | 改装界面 5 格全绑定、无异常；四门舷炮都能装卸；`PlayerShipBigPicture` 不为空 | 连打 5 场后 `Player.AllStarships.Count` 回到基线；`party.json` 体积不增长；玩家舰被击毁→判负正常；带僚舰存档禁用 mod 后仍能读档 |
| **建议顺序** | **第 2 步**（低风险、见效快、验证 PatchType 0 之外的另一条通道） | **第 1 步**（正面回应用户诉求、纯数据、可回滚） | **第 3 步**（价值最高但生命周期最脏，等前两步把工具链踩熟） |

> 明确一点：**"扩武器槽"实际交付的是"重分配"不是"扩容"**。用户直觉"大型舰不止一门"——`catalog` 用引用计数法测了 8 艘大舰（Sword=5 / Heartless=5 两个真值校准通过），巡洋舰/大巡洋舰是 **5–6 槽，格局是"每舷 2 门 + 1-2 门矛/背炮"**。所以正确的说法是：**不是总数暴涨，而是每舷 2 门而非 1 门**，而这个在 5 槽内做得到。

---

## 2. 立即可做的第一步（可直接照抄）

### Step 0 — 20 分钟验通 PatchType 0 管线（最小成本，强烈建议先做）

整条方案的地基是"PatchType 0 真的能用"。这一步不改任何游戏性，只改一个数字，用 ToyBox 蓝图浏览器看结果。

**文件 1**：`<Mod>/OwlcatModificationSettings.json`
```json
{
  "BlueprintPatches": [
    {
      "Guid": "1793b8c1fd824805908bee8ed0e95041",
      "Filename": "Probe_Sword.json",
      "PatchType": 0
    }
  ]
}
```
> `PatchType` **必须是数字 0**——整个 settings 走 Unity `JsonUtility.FromJson`（`OwlcatModification.LoadJson`），写字符串 `"Replace"` 解析不出来。
> PatchType 0 的 `Filename` **按原样使用、不拼扩展名**（PatchType 1 才拼 `.jbp_patch`），所以必须写全名。

**文件 2**：`<Mod>/Blueprints/Probe_Sword.json`
```json
{
  "HullIntegrity": 121
}
```
> 补丁的**根节点 == `.jbp` 里 `"Data"` 节点的内容**（`ApplyPatch` 取的是 `jObject["Data"]`）。不要再套一层 `{"Data":{...}}`。

**验收**：进游戏 → ToyBox 蓝图浏览器搜 `SwordClassFrigatePlayer_Starship` → `HullIntegrity` 显示 121。同时看日志里的 `PFLog.Mods`，有 `Patch path is:` / `Failed patch blueprint` 关键字。
- 显示 121 ⇒ 管线通，下面全部可做。
- 显示 120 且日志无异常 ⇒ 字段名或路径错了，回到 settings 排查。
- 老存档里船的血量上限**不会**变（`ModifiableValue.m_BaseValue` 是 `[JsonProperty]`，属性已烘焙进存档）——这是预期行为，不是失败。

---

### Step 1 — 武器槽重分配（PatchType 0）

**`<Mod>/Blueprints/Sword_Slots.json`**
```json
{
  "_#ArrayMergeSettings": "Replace",
  "HullSlots": {
    "Weapons": [
      { "Type": "Prow",      "Weapon": "!bp_be242f51ce154c2ca3f80bce46ddde1c", "OffsetFromProw": 0, "Width": 0 },
      { "Type": "Port",      "Weapon": "!bp_7bdc12cf63124a86b41cc7ca929d015d", "OffsetFromProw": 0, "Width": 0 },
      { "Type": "Starboard", "Weapon": "!bp_7bdc12cf63124a86b41cc7ca929d015d", "OffsetFromProw": 0, "Width": 0 },
      { "Type": "Port",      "Weapon": "!bp_7bdc12cf63124a86b41cc7ca929d015d", "OffsetFromProw": 2, "Width": 0 },
      { "Type": "Starboard", "Weapon": "!bp_7bdc12cf63124a86b41cc7ca929d015d", "OffsetFromProw": 2, "Width": 0 }
    ]
  }
}
```
settings 里追加：
```json
{ "Guid": "1793b8c1fd824805908bee8ed0e95041", "Filename": "Sword_Slots.json", "PatchType": 0 }
```

**每一处的依据（都可回查）**：

- **不写 `$type`**。`Kingmaker.Blueprints.JsonSystem.Json.Settings` 的 `TypeNameHandling = Auto`，`WeaponSlotData` 声明类型==实际类型 ⇒ 根本不调 `GuidClassBinder.BindToName`。原版 `.jbp` 里就是这个样子（`D:\RT_RetinueMod\ref\rt_probe\extracted\WhRtModificationTemplate\Blueprints\Spacecombat\Units\Player\SwordClassFrigatePlayer_Starship.jbp:216-261`，一手数据）。这正是绕开 `WeaponSlotData` 无 `[TypeId]` 的关键。
- **`_#ArrayMergeSettings` 必须是 `HullSlots` 的兄弟（entry 根）**，塞进 `HullSlots` 里面不会被 `ExtractMergeArraySettings` Remove、也不生效。写 `"Replace"` 其实等于不写（任何未识别值都落 Replace），留着自文档。
- **`HullSlots` 是对象会递归 merge**，所以 `m_PlasmaDrives` / `m_Arsenals` / `m_VoidShieldGenerator` 全部原样保留。**千万别碰 `m_Arsenals`**：`Warhammer.SpaceCombat.StarshipLogic.Equipment\HullSlots.cs:231-238` 的 `TryFixArsenalSlots` 是 `if (Arsenals.Count != 2) { CreateArsenals(...); }` 而 `CreateArsenals` 是**追加**——蓝图 `m_Arsenals` 长度不等于 2 会导致每次读档 Arsenal 无限增殖（3→6→9…）。原版写的是 `["!bp_b7e3298c...", null]`，允许 null 占位但长度必须是 2。
- **武器不能乱放**。`LanceLong1`(be242f51) 与 `LauncherBasic1`(af76b205) 的 `AllowedSlots` 都只有 `["Prow"]`。塞进 Port 槽的后果是 `ItemSlot.InsertItem`（`Kingmaker.Items.Slots\ItemSlot.cs:220-226`）**只打 error log 然后 return**——槽空着、枪掉进货舱，又一个静默失败。四门舷炮统一用 `MacroKinetic1`(7bdc12cf)，其 `AllowedSlots = ["Port","Starboard"]`，一个 guid 通吃两侧。
- **`OffsetFromProw` 分前后段**：原版 `Player_StarshipEL8_Preset` 的 Dorsal 槽就用了 `OffsetFromProw=1`，证明该字段在用。`Width=0` = 覆盖全舰长（`WeaponSlotData` 的 Tooltip 原文）。设 0/2 只是让两组舷炮在视觉上分前后，**非零 Width 没有原版先例，别用**。
- **绝对不要加 Keel / None 槽**。`Kingmaker.Code.UI.MVVM.VM.ShipCustomization\ShipUpgradeVm.cs:159-180` 的 switch 只有 Prow/Port/Starboard/Dorsal 四个 case，Keel/None 被跳过不 `Weapons.Add`，但每个 case 里写的是 `m_AllSlots.Add(Weapons[j])`——用 `WeaponSlots` 的下标 j 索引 `Weapons`。一旦前面漏加过一个，后续任何有效槽当场 `ArgumentOutOfRangeException`。（细节修正：Keel 排在**最末位**不会崩，但那样 `Weapons.Count=4` 又会在 `ShipUpgradePCView.cs:81-84` 越界。两条路都死。）
- **总数必须恰好 5**。真实不变式是 `ViewModel.Weapons.Count >= m_WeaponSlots.Length`，上界是 prefab 烘焙值（`ShipUpgradeBaseView.cs:47-48` 的 `[SerializeField]`），源码读不到；三艘首发船全是 5 槽是强旁证。多出的第 6 槽在改装界面绑不到（但战斗面板 `ShipWeaponsPanelVM.cs:110` 遍历全部 `WeaponSlots`，会并进对应弧位组——即"看不见但能用"）。

**如果要覆盖另两艘首发船**，各下一条同构补丁（它们各自 `m_Overrides` 里都列了 `"HullSlots.Weapons"`，是独立展平蓝图，改 Sword 不会带过去）：
- Falchion `4e4481bae463473ebc4c3f7b10de9403`
- Firestorm `2d9014f545b143418cb42cbf64dfd74f`

---

### Step 2 — 换船模（不需要任何补丁）

```csharp
// 只换视觉，不换实体。不碰 SetMainStarship，不丢装备/等级/船员。
var ship = Game.Instance.Player.PlayerShip;                 // StarshipEntity
ship.ViewSettings.SetCustomPrefabGuid("31da3f04de39e5446b16641deb3be42d"); // Firestorm

// 建议同时 hold 住 bundle，防止被卸载（见风险）
ResourcesLibrary.TryGetResource<UnitEntityView>("31da3f04de39e5446b16641deb3be42d",
                                                ignorePreloadWarning: true, hold: true);
```

已核实的 prefab AssetId（全部从真实 `.jbp` 读出）：

| 船 | prefab AssetId |
|---|---|
| Sword | `a6bcda106bf8fd44da4286ee04a3ad8f` |
| Falchion | `26e3688a99a9eed44baa2e19e16be1a4` |
| Firestorm | `31da3f04de39e5446b16641deb3be42d` |
| 海盗僚舰 | `ba5028dcf8d5bb14e81d2513d2682597` |
| 星系图舰 | `6f3f035a080710949bd40b7a1af533eb` |

要点：
- `PartUnitViewSettings.PrefabGuid` getter（`D:\RT_RetinueMod\ref\rt_probe\dec\Kingmaker.UnitLogic.Parts\PartUnitViewSettings.cs:33-47`）优先返回 `m_CustomPrefabGuid`，否则回落 `Blueprint.Prefab.AssetId`。`BaseUnitEntity.CreateView`（`...\BaseUnitEntity.cs:632`）唯一入口就是 `ViewSettings.Instantiate()` ⇒ 一定被采纳。
- **只填原版 guid**。`m_CustomPrefabGuid` 是 `[JsonProperty]` 会进存档；填原版 guid ⇒ 卸载 mod 后模型仍然解析得到（只是视觉不还原）。填 mod 自带 bundle 的 guid ⇒ 卸载后 `TryGetResource` 返回 null → `CreateView` 返回 null → `Entity.AttachToViewOnLoad` 把 `IsInGame = false`，**船整体下线**。
- 生效时机：走一次区域切换（进星系图/太空战）自然重建视图。不要一上来就 `DetachView + AttachToViewOnLoad`（`DetachView` 只调 `View.DetachFromData()` 不销毁 GameObject，可能留孤儿）。

---

## 3. AI 僚舰完整设计

### 3.1 CombatGroup 怎么设 —— **什么都不要设**

这是三路调研唯一完全一致的结论，也是步兵那个坑的正解。

`D:\RT_RetinueMod\ref\rt_probe\dec\Kingmaker.UnitLogic.Mechanics.Actions\WarhammerContextActionSpawnChildStarship.cs:79-83` 已经写死：
```
baseUnitEntity.CombatGroup.Id = caster.CombatGroup.Id;
```
`UnitGroupsController.GetGroup(id)`（`...\Kingmaker.Controllers.Units\UnitGroupsController.cs:24-38`）按 id 字符串返回**共享**的 `UnitGroup` 实例，`PartCombatGroup.Memory => Group.Memory`。AI 的敌人列表正是 `Unit.CombatGroup.Memory.Enemies`（`Kingmaker.AI\DecisionContext.cs:354`，`SpaceCombatDecisionContext` 未覆写 `InitializeEnemies`）。⇒ 继承 Id = 共享 Memory = 僚舰开局就看得见敌人。

**绝对不要自定义 Id**：`PartCombatGroup.cs:50-65` 的 setter 会先 `Drop()` 再赋值（新组记忆为空 = 步兵那个"整场不动"），且 `m_Id` 是 `[JsonProperty]`（:27-28）会写进存档。玩家阵营的默认 Id 是字面量 `"<directly-controllable-unit>"`。

**一条额外注意**：`DecisionContext.cs:362` 对玩家阵营 AI 多一道门——目标必须满足 `unit.CombatGroup.Memory.HasPlayerCharacterInMemory()` 否则 `continue`。正常空战满足；脚本强塞的敌人可能不满足，症状是"僚舰对某个敌人视而不见"。**别把这个误诊成组记忆坑**，成因不同。

### 3.2 Brain 用哪个 —— **用被召唤舰蓝图自带的，一个字都别改**

硬约束：必须是 `BlueprintStarshipBrain`。给舰船挂地面 `BlueprintBrain` 会同时踩两个坑：
- `Warhammer.SpaceCombat.AI.BehaviourTrees\TaskNodeFindBestTrajectory.cs:144-147` 对非 `BlueprintStarshipBrain` 一律 `return 0f`，而 `maxScore` 初值 0f + 判据严格大于（:101）⇒ `bestPathNode` 恒 null ⇒ 整场不动
- `TaskNodeTryStarshipExtraMeasures.cs:76` 是**无保护强转** `(...Blueprint as BlueprintStarshipBrain).ExtraMeasures` ⇒ NullReferenceException

**存档红线**：`PartUnitBrain.Blueprint` 带 `[JsonProperty]`（`Kingmaker.UnitLogic\PartUnitBrain.cs:62-63`）。虽然 `OnAttachOrPostLoad`（:298-303）会在读档后重置为 `DefaultBrain`，但**反序列化发生在 PostLoad 之前**，`BlueprintConverter.ReadJson` 解析不到已经抛了 ⇒ **该重置救不了你**。只能传 vanilla guid。

选型：

| 蓝图 | brain | AbilitySettings | 评价 |
|---|---|---|---|
| `PlayerWingman_Starship` `0c893e03a8a34415a4eeb59d7fc2e34a` | `c534f2c31ac94ac8adb0409e42674a16` | **1 条**，死绑 `MacroPlasma1_Weapon`(9a8850f0)，两个槽同一把枪 1:1 对应 | Hull 100 / Frigate_1x2 / Speed 12 / Init 45。**推荐** |
| `DLC2_Heartless_Starship` `7c87442966644058b67e8be10c56b861` | `10c355ab29e440a9bc90db9184ed48e6` | 3 条（2 Equipment + 1 Ability） | Hull 240、5 槽，但 **`StarshipSpeed=0`**（DLC2 里是剧本固定站桩）。要用得先补丁 `StarshipSpeed`，见未验项 H |

**别给僚舰换武器**。`TaskNodeFindWhenToCastAbility.cs:74-82`：
```
int num = 0; foreach (...) num = Math.Max(num, cache.GetValue(item, ability));
if (num == 0) return;    // 该技能永不进候选集
```
未登记进 `brain.AbilitySettings` 的武器，AI **永远不开火**。匹配粒度见 `Kingmaker.AI\AbilitySourceWrapper.cs:57-60`：`Type==Equipment` 时取 `Equipment.Abilities.ToList()` ⇒ 登记武器蓝图即覆盖其全部技能。所以**复用同一把枪自动被覆盖，换枪就得同步改 brain**。

想要更强的僚舰，正确做法是走官方扩展点 `StarshipAddFeaturesOnSummon`（`Kingmaker.Designers.Mechanics.Facts\StarshipAddFeaturesOnSummon.cs:48-58`，TypeId `6b4b35d97564c3a4f900256bf0200a09`）——挂在**召唤者**身上，按被召唤舰蓝图过滤后 `AddFact`。这样不动僚舰本体、不动 brain。

### 3.3 怎么不破坏战败判定

`Kingmaker.Controllers.Combat\GameOverController.cs:43-47` 原文：
```
AllStarships.Where(x => x.GetStarshipNavigationOptional() != null && !nav.IsSoftUnit)
            .All(u => !u.LifeState.IsConscious)
```
即：**`Player.AllStarships` 中所有非 soft 舰都失去意识 = 判负**。`PlayerWingman_Starship` 的 `IsSoftUnit = false`。

三个选项，代价递减但确定性也递减：

**选项 α（推荐，先验证）：什么都不做，靠原版 `SummonedUnitsController` 兜底。**
`Kingmaker.Controllers.Units\SummonedUnitsController.cs:27-38` 在召唤者失去意识时把召唤物 `MarkedForDeath`，且基类 `BaseUnitController` 不过滤舰船（只跳过 `IsDead`）。⇒ 玩家舰爆 → 下一 tick 僚舰被标记死亡 → 判负条件随即成立。**代价：延迟可能是 1 帧也可能是 1 回合，必须实测。**

**选项 β：把 `IsSoftUnit` 补丁成 true。**
判负 100% 正确，但 `wingman` 全量 grep 出约 15 处连带语义：不给经验（`UnitLifeController.cs:173`）、不挡路（`CustomGridNodeController.cs:118/131`）、鱼雷不能直击（`RuleStarshipCalculateHitChances.cs:48`）、最小伤害变 1（`RuleRollDamage.cs:162-163`）、不能被跳帮（`AbilityCustomStarshipBoardingTeam.cs:173`）、敌方 AI 选航线时无视（`TaskNodeFindBestTrajectory.cs:185`）、难度修正跳过、检视面板显示"数量"而非 HP。**代价：得到的是"战机蜂群"不是"护卫舰"，与用户诉求实质不符。**

**选项 γ：把实体从 `CrossSceneState` 转挂到 `LoadedAreaState.MainState`。**
`Player.UpdateCharacterLists`（`Kingmaker\Player.cs:1283-1323`）只遍历 `PartyCharacters` 与 `CrossSceneState.AllEntityData`，**从不扫描 area state** ⇒ 不进 `AllStarships` ⇒ 判负 / `party.json` / `PlayerShip` 回退三个问题一并消失，且保留 `IsSoftUnit=false` 的真舰手感。唯一先例 `MeteorStreamEntity.cs:115`（且 meteor 不是 `BaseUnitEntity`）。**代价：未验证，且转挂顺序必须先 Remove 后 Add**——`SceneEntitiesState.AddEntityData:111-117` 只 `SetHoldingState` 不从旧列表移除，且遇重复 `UniqueId` 只报错返回（又一个静默失败）。

**我的建议**：先做 α，实测通过就到此为止；不通过再上 γ。β 只在用户明确接受"战机蜂群"语义时才用。

### 3.4 怎么回收避免幽灵船

**这是真问题，且不会自解。** `LaunchWingman_LaunchAbility` 的 `AfterSpawn.Actions` 为空、`PlayerWingman_Starship` 的 `Components` 为空 ⇒ **零回收链**。战斗正常结束时僚舰活着，没有任何东西销毁它，于是留在 `CrossSceneState`（= `party.json`，`SaveManager.cs:1089`）里跨战斗累积。

好消息：`SpawnStarship:89` 自动挂了 `UnitPartSummonedMonster`，而这个 Part 是**承重的**——`Kingmaker.Controllers\EntityDestructionController.cs:121-138`：player 阵营且**没有**这个 Part 的单位会走 `TryUnrecruit` 并直接 `return`（:130 "Cancel unit's destruction"），根本销毁不掉；有这个 Part 才执行到 :135 `HoldingState?.RemoveEntityData(entity)` 真正移出 `CrossSceneState`。

所以只缺"何时销毁"，代码自己写：

```csharp
static class WingmanRegistry
{
    // 不持久化。仅本次会话追踪。
    static readonly HashSet<string> Spawned = new HashSet<string>();

    public static BaseUnitEntity Launch(BlueprintStarship bp, Vector3 pos)
    {
        var caster = Game.Instance.Player.PlayerShip;
        // 白拿：CombatGroup.Id 继承、Faction 复制、UnitPartSummonedMonster、先攻插入
        var e = WarhammerContextActionSpawnChildStarship
                    .SpawnStarship(bp, pos, null, caster, actBeforeSummoner: true);
        if (e == null) return null;                    // 落点被占用时 SpawnStarship:64-68 返回 null
        Spawned.Add(e.UniqueId);
        return e;
    }

    /// 战斗结束 / 读档后 各调一次。也是给"上次崩在战斗中"的用户自愈。
    public static void CleanupAll()
    {
        var player = Game.Instance.Player;
        var ship   = player.PlayerShip;
        foreach (var e in player.AllStarships.ToList())
        {
            if (e == ship) continue;
            if (e.GetOptional<UnitPartSummonedMonster>() == null) continue;   // 没这个 Part 销毁不掉
            bool ours = Spawned.Contains(e.UniqueId)
                     || e.Blueprint.AssetGuid == "0c893e03a8a34415a4eeb59d7fc2e34a";
            if (!ours) continue;
            e.MarkedForDeath = true;
            EntityDestroyer.Destroy(e);     // 走到 PerformDestroy:135 → RemoveEntityData
        }
        Spawned.Clear();
    }
}
```

> 两处 API 需要落地时确认签名（成本：grep `dec` 一次）：`EntityDestroyer.Destroy` 的确切重载、`MarkedForDeath` 是属性还是方法。`DestroyStarship.cs:26-49`（TypeId `e60b053256f5cf545b5075ee4c00f616`）是原版所有回收链的终结动作，照它写就对了——它做的正是 `MarkedForDeath` + 1 秒后 `EntityDestroyer.Destroy`，另外还能顺手抄 `NoLog→SilentDeathUnitPart` / `NoExp→GiveExperienceOnDeath=false`（不然僚舰死了会给玩家发经验和死亡日志）。

**"战斗结束"事件挂在哪里我没查**。最小成本查法：
```
grep -rn "interface I.*CombatHandler\|HandleCombatEnd\|BuffEndCondition" D:\RT_RetinueMod\ref\rt_probe\dec\
```
`SummonedTorpedoesBuff` 用的 `BuffEndCondition=CombatEnd` 证明这个概念存在，顺着它的消费方就能找到事件接口。

### 3.5 怎么把技能给玩家舰

两条路，看你要不要支持老存档：

**A. 纯数据（只对新游戏生效，零 Harmony）** —— 单独一个 entry，因为 `_#ArrayMergeSettings` 是 entry 级不是节点级：
```json
{
  "_#ArrayMergeSettings": "Concat",
  "m_AddFacts": ["!bp_28bb899d01334a77bf1c723f131028c3"]
}
```
（`BlueprintAbility : BlueprintUnitFact`，`m_AddFacts` 收得下。原版 `PlayerWingman_Starship` 的 `m_AddFacts` 就是这么装 `StarshipAllyUnitMark` 的。）

**B. 运行时（老存档也生效）**：
```csharp
Game.Instance.Player.PlayerShip.AddFact(
    Utilities.GetBlueprint<BlueprintAbility>("28bb899d01334a77bf1c723f131028c3"));
```
Facts 是 `[JsonProperty]`，该 AssetId 进存档——但**是 vanilla AssetId**，卸载 mod 后 `BlueprintConverter.ReadJson` 照样解析得到，存档正常打开。符合硬约束。

> `LaunchWingman_LaunchAbility` 上**没有** `StarshipSummonedUnitsLimit` 组件，`ActionPointCost=0`、`CooldownRounds=0`。原样挂上去玩家可以一回合刷无限僚舰。要限量，用现成的 `StarshipSummonedUnitsLimit`（`Kingmaker.UnitLogic.Abilities.Components.CasterCheckers\StarshipSummonedUnitsLimit.cs:28-38`，按"蓝图相同且 Summoner==caster"计数）通过 PatchType 0 加到该 ability 的 Components 里，或者补丁 `ActionPointCost` / `CooldownRounds`。

---

## 4. 存档安全结论

| 改动 | 存档足迹 | 卸载 mod 后 |
|---|---|---|
| **PatchType 0 蓝图补丁本体** | **零**。补丁只作用于内存里的蓝图实例（`BlueprintsCache.Load` 里 `OnResourceLoaded` 之后），不写盘 | 蓝图立即还原 |
| **武器槽重分配（间接）** | **有，且不可逆**。`PartStarshipHull.HullSlots` 是 `[JsonProperty]`（`PartStarshipHull.cs:35`），`HullSlots.WeaponSlots` 也是（`HullSlots.cs:54-55`），而 `PrePostLoad`（:187-197）**不重读蓝图**。所以：① 补丁只对**新建的船**生效，老存档纹丝不动；② 用补丁开的新档，5 个槽的 Type 已落盘 | **存档能正常打开**（`WeaponSlot` 是原版类型、引用的全是原版武器 guid，`BlueprintConverter` 解析得到），**但槽位布局永久保持修改后的样子**。四门 `MacroKinetic1` 还在，只是玩家换不回原来的矛/鱼雷布局 |
| **换船模 `SetCustomPrefabGuid`** | 一个普通 string（`m_CustomPrefabGuid`，`PartUnitViewSettings.cs:24-26`），挂在原版 part 上 | **存档正常打开，船模保持修改后的样子**（因为填的是原版资源 guid，照样解析）。想还原就 `SetCustomPrefabGuid(null)` 或换回原 guid |
| **AI 僚舰（战斗中不存档）** | **零** | 无影响 |
| **AI 僚舰（战斗中存了档）** | 僚舰实体进 `CrossSceneState` = `party.json` | **存档能打开**（原版蓝图 + 原版 Part），但会多一艘游荡的友方舰，且 `Player.cs:480` 的 `PlayerShip` getter 在 `m_PlayerShip.Entity` 为 null 时会 `FirstItem` 抓 `CrossSceneState` 第一条 `StarshipEntity` —— **可能抓到僚舰**。`Player.cs:1088` 空战 XP 也会给它发一份 |
| **`ReapplyStats` 组件（若用）** | 组件通过 `RequestSavableData<ComponentData>` 把 `Version` 存进存档 | 变成孤儿数据条目。原版组件的常规机制，预计无害，未实测 |

**一句话**：三件事**都不会焊死存档**（零新增蓝图 AssetId 这条硬约束全程满足），但**槽位重分配和换船模是"卸载后不还原"的单向改动**，僚舰是"卸载后留个游荡实体"。分发前必须做的往返测试：**装炮 → 存盘 → 禁用 mod → 读档**。

---

## 5. 走不通的路

| 方案 | 死因 | 出处 |
|---|---|---|
| **`.jbp_patch`(PatchType 1) 改 `HullSlots.Weapons`** | `WeaponSlotData` 无 `[TypeId]`。FieldOverrides 抛 `ArgumentException` 被末尾 try/catch 吞、ArrayPatches 类型不匹配中止、加 `$type` 让 `GuidClassBinder.BindToType` 直接 throw。**症状是静默失败** | 上一轮已证实；本轮的解法是换 PatchType 0，不是放弃 |
| **`.jbp_patch`(PatchType 1) 改 float 字段** | `BlueprintFieldOverrideOperation.Apply` 只特判 `int`/enum/`UnityEngine.Object`/`IReferenceBase`，float 落到 `field.SetValue(fieldHolder, FieldValue)`，Newtonsoft 给的是 `Double`/`Int64` → `ArgumentException` → **被吞掉**。`SpeedOnStarSystemMap` / `AiDesiredDistanceToPlayer` / `ApproachStarSystemObjectRadius` 三个字段全中 | `hullswap`，`RogueTrader.GameCore.dll` |
| **`.jbp_patch`(PatchType 1) 改 enum 传字符串** | enum 分支走 `Enum.ToObject(fieldType, "FirestormClassFrigate")` → `ArgumentException`，且该分支在 try/catch **之外**。要用 PatchType 1 改 `m_ShipType` 只能填整数 | 同上 |
| **加第 6 个武器槽** | 改装界面 `ShipUpgradePCView.cs:81-83` 循环上界是 prefab 烘焙的 `m_WeaponSlots.Length`，第 6 槽永远绑不到、玩家无法装卸。战斗中有效但只能用预设默认武器 | `catalog` + `slots` 一致 |
| **加 Keel / None 槽** | `ShipUpgradeVm.cs:159-180` 的 switch 无这两个 case，非末位插入当场 `ArgumentOutOfRangeException`，末位则总数变 4 又在 PCView 越界 | 同上 |
| **把玩家舰换成 NPC 巡洋舰** | `PlayerShipType` 是只有 `SwordClassFrigate`/`FalchionClassFrigate`/`FirestormClassFrigate` 三个值的硬枚举（`PlayerShipType.cs:3-8`），游戏在类型层面不存在玩家巡洋舰。且改装界面槽位 prefab、船内区域 `ShipAreaEnterPoint`、`Posts` 船员岗位全按护卫舰烘焙 | `catalog` |
| **中途 `SetMainStarship` 换船** | 全仓 grep 只有两个调用点：`Game.cs:2091`(LoadNewGame) 与 `SelectionStateShip.cs:45`(捏人选船)。函数体只换 `UnitPartCompanion` 和 `m_PlayerShip` 引用，**零迁移逻辑** ⇒ 装备/等级/船员全丢 | `hullswap` |
| **加同型第 3 个 Prow 提火力** | 不崩，但 `StarshipProwLimiter.cs:23-34` 在任一 Prow 武器开火后把**同 WeaponType** 的其它 Prow 槽 Charges 清 0 ⇒ 白给。换 WeaponType 才有意义 | `slots`（推翻了上一轮的"不能加"说法） |
| **给舰船挂 `PetSeparateMomentumGroup` 隔离士气** | 不需要。`MomentumController.cs:143` 太空战直接 return，:100/:113 要求 `is UnitEntity`，:220/:231 显式排除 `StarshipEntity`；且 `PartMomentum.IOwner` 只挂在 `UnitEntity` 上（`UnitEntity.cs:27`）—— 舰船根本没有 `PartMomentum`。**步兵教训 #3 在舰船侧完全不适用** | `shipbrain` + `wingman` 一致 |
| **给舰船 brain 用 `m_UseOnlyListedAbilities`** | 该字段只在 `BlueprintBrain.cs:28`，`BlueprintStarshipBrain` 是 `BlueprintBrainBase` 的另一子类、没有这个字段。**但等价的门在 `TaskNodeFindWhenToCastAbility.cs:79-82`，常开无开关** | `shipbrain` |

---

## 6. 需要你决策的取舍

### 决策 1：武器槽布局（三选一）

| | 布局 | 得到 | 失去 | 先例 |
|---|---|---|---|---|
| **A** | Prow×1 / Port×2 / Starboard×2 | 舷侧火力翻倍 + 保留艏矛 | 鱼雷发射管、背炮 | 每个元素都有先例，但这个组合没有 |
| **B** | Port×2 / Starboard×2 / Dorsal×1 | 舷侧翻倍 + 保留背炮，**完全照抄 `DLC2_Heartless_Starship`** | 艏矛、鱼雷发射管（两个 Prow 全没了） | 官方原样实现，风险最低 |
| **C** | 不动槽位，改 `BlueprintStarshipWeapon.AllowedSlots` 或直接开放最强舷炮 | 零槽位风险、零存档不可逆、`.jbp_patch` 也能改（该类带 `[TypeId("d2513f4a...")]`） | 槽数不变，仍是"每舷一门" | — |

**C 的零成本版本值得单独说**：`Pirate_LanceLong2Side_Weapon`(`3f4e4b7279e7433db5dc44012b056310`) 是全游戏唯一的舷侧矛，`AllowedSlots` 原生就含 `[Port,Starboard]` 且 `DamageInstances=7`（全游戏最强舷侧武器）。**不需要改任何字段**，只需让它进玩家的掉落/商店池（`StarshipComponentsUnlockTable` `ff8b82b9958a4e159a0e0623f731efd1` 是唯一一张组件解锁表）。如果用户的真实痛点是"舷侧火力弱"而不是"槽位少"，这条路成本几乎为零、存档足迹为零。

**我的倾向是 B**（照抄官方先例风险最低），但 A 保留艏矛更符合"护卫舰"手感。**你定。**

### 决策 2：僚舰用哪个蓝图

| | `PlayerWingman_Starship` | `DLC2_Heartless_Starship` |
|---|---|---|
| 体量 | Hull 100 / Frigate_1x2 / 2 槽 | Hull 240 / 5 槽 / Armour 7-5-5-5 |
| brain | 1 条 AbilitySettings，与 2 个同枪槽 1:1 | 3 条，多武器 AI 参考 |
| 移动 | Speed 12 / Init 45，正常 | **`StarshipSpeed=0`** —— 需要补丁才会动 |
| 召唤入口 | 现成 `LaunchWingman_LaunchAbility` | **未找到**（bbp 全包扫描零引用，推测在区域场景数据里） |
| 结论 | **推荐起步** | 想要"重护卫"再考虑，要多做 2 件事 |

### 决策 3：僚舰的判负方案

选项 α（靠 `SummonedUnitsController` 兜底，先实测）/ β（`IsSoftUnit=true`，变战机蜂群）/ γ（转挂 `MainState`，未验证）—— 见 3.3。**我建议先花 30 分钟实测 α，别一上来就上 γ。**

### 决策 4：老存档要不要生效

| | 新档生效 | 老档生效 |
|---|---|---|
| 武器槽 | PatchType 0，零代码 | 需要 Harmony 反射调私有 `HullSlots.AddWeapon` + 手动 `EquipmentSlots.Add`（`AddWeapon:154` 不做这步）+ `RefreshArsenals()`。且改现存槽的 `Type` 比加槽更麻烦 |
| 数值强化 | PatchType 0，零代码 | 需要 `ReapplyStats` 组件（TypeId `6a496c8a07bf4a05b17e9d5e3afdc886`，`ReapplyStats.cs:14-45`）或 Harmony 直接调 `ship.GetStatsContainerOptional()?.Container.ReinitializeBaseValues()`。根因是 `ModifiableValue.m_BaseValue` 是 `[JsonProperty]`（`ModifiableValue.cs:153-154`），基础属性烘焙进存档 |
| 换船模 | 运行时 API，新老通吃 | 同左 |
| 僚舰 | 运行时 `AddFact`，新老通吃 | 同左 |

**如果用户只在自己的存档上玩，"老档生效"是必须的**，那武器槽这块工作量要翻倍。**先问清楚。**

### 决策 5：PatchType 0 vs PatchType 1 的兼容代价

`OwlcatModificationsManager.OnResourceLoaded` 里 Edit 分支的守卫是 `if (obj == null && resource is SimpleBlueprint)`。**只要有任何 mod 对某 guid 出了 PatchType 0 补丁，所有 mod 对同一 guid 的 `.jbp_patch` 都被静默跳过**（Replace 之间是链式的，没问题）。ToyBox 是运行时改不走这条路，实际冲突概率低，但和其它内容 mod 抢 Sword 蓝图时会互吃。**同一蓝图上统一只用一种 PatchType。**

---

## 7. 未验证项 × 最小验证成本

按"落地前必须验"排序。

| # | 未验证的事 | 最小成本验法 |
|---|---|---|
| **A** | **PatchType 0 管线是否真的能用**（settings 字段名 / 文件名扩展名 / 补丁根节点层级）。这是整套方案的地基，两路调研都是纯反编译推导，无实机验证 | **§2 Step 0**：`HullIntegrity: 121` 探针 + ToyBox 蓝图浏览器 + `PFLog.Mods` 日志。20 分钟 |
| **B** | `LaunchWingman_LaunchAbility` 的 guid 到底是 `...d01334a77...` 还是 `...d31334a77...`（调研内部互相矛盾） | ToyBox 蓝图浏览器搜名字看 guid。2 分钟。**做任何僚舰工作之前必须先做这个** |
| **C** | `m_WeaponSlots.Length` 实际值是否为 5（prefab 烘焙，源码读不到） | 应用 §2 Step 1 的 5 槽补丁 → 新游戏 → 开改装界面。渲染出 5 格且无异常 ⇒ 是 5。**不要用"故意下 4 槽看崩不崩"来验，那是破坏性的** |
| **D** | **`UnityObjectConverter` 往返是否无损**。PatchType 0 是"序列化→合并→反序列化"，`WriteJson` 在 `AssetList.GetAssetId(obj)` 返回 null 时**静默写 null**（引用丢失不报错）。Sword 有 `PlayerShipBigPicture` + 6 个 `Post` 图标要过这条路 | 同 C 的那次新游戏：开改装界面看**大船图还在不在**，开船员岗位界面看**6 个岗位图标还在不在**。这是 PatchType 0 唯一没实证的关键环节 |
| **E** | 僚舰活着时玩家舰被击毁，判负是否正常触发（选项 α） | ToyBox 直接 spawn 一艘 `PlayerWingman_Starship`（或先做完 §3.5 的 AddFact），开战 → ToyBox 秒杀自己的玩家舰 → 看是否弹出 Game Over。30 分钟 |
| **F** | 转挂 `LoadedAreaState.MainState` 后能否正常存读档、能否跨越空战结束（选项 γ） | 先别写代码。**先读**：`grep -n "MainState\|SetHoldingState" D:\RT_RetinueMod\ref\rt_probe\dec\Kingmaker.EntitySystem\SceneEntitiesState.cs` 与 area 卸载路径，确认 area state 实体在空战结束时的处置。读完仍不确定再上手 |
| **G** | `Globalmap_Starship`(`e80a20e2a1d0495288bd6fd14dd91164`) 是不是星系图上的**独立实体**（它的 prefab `6f3f035a...` 与战斗船模不同，dec 全仓无代码引用） | 做完 §2 Step 2 换船模后，进星系图看模型换没换。没换 ⇒ 是独立实体，得连它一起处理 |
| **H** | `DLC2_Heartless_Starship` 的 `StarshipSpeed=0`（只有 `shipbrain` 一路提到），以及它的 Port×2/Starboard×2/Dorsal×1 槽位（`slots` 说该 `.jbp` 不在 extracted 集里、无法一手复核；`catalog` 靠 bbp.py 直读 pack 得出） | `python D:\RT_RetinueMod\ref\rt_probe\bbp.py` 直读 `7c87442966644058b67e8be10c56b861` 的记录，或 ToyBox 蓝图浏览器看。5 分钟 |
| **I** | `AbilitySettings` 缺失时"评分 0"是否等价"永不开火" | **已解决**，不必再验。`shipbrain` 读到了消费端 `TaskNodeFindWhenToCastAbility.cs:79-82` 的 `if (num == 0) return;`。`catalog` 的"软降权"说法是没读到消费端造成的 |
| **J** | 舰船是否走 `MomentumController` 的 `IsPlayerFaction` 分组、偷玩家士气 | **已解决**，不必再验。`shipbrain`（`MomentumController.cs:143/220/231`）与 `wingman`（`UnitEntity.cs:27` + `:100/:113`）两路独立证实舰船被结构性豁免 |
| **K** | `StarshipAllyUnitMark`(`7708c385eb3740618774e2ae848454b5`) 的内容 —— 所有己方舰（PlayerWingman / Pirate / Heartless 三系）都挂了它，可能承载"友方舰船"识别语义 | `bbp.py` 直读该 guid，或 ToyBox 看组件列表。5 分钟。**如果它是必挂的，就得确认我们 spawn 出来的僚舰有没有**（走蓝图 `m_AddFacts` 应该自带） |
| **L** | `SpawnStarship` 在落点被占用时返回 null（:64-68），而 `RunAction:53-54` 未做 null 检查直接 `entity.ToITargetWrapper()` | 走 `WingmanRegistry.Launch` 那条路时我们自己检查了 null，不受影响。但**用蓝图 ability 路径（§3.5 方案 A）时会走 `RunAction`** ⇒ 拥挤战场召唤可能 NPE。实测：把僚舰往一堆船中间召 |
| **M** | 推荐的 `ReapplyStats` 组件 JSON 形状（`hullswap` 说这块是照 `DlcCondition` 结构**构造**的，不是抄录，`Version` 字段的序列化名与默认值未经原版样本验证） | 只有决定要"老档生效"时才需要。验法：加上后看 `PFLog.Mods` 有没有反序列化报错，再看老档血量上限有没有变 |

---

## 附：最终建议的执行顺序

1. **B**（2 分钟，确认 ability guid）→ **A**（20 分钟，PatchType 0 探针）
2. 与用户敲定**决策 1**（槽位布局）与**决策 4**（要不要老档生效）
3. 下 §2 Step 1 的槽位补丁 → 新游戏 → 同时验 **C** 和 **D**
4. §2 Step 2 换船模 → 顺手验 **G**
5. **H** + **K**（10 分钟数据核查）→ **E**（判负实测）
6. 僚舰：§3.5 方案 B（运行时 AddFact）+ §3.4 的 `WingmanRegistry` 回收 → 连打 5 场验证 `AllStarships.Count` 与 `party.json` 体积
7. 最后统一做**往返测试**：装炮 → 存盘 → 禁用 mod → 读档

关键路径上唯一真正的地基是 **A**。它不通，方案 1 和 2 全部要退回 Harmony；它通了，扩武器槽从"必须 Harmony + 分发不安全"降级成"一个 JSON 文件"。
---

# 附录：离线核查勘误（2026-08-15，主线亲验）

工具：`ref/rt_probe/verify_ship.py`（直读 `blueprints-pack.bbp`，181584 条记录）
+ `extracted/` 下的明文 `.jbp`。运行方式：`py.exe verify_ship.py`
（注意：`python.exe` 在本机 shell 里连 `print(1+1)` 都崩，退出码 0xC000041D；`py.exe` 正常）。

## 已解决的待验项

**B — ability GUID**：`28bb899d01334a77bf1c723f131028c3`（第 9 位是 `0`）。
从 `cd2.txt` 索引直接读出，无需进游戏。综合报告里那个 `...d31334a77...` 是错的。
该 ability 记录 484 bytes，只引用一个蓝图：`PlayerWingman_Starship`。干净。

**H — Heartless**：一手确认（`extracted/.../DLC2_Heartless/DLC2_Heartless_Starship.jbp`）
```
HullIntegrity: 240    StarshipSpeed: 0    IsSoftUnit: false
槽位 = Port / Starboard / Port / Starboard / Dorsal
       全部 OffsetFromProw = 1, Width = 0
```
⇒ 决策 1 的选项 B（照抄 Heartless 布局）**有完整官方先例，逐字段可抄**。
⇒ 但 Heartless **不能直接当僚舰**：`StarshipSpeed=0` 属实，它在 DLC2 里是站桩剧本单位。

**K — StarshipAllyUnitMark**：记录只有 133 bytes 且**零蓝图引用** ⇒ 是个纯标记 Feature，
无逻辑负载。三系己方舰（PlayerWingman / PlayerWingmanPirate / Heartless）都在自己的
蓝图 facts 里带着它，所以走蓝图 spawn 的僚舰**自动就有**，不需要额外处理。

## 新发现：存在第二条僚舰链，且它自带回收

综合报告只讨论了 `LaunchWingman` 那条。实际上并列存在一条**发射舱**链：

```
Launcher_Wingman_Weapon (1dd14297fad5478a95ea1ce0ac384b1e)   ← 可装备的舰载武器
  └→ LauncherWingman_LaunchAbility (79ecefdb63164229aa07c5d438924d48)
       ├→ WingmanFast_PlasmaTorpedoUnit (4a7cc5e0487942e6b2ad33e148d55dd6)
       ├→ SummonedTorpedoesBuff          ← ★ BuffEndCondition=CombatEnd
       └→ MinionsDeploy_AbilityFXSettings
```

`PlayerWingmanPirate_Starship`(ca2239e8…) 装的就是这把 `Launcher_Wingman_Weapon`。

**为什么这条更值得考虑**：综合报告 §3.4 说 `LaunchWingman` 链「零回收链、幽灵船是真问题、
得自己写 WingmanRegistry」。而这条链带 `SummonedTorpedoesBuff`，正是报告自己提到的
`BuffEndCondition=CombatEnd` 机制 —— **回收可能是现成的**。
落地前要读：该 buff 的组件里到底是不是挂了销毁动作（`DestroyStarship` TypeId
`e60b053256f5cf545b5075ee4c00f616`）。若是，§3.4 那段自写回收代码可以整段省掉。

代价：`WingmanFast_PlasmaTorpedoUnit` 顾名思义是「快速鱼雷单位」，
体量多半远小于 `PlayerWingman_Starship`，更接近「战机」而非「护卫舰」。
两条链的取舍 = 「要护卫舰但自己写回收」vs「要战机但白拿回收」。

## 修正：大舰槽位数（5-6 槽）这个数字不可靠

综合报告称用「武器引用计数法」测出巡洋舰 5-6 槽，并称 Sword=5 校准通过。
但我直读 Sword 记录，它引用的 `BlueprintStarshipWeapon` 只有 **4 个不同蓝图**：
`LanceLong1 / LauncherBasic1 / MacroKinetic1 / MacroLight1`
—— 而它实际是 **5 槽**，因为 `MacroKinetic1` 同时占了 Port 和 Starboard 两个槽。

**同一把武器重复出现在多个槽位时，按引用去重计数必然少算。**
所以那批巡洋舰的 5-6 这个数，最多只能当**下界**看待，不能当实测值。
真实槽位数很可能更高 —— 这个方向反而更支持用户「大型舰不止一门」的直觉，
但在拿到巡洋舰的明文 `.jbp` 之前，**不要把 5-6 当结论引用**。

（巡洋舰不在已解出的 412 个明文 `.jbp` 里。`.jbp` 里槽位是明文 `"Type": "Port"`，
而 pack 记录里是二进制枚举 —— 这就是为什么 Heartless 能一手读到而巡洋舰不能。
要坐实这条，得先把 pack 记录的槽位段解出来，或扩大 `.jbp` 解包范围。）

## 未动的待验项

A（PatchType 0 管线）、C（prefab 槽位数=5）、D（UnityObject 往返是否无损）、
E（僚舰在场时判负）、F（转挂 MainState）、G（星系图船模）、L（拥挤落点 NPE）、
M（ReapplyStats 形状）—— 全部需要进游戏，等实机测试。
