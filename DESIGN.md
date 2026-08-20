# RT 舰上 AI 卫队 mod —— 设计文档

版本：2026-08-14
目标游戏：Warhammer 40,000: Rogue Trader v1.6.1 + DLC3（buildid 24257885，Unity 6000.0.64f1）
共存要求：UnityModManager + ToyBox 1.7.34（用户核心 mod，不可移除）

本文档所有结论均来自对 Code.dll / RogueTrader.GameCore.dll / 0Harmony.dll 的实际反编译，
以及一次实机验证（ShipPatchTest）。标注「待实测」的是尚未在游戏内确认的假设。

---

## 一、功能目标

1. 在飞船上招募 AI 控制的卫队
2. 卫队自行成长（升级）
3. 可换装
4. 可自选出战，由 AI 控制，作为己方单位参战
5. 卫队池的深度与广度和不同派系好感度挂钩

---

## 二、核心安全原则

### 2.1 坏档的唯一机制

```csharp
// BlueprintConverter.ReadJson
return ResourcesLibrary.TryGetBlueprint(text)
    ?? throw new JsonSerializationException("Failed to load blueprint by guid " + text);
```

配合 player.json / party.json 的反序列化**没有 try/catch**（ThreadedGameLoader.cs:119）：
**一个 mod 自有的 AssetId 进了这两个文件，存档就永久打不开。**

FleetGuard（新增 7 个 BlueprintStarship）与 BodyGuard（新增 38 个蓝图）坏档，根因都是这个。
BodyGuard 作者在 Nexus bug #1051272 亲口承认。

反过来：**只要存档里不出现 mod 的 AssetId，卸载在结构上不可能坏档。**

重要澄清：决定安全的是**蓝图**，不是单位状态。
MechanicEntity.OriginalBlueprint / Blueprint 都是 [JsonProperty]，
单位的 BlueprintUnit AssetId 无论什么 CompanionState 都会进 party.json。
用原版蓝图 = 安全；挂任何 mod 蓝图 = 毁档。

### 2.2 五条红线

| # | 红线 | 落实方式 |
|---|---|---|
| 1 | 存档里零 mod AssetId | 零新增 .jbp，只用原版蓝图 |
| 2 | 存档里零 mod 程序集名 | 不定义任何带 [JsonProperty] 的类型；不加自定义 EntityPart |
| 3 | 持久化只走一个 string | InGameSettings.List["<key>"] = 一段 JSON 字符串 |
| 4 | 不镜像游戏侧已有数值 | XP/等级/career rank/装备一律实时读或派生，不存 |
| 5 | write-through | 状态一变立刻写；没有 pre-save 事件可用 |

### 2.3 红线 3 的致命细节：数字必须字符串化

```csharp
// JsonInt32AndSingleConverter
public override bool CanConvert(Type t) { return t == typeof(object); }  // Dictionary<string,object> 全命中
JsonToken.Integer => Convert.ToInt32(reader.Value),   // > Int32.MaxValue → OverflowException
```

外面没有 try/catch。**任何毫秒时间戳 / Ticks / 64 位 ID 写成 JSON 数字 = 读档硬失败。**
正确姿势（ToyBox 和 RTAutoBuilder 都这么做）：值 = 一个内含转义 JSON 的字符串，
字符串走 serializer.Deserialize 分支，完全绕开这个转换器。

---

## 三、架构总览

| 模块 | 方案 | 新增蓝图 | 存档足迹 |
|---|---|---|---|
| 卫兵生成 | 原版 BlueprintUnit + Faction.Set(Player) + 自定义 CombatGroup.Id | 零 | 原版 AssetId（安全） |
| 跟随移动 | UnitPartFollowUnit（原版系统） | 零 | **零**（不持久化） |
| 入口交互 | AddInteraction 挂原版 NPC | 零 | **零**（无 JsonProperty） |
| 成长 | AdvanceExperienceTo(RT.Exp x ratio) | 零 | 零（不存数字） |
| 名册持久化 | InGameSettings.List 一个 JSON 字符串 | 零 | 一个字符串键 |
| 面板 UI | 自建（UMM 面板 / 自绘） | 零 | 零 |
| 派系门槛 | ReputationHelper 只读 | 零 | 零 |

生命周期：
```
OnAreaLoadingComplete → 清扫旧卫兵 → 按名册在主角旁 spawn → 挂跟随 → 对齐经验 → 消费等级 → 装配
OnAreaBeginUnloading  → 全部销毁
```

卫队**永不进 party.json**（CrossSceneState），只出现在 area state ——
唯一有 catch 兜底的桶（AreaDataStash.cs:130-134）。

---

## 四、各模块实现要点

### 4.1 生成 AI 己方单位（零蓝图）

```csharp
var bp = (BlueprintUnit)ResourcesLibrary.TryGetBlueprint(vanillaAssetId);
var u  = Game.Instance.EntitySpawner.SpawnUnit(bp, pos, Quaternion.identity, currentAreaState);
u.Faction.Set(BlueprintRoot.Instance.PlayerFaction);
u.CombatGroup.Id = "kgd.guard";     // 纯字符串，零蓝图
// 不挂 UnitPartCompanion，不挂 UnitPartSummonedMonster，不需要 ForceAIControl
```

原理（BaseUnitEntity.IsDirectlyControllable 判定链末尾）：
```csharp
if (GetOptional<UnitPartSummonedMonster>() != null) return Faction.IsDirectlyControllable;
if (HasMechanicFeature(ForceAIControl)) return false;
var comp = Master?.GetOptional<UnitPartCompanion>() ?? GetOptional<UnitPartCompanion>();
if (comp != null && comp.State != ExCompanion) return comp.State != Remote;
return false;   // ← 非 companion、非 summon 天然不可直控
```
配套 PartUnitBrain.IsAIEnabled { if (!Owner.IsDirectlyControllable) return true; }

Brain 什么都不用做：PartUnitBrain.OnAttachOrPostLoad() 会自动 SetBrain(Blueprint.DefaultBrain)。

**用自定义 group id 而不是 "<directly-controllable-unit>"**：
两者都是盟友，但自定义 id 让 UnitGroupMemory / AttackFactions 与主队隔离，
且 TurnController.IsAiTurn 要求 !IsInPlayerParty，而 IsPlayerParty 判定就是
Id.Equals("<directly-controllable-unit>")。用主队 id 会导致 AI 拿不到回合。

### 4.2 跟随（原版系统，两句代码）

原版队友**根本不跟随** —— UnitCommandsRunner.MoveSelectedUnitsToPointRT 是
点一次给每个选中单位各下一条 UnitMoveTo，看起来像跟随只因整队被全选。
筛选条件 `PartyAndPets.Where(IsDirectlyControllable)` 把我们排除在外。

真正的跟随系统是给**非队伍单位**用的（现有消费者全是宠物/魔宠/漫游NPC，没有一个是队友）：

```csharp
var personal = new FormationPersonalSettings {
    m_Offset = new Vector2(2f, -2f),   // X=队长右方, Y=队长前方
    m_RepathDistance = 4f,
    m_LookAngleRandomSpread = 90f,
};
guard.GetOrCreate<UnitPartFollowUnit>()
     .Init(Game.Instance.Player.MainCharacterEntity, new FollowerSettings(personal));
// 解除：guard.Remove<UnitPartFollowUnit>();
```

FollowerSettings 与 FormationPersonalSettings 都是普通 [Serializable] C# 类，
**不是 BlueprintScriptableObject**，运行时 new 即可，不产生 AssetId。

传 personal != null ⇒ 走 AddIndependentFollower，独立固定偏移，不占队伍编队槽位。

四个相关类（全部零党派耦合）：
- UnitPartFollowUnit（跟随者身上）
- UnitPartFollowedByUnits（队长身上，自动创建）
- FollowersFormationController（算目标点）
- UnitFollowUnitController（执行移动/传送）
注册于 GamesModeFactoryFacade.cs:101-102，**仅 GameModeType.Default**（实时探索）。

白拿两个官方修复：
- 跨区域被抛飞 → GetActionType 检测不同 area 时返回 Teleport
- 过图消失 → Player.MoveCharacters 用 alwaysTeleport:true 搬运；
  **IndependentFollowers 无视 moveFollowers 参数，永远被搬运**

原版一行先例：UnitPartPetOwner.StartFollowing()

**坑：完全不持久化**（两个 Part 的 [JsonProperty] 数量都是 0），
读档后 leader 引用全丢。必须每次读档/进图重新 Init。
原版自己就这么做（MakeUnitFollowUnit.OnActivateOrPostLoad）。
不进存档 = 不可能毁档，是好事。

**坑：FollowerSettings 的四个 bool 是 private setter，只能是 false**
（AlwaysRun / CanBeSlowerThanLeader / FollowWhileCutscene / FollowInCombat）。
要改需反射调私有重载 Init(BaseUnitEntity, bool, bool, bool, bool, FormationPersonalSettings)。

### 4.3 入口交互（零蓝图，原生体验）

```csharp
vanillaNpc.GetOrCreate<UnitPartInteractions>()
          .AddInteraction(new OpenRetinuePanelInteraction());
```
自己实现 IUnitInteraction（普通 C# 接口）：
```csharp
int Distance { get; }            bool IsApproach { get; }
float ApproachCooldown { get; }  bool MainPlayerPreferred { get; }
bool IsAvailable(BaseUnitEntity initiator, AbstractUnitEntity target);
AbstractUnitCommand.ResultType Interact(BaseUnitEntity user, AbstractUnitEntity target);
```
Interact() 里直接开面板，不碰 DialogController。

存档足迹逐层验证为零：
- m_Interactions 是 private readonly List<IUnitInteraction>，无 [JsonProperty]
- GetHash128() 不含交互列表
- 唯一带 [JsonProperty] 的 IUnitInteraction 字段是 UnitInteractWithUnit.m_Interaction，
  但 PartUnitCommands 的 m_Queue/m_Current 都无 [JsonProperty]，命令对象从不落盘，是死路径

更外科手术的变体：Postfix UnitPartInteractions.SelectClickInteraction，
按 UniqueId 精确换 __result —— 完全不写入 m_Interactions，卸载即复原。

**坑：AddInteraction 插 index 0**，会压掉该 NPC 的原版对话。挑龙套 NPC，
或用 IsAvailable() 做条件互斥。避开被 etude bracket 引用的 NPC（会有顺序竞争）。

**坑：绝对不要用 RemoveInteractions(Predicate)** —— 它自身有 bug
（UnitPartInteractions.cs:127-128 先 RemoveRange 再用已修改的 Count 裁剪 Cooldowns，
导致两者长度不同步）。只用 RemoveInteraction(instance)。

**坑：交互是纯运行时态。** 只在 MechanicEntity.OnInitialize() 里由
SetupBlueprintInteractions 从蓝图重建，Entity.PostLoad() 不会重跑。
每次读档/切区域/回主菜单后必须重新挂载。除 UniqueId 外不要缓存实体引用。

### 4.4 成长（mod 一个数字都不存）

BodyGuard 的错误是架构性的：自建 XP 账本引入第二个真值，必然与 ToyBox 失同步（issue #11）。

正确做法（这是 Player.TryUpdateLevel 的原文写法）：
```csharp
int rtXp = Game.Instance.Player.MainCharacterEntity.Progression.Experience;
guard.Progression.AdvanceExperienceTo((int)(rtXp * ratio), log: false);
```
ratio 来自名册（杂兵 0.6 / 精锐 0.9），是**派生**不是**镜像**。
ToyBox 改主角经验 → 下次重建卫队立刻跟上。第二本账不存在，失同步不可能发生。

消费等级（照抄 ApplyCareerPath.OnActivate）：
```csharp
int limit = guard.OriginalBlueprint.GetComponent<CharacterLevelLimit>()?.LevelLimit ?? int.MaxValue;
while (guard.Progression.CanLevelUp && guard.Progression.CharacterLevel < limit) {
    if (!guard.Progression.CanUpgradePath(path)) break;
    using var m = new LevelUpManager(guard, path, autoCommit: true, guard.Progression.CharacterLevel + 1);
    foreach (var sel in m.Selections)
        if (sel is SelectionStateFeature f && f.CanSelectAny)
            f.Select(PickFromPresetOrDefault(f));
}
```
19 个原版 career path 可用，零新增。

**坑：LevelUpManager 用 autoCommit:false 会 CreatePreviewUnit() 造真实克隆实体**，
不 Dispose 就往世界里漏实体。**一律用 autoCommit: true。**

**坑：GainExperience 会静默吞小额**：`int r = exp * ExperienceRatePercent / 100; if (r < 1) return;`
经验倍率非 100% 时 AdvanceExperienceTo 不会一次收敛。

**坑：AddPathRank 不做任何校验**，能推过 tier 规则。自己 clamp，
否则 GetRankEntry 抛 "Rank entry is null"。

### 4.5 千万别碰的命名空间

Kingmaker.UnitLogic.Levelup.**Obsolete** —— Pathfinder 残留：
PartUnitProgression.Classes / GetClassLevel / MythicLevel、ProgressionData、
AddArchetype、BlueprintCharacterClass / BlueprintArchetype / BlueprintProgression

**写了能编译、能跑、不产生任何效果。** 最容易白白浪费一整天的坑。

### 4.6 换装

模式 A（v1 默认，绝对安全）：制式配发
```csharp
guard.Inventory.EnsureOwn();                    // 私有背包
guard.Body.TryInsertItem(itemBlueprint, slot);  // 失败自动回落到背包
```
私有背包随单位销毁，零仓库交互、零物品复制风险。

模式 B（验证后再上）：从共享仓库转移
```csharp
guard.Inventory.MakeSharedInventory();   // 要求 OwnerUnit.Faction.IsPlayer
```
**阻塞项**：销毁卫兵时共享 collection 里的已装备物品会不会被一并销毁？必须先测。

装备约束（原版行为，注意方向）：
```csharp
// ItemEntity.CanBeEquippedBy
if (!owner.IsPlayerFaction || CanBeEquippedInternal(owner)) return true;
```
非玩家阵营无条件放行；设成玩家阵营后 EquipmentRestriction 才开始生效。
EquipmentRestrictionMainPlayer 的物品卫队永远装不上，UI 要预先过滤。

**隐藏地雷**：PartInventory.OnApplyPostLoadFixes 里
`if (Player.PartyAndPets.Contains(OwnerUnit)) RestoreSharedInventory();`
会把私有背包物品全部抽进 Player.Inventory。我们的卫队不在 PartyAndPets 里，幸免。
**但一旦为了拿原版 UI 而给卫队挂 CompanionState.InParty，下次读档私有装备就会被吸走。**

### 4.7 派系门槛

```csharp
ReputationHelper.GetCurrentReputationLevel(FactionType.Drusians)
ReputationHelper.FactionReputationLevelReached(FactionType.Pirates, 3)
// 事件：EventBus 订阅 IGainFactionReputationHandler
```
底层 Game.Instance.Player.FractionsReputation（Dictionary<FactionType,int>，
public 字段，注意官方拼写笔误 "Fractions"）。

**FactionType 是编译期枚举，只有 6 个值**：None / Drusians / Explorators / Kasballica / Pirates / ShipVendor。
加不了新派系。

招募池配置放 mod 目录的 JSON（不是存档），改配置不改代码。

---

## 五、已否定的路线及原因

| 路线 | 否定原因 |
|---|---|
| 召唤物 | IsDirectlyControllable 里 summon 分支**早于** ForceAIControl 返回 ⇒ 玩家阵营召唤物永远玩家直控 |
| AddCompanion(remote:true) | ① Remote 语义是「不在场」，无 CompanionSpawner 就没有肉身，mod 加不了场景 GameObject ② AddCompanion 总是设 CombatGroup.Id = "<directly-controllable-unit>" ⇒ IsInPlayerParty ⇒ TurnController.IsAiTurn 返回 false ⇒ AI 拿不到回合 |
| 对话式招募（新增 BlueprintAnswer） | 新蓝图必然进存档 |
| 运行时注入对话选项 | 技术可行，但 DialogState.BookEventLog 是**真蓝图引用**，假 answer 在 book event 对话里被选中即毁档。为开个面板不值得 |
| 原版 vendor 界面 | 需要新 BlueprintSharedVendorTable + 代币物品 |
| 自定义军衔特性 | 新 BlueprintFeature 会挂到单位 Features 上，EntityFact.Blueprint 是 [JsonProperty] |
| fork BodyGuard | 反编译产物编译不过（base..ctor()、非法空合并、unsafe constrained 前缀、上千条 Unknown result type）；36.6% 是不要的 UI；且授权不允许再分发 |
| 复用 AssetId 整体覆盖蓝图 | BlueprintsCache.Load 里已有实例则 OnResourceLoaded 永不触发 ⇒ 所有 mod 对该蓝图的一切 patch 被静默跳过 |

---

## 六、里程碑

M0（半天，不写代码）—— 用 ToyBox spawn 候选单位，肉眼确认阵营/brain/等级/装备。
  产出短名单。因 blueprints-pack.bbp 是二进制，蓝图内容读不到，只能实测。

M1（1-2 天）—— **生死关**。最小 mod：热键 spawn 一个原版单位 + Faction + CombatGroup + 跟随。
  验收：玩家点不到 / 敌人会打它它会打敌人 / 进先攻序列并自己行动 /
        战斗能正常结束 / 跟着玩家走 / 与 ToyBox 同时加载无异常
  **不过则整个方案作废。**

M2（2-3 天）—— 区域生命周期：OnAreaLoadingComplete 重建、OnAreaBeginUnloading 销毁。
  换 3 个区域来回跑 + 存读档各一次。

M3（1 天）—— **安全验收**（必须在加功能之前完成）：
  名册落 InGameSettings；禁用 mod 后读档成功 + 跑验收脚本：
  ```
  unzip -o Auto_1.zks -d /tmp/sv
  grep -c 'kgd\|<程序集名>' /tmp/sv/player.json /tmp/sv/party.json   # 必须全 0
  ```

M4（2-4 天）—— 成长。ToyBox 交叉测试。
M5（2-3 天）—— 换装模式 A。
M6（1-2 天）—— 招募池 + 声望门槛 + 面板。
M7+ —— 出战开关、squad 先攻收敛、模式 B、军衔展示。

工作量：原估 15-28 人日；跟随与交互两块因复用原版系统大幅缩减，实际应低于此。

---

## 七、未知项（按阻塞程度）

阻塞级：
1. 「spawn + faction + combat group」是否真产出行为正常的 AI 盟友（M1 全部验收项）
2. UnitPartFollowUnit 挂在非宠物单位上是否正常工作
3. 销毁卫兵时共享 collection 里已装备物品是否被一并销毁（决定模式 B 生死）
4. PartCombatGroup.Id 是否 [JsonProperty]（决定能否免费拿到 mod 标记）

设计影响级：
5. 候选单位的实际内容全部未知（faction/brain/等级/装备/CharacterLevelLimit）
6. LevelUpManager(autoCommit:true) 对非同伴单位的实际行为
7. SpawnUnit 的 UnitCustomizationVariation 重载能否 pin 住外观
8. 非战斗状态下 brain 是否运行（影响战斗外行为）

---

## 八、参考路径

原版明文蓝图：模板 tar 内共 181,584 个 .jbp，**但实际只解压了 412 个**（当时按需抽取，
没有全量展开）。要 grep 全库 AssetId 必须先补解压，否则会漏。已解压部分在：
  <仓库根>\ref\rt_probe\extracted\WhRtModificationTemplate\Blueprints\   (412 个)
蓝图索引（{Name,Guid,TypeFullName}）：
  H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\cheatdata.json
游戏程序集：
  H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\WH40KRT_Data\Managed\
    Code.dll (13.9MB) / RogueTrader.GameCore.dll / 0Harmony.dll (2.2.2.0) / mscorlib.dll / netstandard.dll
  注意：主程序集是 Code.dll，不是常见的 Assembly-CSharp.dll，网上多数教程对不上
Code.dll 反编译产物：
  <仓库根>\ref\rt_probe\dec\
BodyGuard 反编译源码（零混淆，15,151 行，含作者原始文件路径行号，作侦察用不抄）：
  %USERPROFILE%\bg_decomp\
UMM 程序集：
  %USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\UnityModManager.dll
数据 patch 骨架（已实测跑通）：
  %USERPROFILE%\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Modifications\ShipPatchTest\

构建注意：**必须引用游戏自带的 BCL**（mscorlib/netstandard/System.Memory 等），
不能用微软 net472 参考程序集 —— 否则 MathF / Math.Clamp / System.Index 全部报错。
csproj 需 <NoStdLib>true</NoStdLib> + <NoConfig>true</NoConfig> +
<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>。
---

## 九、M1 实测结果（2026-08-14，v0.0.2）

**核心假设全部成立。M1 通过。**

测试环境：Footfall，主角 exp=85799，候选单位 DLC3_DL_Guard_Ranged_Ally_Unit (02094127ee4c402fbedbce1aff086e62)

Dump 输出（原文）：
```
faction=Player  combatGroup=kgd.guard
IsDirectlyControllable=False  IsInPlayerParty=False
IsInCombat=False  IsDead=False
brain=DLC3_DL_Guard_Ranged_Brain  AIEnabled=True
follow=有, leader=StartGame_Player_Unit
level=15  exp=68639
```

验收项：
- [x] 单位生成
- [x] 玩家点不到（IsDirectlyControllable=False）
- [x] IsInPlayerParty=False（TurnController.IsAiTurn 的前提）
- [x] brain 自动挂上（PartUnitBrain.OnAttachOrPostLoad 取 DefaultBrain），AIEnabled=True
- [x] **跟随生效** —— UnitPartFollowUnit 在非宠物单位上可用，实测确实跟着玩家走
- [x] 敌人视其为目标并攻击
- [x] **AI 在自己回合移动并攻击**
- [x] 与 ToyBox 同时加载无异常

### 新确认的事实

1. **Brain 无需任何处理**：复用原版战斗单位就自带调好的 brain。
   BodyGuard 新建的 12 个 BG_*_Brain 是不必要的。

2. **AdvanceExperienceTo 单独不涨等级**（实测 0 -> 0），
   必须靠 career path 消费才生效。设计文档第 4.4 节的推断被证实。

3. **等级从经验派生**：只跑了 2 次 LevelUpManager 就到 15 级，
   说明加 path rank 只是解锁派生，不是逐级累加。

4. **CanUpgradePath 会在某点返回 false**：本例中 SoldierCareerPath
   在该单位上到 15 级停止。原因待查（tier 规则？与单位自带 career 冲突？）。
   这是每个候选单位的**天花板机制**，做梯队时必须逐个摸清。

5. 该单位**没有 CharacterLevelLimit** 组件，无硬等级上限。

6. **未做经验对齐时，卫兵在高级区域第一回合即死** —— 等级差造成，非 bug。

### 已知简化（M1 阶段刻意留的）

- 落点是主角右后方 1.5m 的简单偏移，无寻路校验，可能卡墙/悬空 → M2 修
- DespawnAll 只在当前区域有效，换图后旧单位残留 → M2 的区域生命周期解决
- LevelUpManager 的 selections 自动选第一个合法项，不可配置 → 后续做名册配置
- 无区域生命周期，无名册持久化

### 待查

- CanUpgradePath 在什么条件下返回 false（决定每个候选单位的等级天花板）
- 不同候选单位的 CharacterLevelLimit 分布
- 销毁卫兵时共享 collection 里已装备物品是否被一并销毁（换装模式 B 的一票否决项）