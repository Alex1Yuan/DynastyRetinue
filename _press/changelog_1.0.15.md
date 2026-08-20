# 1.0.15 更新日志 / Changelog

> 贴到 Nexus 的 Changelog 标签页 / B站动态。中英双语，各自独立成段。
> **发布包**：`dist/DynastyRetinue-1.0.15.zip` —— 解压覆盖 `UnityModManager/DynastyRetinue/` 即可。
> `Settings.xml` 不在包里，覆盖后设置会保留。

---

## 中文

### 修复

**卫兵名字里出现方框**

游戏自带的中文字体是**子集**，用到子集外的字，游戏内的头顶名条会渲染成方框。
实测下来有 6 个字整套字体都没有，全是姓氏：**砚 秦 萧 袁 裴 邵**，人名池里十个名字中招。

麻烦的是这个问题在 mod 面板里**看不出来** —— 面板走 Unity 内置字体，覆盖面大得多，
同一个字在面板里显示完全正常。所以这一版加了个开发工具，直接向游戏已加载的字体
逐字询问，把实测结果导出成白名单，编译时自动校验。以后不会再靠"这字应该挺常用"来判断。

⚠️ **存量卫兵的名字存在存档里，不会自动更新。** 进游戏点一次面板上的
【重新命名全部】，方框才会消失。不点也不会有别的问题，只是继续显示旧名字。

**精英之间的血量断层**

同一条路线上的两个精英，基础血量能差 2.7 倍；灵能线的「亚空间审判者」更是
高出正常区间 3.7 倍。原因是选蓝图时只看了 AI 行为和武器，没有横向对比过数值。

- 赏金 · 猎首：96 → **224**（换成机械教的 Sicarian Ruststalker，主题上「刺客」也比「赏金猎手」更贴"猎首"）
- 亚空间审判者：1040 → **700**

**灵能普通卫兵血量异常偏高**

它用的蓝图是一个 Boss 级的审判官模板，4 级时 428 血，而同级近战 64、军官 48。
换成星语者模板，与军官分型同族。

**没有 DLC3 时，精英会退化成普通卫兵**

五个分型里有四个的精英用的是 DLC3 蓝图。没买 DLC3 的玩家，招到的
「赏金 · 猎首」其实是个普通甲板卫兵 —— 因为兜底逻辑只对普通卫兵生效，精英那一支是空的。
现在精英有自己的兜底链，**退化的只有模型**：名字、军衔、职业链、装备、加点方案全部照常。

**卫兵全挤在同一格里**

跟随位置对每个卫兵都是同一个偏移量，五个人的目标坐标完全重合。
现在按序号排成队长背后的方阵，三列一排。

**打开「详细日志」后游戏明显卡顿**

那个开关下每条卫兵指令都会写一行日志，而写法是"开文件 → 写一行 → 关文件"，
全在游戏主线程上。现在攒够 32 KB 或 2 秒才落盘一次，内容一行不少。
（这个开关默认就是关的，只有手动开过的人受影响。）

**面板上的【重新命名全部】看着像没反应**

它做的是"按规则重新推导名字"，推出来通常还是原来那个，所以点下去零变化。
真正缺的是**逐个改名** —— 现在「命名」下面会列出所有在册卫兵，每人一个输入框，
外加【改名】和【还原】。手改过的名字会一直保留，自动命名不会覆盖它。

### 兼容性

- **不影响现有存档。** 换蓝图只作用于新招募的卫兵，存量卫兵保持原样。
- 没有任何 mod 自建资源写进存档，全部使用原版蓝图。
- 唯一需要手动做一次的是上面提到的【重新命名全部】。

---

## English

### Fixes

**Boxes (tofu) in guard names**

The game's bundled Chinese font is a **subset**. Characters outside it render as
empty boxes on in-world nameplates. Six characters turned out to be missing from
every loaded font — all of them surnames — affecting ten entries in the name pool.

The awkward part: this is **invisible in the mod's own settings panel**, which uses
Unity's built-in font with far wider coverage, so the same character looks perfectly
fine there. This release adds a developer tool that queries the game's actual loaded
fonts character by character and exports the result as a whitelist, which the build
now validates against. No more guessing from "that character seems common enough".

⚠️ **Existing guards keep their stored names.** Click **Rename all** in the panel once
to refresh them. Skipping this is harmless — the old names simply remain.

**Health gaps between elites**

Two elites on the same line could differ by 2.7× in base health, and the psyker line's
Warp Arbiter sat 3.7× above the normal range. Blueprints had been picked for AI
behaviour and weapons without ever comparing the numbers side by side.

- Bounty Hunter: 96 → **224** (now a Mechanicus Sicarian Ruststalker; "assassin" also
  suits *Headtaker* better than "bounty hunter" did)
- Warp Arbiter: 1040 → **700**

**Psyker regular guard had wildly inflated health**

It used a boss-tier Inquisitor template — 428 HP at level 4, against 64 for melee and
48 for officer at the same level. Now uses an Astropath template, same family as the
officer archetype.

**Elites degraded into plain guards without DLC3**

Four of the five archetypes use DLC3 blueprints for their elites. Without DLC3 you
would recruit a *Bounty Hunter* that was actually a generic deck guard, because the
fallback chain only ever applied to regular guards — the elite branch was empty.
Elites now have their own fallback chain, and **only the model degrades**: name, rank,
career chain, gear and talent plan all stay intact.

**Guards bunched up on a single tile**

Every guard used the same follow offset, so all five targeted identical coordinates.
They now form up behind the leader, three to a row.

**Noticeable stutter with Verbose logging enabled**

That toggle logs one line per guard command, and each line was an open-write-close
cycle on the game's main thread. Logging is now buffered — flushed at 32 KB or every
2 seconds — with no loss of content. (The toggle is off by default; only players who
turned it on were affected.)

**"Rename all" appeared to do nothing**

It re-derives names from the same rules, so it usually produces the identical name and
looks like a no-op. What was actually missing is **per-guard renaming** — the Naming
section now lists every enlisted guard with a text field plus **Rename** and **Reset**.
A name you set by hand is kept; automatic naming will not overwrite it.

### Compatibility

- **Existing saves are unaffected.** Blueprint changes apply to newly recruited guards
  only; guards already in your save are left as they are.
- No mod-created assets are written into saves — everything uses vanilla blueprints.
- The one manual step is the **Rename all** click mentioned above.
