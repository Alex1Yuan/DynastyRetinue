# 近战精英动画问题 —— 研究归档

> 归档时间 2026-08-30。原始 workflow 输出见同目录 `_raw_tpose_workflow.txt`（277 KB）。
> 建立这个目录的原因：作者提出「每次深度检索出来的结果建议保存起来，
> 以后要查可以直接查到，而不是上下文长了之后就忘记了」。**这条以后一律照做。**

## 结论速查

| 问题 | 结论 | 置信度 |
|---|---|---|
| 电僧攻击放不出来 + 卡到超时 | **Sequenced 队列饿死**（见下 §1） | 已修，实测验证 |
| 施法技能大字 | **单位本身只有一个施法动画**（见 §2） | 双路独立确证 |
| `ReaperCareerPath` 换掉了动画集？ | **证伪**（见 §3） | high |
| `BlockAttackAnimation` 卡住？ | **证伪**，实测连续多场触发 0 次 | 已删补丁 |
| 死从天降走跳跃动画？ | **证伪**，它走 CastSpell | 已删补丁 |
| 帧率下降 | 诊断补丁挂在每帧路径上（见 §4） | 已修，138→290 帧 |

---

## §1 电僧「攻击放不出来」根因（已闭合）

两个精英的攻击动画资源 `ExecutionMode` 不同，这是它们表现不同的**唯一**差异：

```
ElectroPriest_AnimationSet_Melee / _v2   ExecutionMode = 1 (Sequenced 排队型)
Sicarian_Chaos_AnimationSet_Melee        ExecutionMode = 0 (Interrupted 打断型)
```

`AnimationManager.Execute(AnimationActionHandle)` 按这个字段分叉（IL_01E2 是全库唯一读取点）：

- **Interrupted(0)**：`AddActionHandle` → Release 旧 `m_CurrentAction` → `StartInternal`，**必启动**
- **Sequenced(1)**：`if (m_CurrentAction == null || m_CurrentAction.DontReleaseOnInterrupt)` 启动；
  `else if (队列 > 10)` 报 Warning 丢弃；**`else Queue.Enqueue` —— 唯一静默出口，不入表不启动**

`UpdateActions` 补不上：顶部循环只遍历 `m_ActiveActions`，排队中的句柄不在里面；
排空条件是 `(CurrentAction == null || IsReleased || DontReleaseOnInterrupt) && 队列 > 0`。

**占位者是森罗刃网**：`ReaperBladeShroud_Caster_Buff` 带 `PlayLoopAnimationByBuff`，
`TryRequeueAction` 把 buff 循环的 `ExecutionMode` 强制写成 1 并接管 `m_CurrentAction`；
它是循环、自己不结束、`DontReleaseOnInterrupt=false` ⇒ 永久堵死队列。
`DontReleaseOnInterrupt` 全库只有 `WarhammerUnitAnimationActionLocoMotion` / `LocoMotionHuman` 返回 true。

**修法演进**（三版才对）：
1. 1.7.36 `cur.Release()` —— 能放行队列，但**杀掉循环动画**，而 `PlayLoopAnimationByBuff`
   只在 `HandleUnitCommandDidStart` 和 `UnitAttackOfOpportunity.RestoreLoopAnimation` 里恢复循环。
   回合结束后没有新指令 ⇒ 单位一直站在绑定姿势里。
2. 1.7.40 把循环句柄的 `DontReleaseOnInterrupt` **无条件**答成 true —— 打断分支也读这个属性，
   导致循环**永远不被释放**、永久占位。实测「循环保护 12568 次、替换武器风格 373→3688」。
3. 1.7.41 **限定作用域**：只在 `Execute` 那一次调用内为 true（Prefix 开、Finalizer 关）。数字回落正常。

**副作用（已知、不修）**：`UnitAnimationManager.Execute` 在 base 之前先调 `CanExecute`，
后者在 `IsBusyByLoopAnimation`（= `CurrentAction.Action is WarhammerBuffLoopAction`）时
抑制 `Dodge(14) / Hit(15) / JumpAsideDodge(31)`。把循环挤下去之后这三类动画又能播了 ——
即卫兵在森罗刃网期间会有原版不会有的受击/闪避反应。
**为什么不修**：直觉的补救（给 `get_IsBusyByLoopAnimation` 加 Postfix 补回 true）会
**过度抑制** —— 原版抑制窗口天然自终止（下次 Execute 覆盖 `m_CurrentAction`），
而我们的循环整个 buff 期间都挂着，补回去等于「整场挨打没有受击反应」，比现状糟。
且该 getter 内 `CurrentAction.Action` 无 null 检查，在里面抛异常会打断整条动画指令。

---

## §2 施法技能大字的真因：动画素材本来就没有

`sicarian.animations` 全部 27 个 AnimationClip：

```
动作类（仅 3）  Sicarian_Spec_Melee_v1 / v2 · Sicarian_Spell_Granade
位移类（5）     Sicarian_Forcemove_v1 ~ v5          ← 未接入施法表
待机/移动       LoMo_Idle · LoMo_Idle_Noncombat_v1/v2 · LoMo_Run · LoMo_Walk
                MicroIdle_v1/v2 · VariantIdle_v1
受击/控制       HIt · HookPulled_In/Loop/Out · Stun_Loop/Out
倒地            Prone_Breath/Dead/Die/Fall/StandUp
```

**`Sicarian_Spell_Granade` 是整个包里唯一的施法动画。**
这直接解释实测的 `[动画查表] CastSpell 表里有=[Grenade]`。

同理 `electropriest.animations` 只有 `ElectroPriest_AnimationSet_Melee` / `_v2` 两个特殊攻击，
其施法表只有 `Style=0=Directional` —— 所以电僧「其它技能看着正常」纯属它那一条恰好能命中，
不是它更完整。

**这两个单位在原版里是杂兵**：走路、扔个手雷、砍一刀、被钩子拉、倒地。
我们给它们挂了收割者 + 战术专家两条职业线、十几个施法技能，动画库里没有对应素材。

⇒ **前十几个版本都在修「查表逻辑」，而真正的问题是「表里本来就没货」。**

---

## §3 `OverrideAnimationSet` 假说 —— 证伪（high）

`ReaperCareerPath`（dd6948ee）**确实**在 +0x0B3F 带 `OverrideAnimationSet` 组件，
索引 [1684]/[1685] → `Human_Reaper_AnimationSet` / `Human_Reaper_Female_AnimationSet`。
但它对这两个单位**物理上进不了门**：

```
OverrideAnimationSet → UnitPartVisualChange.AnimationSetOverride
  → AbstractUnitEntityView.OnDidAttachToData
       IL_00BE GetComponentInChildren<Character>()
       IL_00CB brfalse → 拿不到 Character 就整段跳过
  → Character.OnStart 才写 AnimationManager.set_AnimationSet
```

- `AnimationManager.set_AnimationSet` 全库 **total hits = 1**，唯一调用者就是 `Character.OnStart`
- 两个 prefab（`81328b84…​.unit` 30 个 MonoScript / `de093833…​.unit` 32 个）
  **都没有 `Character` 脚本**，只有 `UnitEntityView` + `UnitAnimationManager`；
  它们是自带骨骼蒙皮的烘焙 creature，不是可换装的 doll
- 独立铁证：引擎实际在用的就是 prefab 自带的表，与 §2 的资源包 dump 逐项吻合；
  若覆盖生效，表应变成 Reaper 那份

**复核命令**：`py tools\_q14_scripts.py`（期望 `HAS Character component script? -> False`）；
`tools\_kt_callers.ps1 -TypeName Kingmaker.Visual.Animation.AnimationManager -MethodName set_AnimationSet`（期望 total hits = 1）

⚠️ 边界：这条只否定 `OverrideAnimationSet / UnitPartVisualChange` 这一条通道。
现有补丁 A/C/F5/I/J/K 动的是 AnimStyle 替换、CreateHandle 回退、ExecutionMode/DontReleaseOnInterrupt，
与本通道无交集，**不要据此一并回滚**。

---

## §4 帧率回归（已修）

同一场战斗对比（作者确认场景可比）：

```
              帧数/10秒                        尖峰(>100ms)
1.5.21 基线   712 / 727 / 602 / 358            22 / 3 / 3 / 1
1.7.43        312 / 251 / 295                  5 / 9 / 1
1.7.45        141 / 138 / 138                  16 / 17 / 18     ← 最差
1.7.48        314 / 312 / 273 / 303 / 246…     6 / 5 / 2 / 2 / 3
```

两个原因：

1. **诊断补丁挂在每帧路径上**。即使补丁体第一行 return，Harmony 的调度开销省不掉。
   1.7.48 起加 `[Main.DiagOnly]` 标记，「详细日志」关着时**整个类跳过、一个钩子都不装**。
2. **`GateNote` 的参数在调用点无条件拼串**，开关关着也照样分配。改成延迟构造（传 `Func<string>`）。

**教训**：日志参数的构造成本是调用方付的，与日志开不开无关；热路径必须延迟构造。

---

## §5 已删除的死补丁（连续多场实测 0 触发）

| 补丁 | 挂点 | 删除版本 |
|---|---|---|
| E `WarhammerUnitAnimationActionCastSpell.GetAnimationVariant` | 真正跑施法的是无 Warhammer 前缀那个 | 1.7.48 |
| F `UnitAnimationActionJump.GetAnimation` | 死从天降走 CastSpell 不走 Jump | 1.7.48 |
| H `AttackBlockOnHandAttack/CastSpell` + `AttackBlockWatchdog` | `BlockAttackAnimation` 理论已推翻 | 1.7.49 |
| P1 `CoverThrowUnblockPatch` | 掩体抛异常恒为 0 | 1.7.49 |
| P3 `StyleNoneRepairPatch` | 全局改写风格，0 触发且是地雷 | 1.7.35 |

**教训**：假说被推翻时要连同它派生的补丁一起撤。我当时只更新了注释，
补丁却留在热路径上白跑了十几个版本。

---

## §6 诊断工具现状

| 日志 | 内容 | 开关 |
|---|---|---|
| `[动画时间轴]` | 技能 / 动作类 / 创建→启动延迟 / 播放时长 / 结束时技能是否还在跑 | 详细日志（关时不挂载） |
| `[动画查表]` | 动作 + 技能 → 要哪种风格 / 表里有哪些 | 详细日志 |
| `[动画路由]` | 技能 → 请求哪种 `UnitAnimationType` → 拿到没有 | 详细日志 |
| `[大字现场]` | 无指令且动画层空时的现场（CurrentAction / 移动句柄 / 队列） | 详细日志 |
| `[指令卡顿]` | 指令跑超 5 秒的现场快照 | 详细日志 |
| `[帧时间]` | 帧数 / 尖峰分布 | **无开关，始终打** |

**判读速查**（时间轴）：
- `创建→启动 = ★从未启动★` ⇒ 句柄进队列没轮上（排队饿死）
- `播放时长 < 50ms` ⇒ `set_IsSkipped → Release`，播了等于没播 = 肉眼大字
- `结束时技能=已结束` 而动画才收尾 ⇒ 时序错位

**视觉判据**（日志分不出，必须看画面）：
- 双臂水平伸直 = 真 bind pose ⇒ 没有片段，或片段在播但图层权重为 0
- 冻在自然姿势 = 片段加载了但不推进
- 站姿正常动作不对 = 片段在播，只是选错了

---

## §7 死从天降的两段动画机制（2026-09-01 工作流，40 agent，全部对抗验证过）

### §7.1 打击段没有自己的动画 —— 它复用主技能的 handle

```
ContextActionJumpToTarget.RunAction            (Code.dll)
  IL_0182  call AnimationManager::get_CurrentAction   → stloc.5   此刻在播的 = 主技能 CastSpell handle
  IL_01A1  get_Type / IL_01A6 ldc.i4.1 / bne.un.s     只有 Type==LocoMotion(1) 才丢弃
  IL_01DD  set_OverrideAnimationHandle                把老 handle 交给 Strike
  IL_01EB  PartUnitCommands::AddToQueue

UnitUseAbility.StartAnimation  (tok=0x0600DE58)
  get_OverrideAnimationHandle → isinst → brfalse.s IL_0202
  set_Animation(h); set_HasAnimation(true); ret        ★早退，永不 Execute★
```

全游戏只有这一处用途：写入方唯一 `ContextActionJumpToTarget.RunAction`，
读取方唯一 `UnitUseAbility.StartAnimation`（`_allcallers` 扫 16388 类型，各 total hits = 1）。

⇒ **`ReaperDeathWaltzStrikeAbility` 在原版绮贝菈身上也没有独立动画。**
全套变体（Spring / Targeted / Ultimate / UltimateNoTarget / FreeAbility）共用同一个 Strike 蓝图；
它的 `Charge_AbilityFXSettings` 四条 `UnitAnimationActionLink` 全是空串，
蓝图上也没有 `IAbilityCustomAnimation` / `CustomAnimationOverride`。**不是 mod 引入的差异。**

### §7.2 真正吃掉动画时间的是 buff 循环，不是片段长度

```
22:39:58  ReaperDeathWaltzAbility       CastSpell        739 ms
22:39:59  ReaperDeathWaltzAbility       BuffLoopAction  1907 ms
22:40:00  ReaperDeathWaltzStrikeAbility BuffLoopAction  1992 ms
```

施法动画 739ms 就被 buff 循环顶掉（`CastSpell.OnUpdate` 不释放句柄 —— 它 IL 0x4B
只做两件事：写 `SpeedScale = Game.CombatAnimSpeedUp`、`CastClip==null` 时 `UpdateInvalid`）。
剩下 2.6 秒全归 BuffLoop。

**我 1.7.72 盯着「跑步片段只有 0.67s」查，方向是错的。**
「跑一下就站着不动」的成因是 BuffLoop 那 2.6 秒播的是待机片段。

### §7.3 LocoMotion 片段顺序 —— 我一直取的是**非战斗**待机

```
电僧 WarhammerUnitAnimationActionLocoMotionHuman.ClipWrappers
  [0] ElectroPriest_LoMo_Idle_NonCombat      2.6667s   ← FirstClip() 取到的
  [1] null
  [2] ElectroPriest_LoMo_Run_speed_7         0.6667s
  [3] 1H_Freehands_MHA_LoMo_Run_speed_9_In   0.3667s   ★humananimation.animations = 人类骨架★
  [4] 1H_Freehands_MHA_LoMo_Run_speed_9_Out  0.8000s   ★同上★
  [5] ElectroPriest_LoMo_Idle                2.0000s   ← 战斗待机
```

- 电僧**没有走路片段**：Crouch/Walking/Run/Sprint 四档全指向同一个 `Run_speed_7`，Speed 均 7.0
- 锈行猎手 `Walking=Sicarian_LoMo_Walk 1.1000s(速度2.0)` / `Run=Sicarian_LoMo_Run 0.7667s(速度8.38)`
- ⚠️ **两个单位走不同的类**：锈行猎手 `WarhammerUnitAnimationActionLocoMotion`，
  电僧 `...LocoMotionHuman`。二者是**兄弟不是父子**（都直接继承 `UnitAnimationAction`），
  强转会 `InvalidCastException`。字段布局也不同（`WalkingStyleLayer` vs `MovementStyleLayer`）。
- [3][4] 是地雷：按 "Run" 找片段时它们也会命中，塞给机械教骨架就是 bind pose。
  **必须按族前缀过滤。**

### §7.4 ClipDurationType —— 循环这条路走不通

`Default=0 / Oneshot=1 / Endless=2`，唯一消费点 `AnimationManager.AddAnimationClip`(7参) IL_0172 switch：

```
case0 Default → ActiveAnimation.GetDuration()
case1 Oneshot → clipWrapper.Length
case2 Endless → 0f
→ TransitionOutStartTime = duration>0 ? Max(duration-TransitionOut, 0.01f) : 0f
```

- **`Endless` 不会让片段循环。** 循环只取决于 `AnimationClip.isLooping`（资源属性），
  `durType` 传不到建 Playable 那一层（4参重载签名里根本没有 `ClipDurationType`）
- **`Endless` 会掐断自终止链**：句柄结束依赖 `IsReleased`，而 Oneshot 的自动 Release
  正是靠 `TransitionOutStartTime>0` 触发。改 Endless ⇒ 句柄永不 Finish = **死锁**
- 施法路径**本来就是 Endless**：`WarhammerUnitAnimationActionCastSpell.StartClip IL_0007 ldc.i4.2`
- `Default` 对循环片段退化成 0，等价 Endless；`m_OverridedDuration` 恒为 -1 是死代码

**拉长片段的正确杠杆是 `SpeedScale`**（`SpeedScale = clipLength / 目标秒数`，
同时缩放播放速率和 TOST 比对用的时间）。但陷阱：`UnitAnimationActionCastSpell.OnUpdate`
和 `WarhammerUnitAnimationActionHandAttack.UpdateInternal` **每 tick 覆写** `SpeedScale`
为 `Game.CombatAnimSpeedUp`，在 Prefix 里设一次下一帧就没了。

### §7.5 位移驱动链与运行时可读字段

```
ContextActionJumpToTarget.RunAction → UnitJumpMoveController.TryStartJump → UnitPartJump.Jump
蓝图 ReaperDeathWaltzAbility: m_Speed=5.0  m_JumpSubType=0(Jump)  m_CastOnSelf=1
UnitPartJump.Jump: Chunk.MaxTime = 世界距离 / 5.0
  若存在 Jump 动作且 LoopedFly=false，MaxTime 被 fly clip 长度整个覆盖
```

`caster.GetOptional<UnitPartJump>()?.Active` → `Chunk`，全 public：
`MaxTime / PassedTime / InClipTime / OutClipTime / Speed / JumpPhase / TargetPosition`，
`JumpPhaseType { In=0, Fly=1, Out=2 }`。**这个 part 只在跳跃时才挂上**，
所以 `Active != null` 就是「此刻在位移中」的现成判据，不用自己算。

### §7.6 已知盲区（未修）

`WarhammerUnitAnimationActionHandAttack.StartClip` 在**带副手片段**时走
`AddAnimationClip` 直连，**绕开 `AnimationActionHandle.StartClip`** ——
挂在后者上的 Prefix 在那条分支上不生效。


---

## §8 动画事件会不会影响伤害？—— 不会（2026-09-01，逐类核实）

作者问：落地那次把 `Spec_Melee_v1` 塞进 buff 循环，会不会触发命中事件造成额外伤害。

两个包里 `AnimationClipEventTrack` 用到的**全部 7 个事件类**：

| 类 | 基类 | 字段 | 性质 |
|---|---|---|---|
| `AnimationClipEventSound` | `AnimationClipEvent` | `m_Name / m_StopName / m_Volume` | 音效 |
| `AnimationClipEventSoundMapped` | `AnimationClipEvent` | `m_Type: MappedAnimationEventType` | 音效/特效 |
| `AnimationClipEventSoundSurface` | **← Sound** | — | 音效 |
| `AnimationClipEventFootStep` | **← Sound** | — | 音效 |
| `AnimationClipEventBodyFall` | **← Sound** | — | 音效 |
| `AnimationClipEventPlaceFootprint` | `AnimationClipEvent` | `m_Locator / m_FootIndex` | 脚印贴花 |
| **`AnimationClipEventAct`** | `AnimationClipEvent` | **无字段** | **视觉特效** |

`Act` 是唯一名字看不出用途的，所以单独查到底：

```
AnimationClipEventAct.Start(AnimationManager)   IL 0xD6
  IL_0049  call EventBus::RaiseEvent<IAnimationEventHandler>
Kingmaker.PubSubSystem.IAnimationEventHandler
  唯一方法    HandleAnimationEvent(MappedAnimationEventType)
  ★唯一实现者  Kingmaker.Controllers.VisualEffectsController★   （全 Code.dll 扫描）
```

⇒ **没有任何一个事件类能触发伤害/攻击/指令。换片段不会影响伤害结算。**

复核：`dump_type.ps1 -TypeName Kingmaker.PubSubSystem.IAnimationEventHandler -All`
（期望只有一个方法）；实现者扫描期望 total = 1。

⚠️ 注意 `AnimationClipEventSoundMapped` 和 `AnimationClipEventAct` 用的是同一个
`MappedAnimationEventType` 枚举，都汇到 `VisualEffectsController` —— 所以「Mapped 音效」
这个名字其实也覆盖特效，**不要按名字把它当纯音效**。


---

## §9 死从天降伤害锚点 —— 根因、修法、实测（2026-09-03，已闭合）

### §9.1 根因链（工作流 + 引擎自身日志端到端坐实，5/5 次一致）

```
CastStyleFallbackPatch 把锈行猎手的施法风格改写成 Grenade（它的施法表里只有 Style=8）
 → 播 Sicarian_Spell_Granade，该片段带 ★2 个★ AnimationClipEventAct
    t=1.0187215 / 1.0373439（间隔 18.62ms，片段长 1.5333）
    对照 ElectroPriest_Spell_Direct 只带 1 个（t=0.8006）
 → 第 2 个 Act 让父指令二次进 OnAction（句柄计数 2 > 父指令已计 1）
 → 撞 OnAction IL_001E-0057 `CurrentActionIndex 1 >= ActionsCount 1` → 返回 Fail(1)
 → Tick IL_03CE 见 Fail →「Forcing finish of UnitCommand cause of ResultType.Fail」
 → 父指令被强制结束、队列腾空 → Strike 立刻拿到第一次 Tick
 → 借来的句柄计数 2 > 自己的 0 ⇒ 当场结算，此时跳跃才过 0.10/0.60 秒
```

**影响面远不止死从天降**：同场 11 次强制结束覆盖 TacticianInspire / Linchpin /
Strongpoint / FinishTheJob / Ultimate / ReaperBloodOath / ReaperBladeShroud / DeathWaltz
—— 每个被改写成 Grenade 的锈行猎手技能都在吃两条红字 + 指令被强制结束。

**不能靠撤补丁修**：撤掉 ⇒ `set_IsSkipped(true)` + `Release`，技能整个不起播 = 1.7.67 那类卡死。
真正的缺陷是**替身片段的 Act 事件数 ≠ 该技能的 ActionsCount**，不是替换本身。

### §9.2 平移的根因：两个单位都没有 Jump(38)

两个 prefab 各有一个 `UnitAnimationActionJump`，但 `m_SubType=2` → `ToAnimationType` 映射成
**41(HookPulled)**，不是 38：

```
电僧      ElectroPriest_AnimationSet.m_Actions[9] = 'Human_AnimationSet_HookPulled'
锈行猎手  Sicarian_AnimationSet.m_Actions[8]      = 'Sicarian_AnimationSet_HookPulled'
```

⇒ `UnitPartJump.ExecuteJumpAnimationAction` 的 `GetAction(38)` 必返回 null，
IL_0049-0052 **静默 ret**（一条日志都不打），跳跃期一帧不播，而位移照常推进 = 滑行。
**是原版行为，不是 mod 弄坏的。** 锈行猎手也在滑，只是被手雷片段遮住了。

### §9.3 修法与实测（1.7.79）

| 补丁 | 挂点 | 实测 |
|---|---|---|
| 【方案 1】OnAction 重入闸 | `UnitUseAbility.OnAction` Prefix，`Result != None` 时原样返回 | 引擎强制结束 **11 → 0** |
| 【方案 4】补 Jump(38) | `UnitAnimationManager.GetAction` Postfix，运行时 `ScriptableObject.CreateInstance<UnitAnimationActionJump>`，`m_JumpFly` = 单位自己的跑步片段，`m_LoopedFly=true` | 两单位都补上；看门狗救场 0 次 |

- 帧率 305~349（改前 302~336），无退步
- **伤害落点已修正**（作者实测确认）—— 机制正如工作流预判：
  补上 Jump 后 `AnimationManager.Execute` 把它设成 `m_CurrentAction` 并释放施法句柄，
  而 `ContextActionJumpToTarget` 是在 `TryStartJump` **之后**才读 `CurrentAction`，
  于是 Strike 借到的变成 **Jump 句柄（act 计数 0）** ⇒ `hasNewAct` 恒假 ⇒ 不再提前结算。
- `LoopedFly=true` 是刻意的：为 false 时 IL_0085-008A 会用片段长度改写 `MaxTime/Speed`，
  跳跃手感会变。配看门狗兜住「飞行中 CurrentAction 被抢走 ⇒ 原地跑步无限循环」（1.7.68 形状）。

### §9.4 本轮的三次测量事故（都写进了代码注释）

1. **l10n 审计**：自写提取器只抓 `L.T(` 后第一个字面量，跨行 `+` 拼接被截断
   ⇒ 报出「缺译 42、其中 41 条键漂移」，全是伪影。仓库里本来就有更完整的
   `tools/check_l10n.py` 且全绿。★该起疑的信号：每个「漂移对」新 key 都恰好是旧 key 的前缀。★
2. **动画时间轴配额**：`MaxPerKind=3` 且**静默**停止记录
   ⇒「完成任务接打击没有 SpecialAttack」看着像铁证，其实配额早用完了。1.7.77 提到 12 并加了达上限提示。
3. **结算探针挂错时刻**：挂在 Strike 首次 Tick 上，在「首次 Tick 就结算」的前提下才等价；
   补了 Jump(38) 之后 act 计数=0，前提消失，标签却还写着「结算点」。
   1.7.80 挪到 `UnitUseAbility.OnAction`（效果真正跑的地方），旧探针改名为 `[死从天降·首次Tick]`。

**共同教训**：探针的语义会随被测对象改变；被测对象一改，标签和上限都必须跟着复核，
否则它会安静地继续输出一个已经不成立的量。

