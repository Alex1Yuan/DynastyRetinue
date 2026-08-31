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
