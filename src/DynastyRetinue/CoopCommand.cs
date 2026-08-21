using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.GameCommands.Cheats;

namespace DynastyRetinue
{
    /// <summary>
    /// 走官方指令队列的跨机器动作通道。
    ///
    /// ★为什么必须有这么一层★
    ///   官方合作是 lockstep：两台机器各跑一遍**同样的模拟**，网络上只传**指令**。
    ///   本 mod 面板上点一下就直接改状态 —— 只有一台机器执行了，帧末对哈希必然对不上。
    ///   实测确认：合作里从面板招募会立刻弹不同步。
    ///
    ///   而且代价比"这一次不同步"更重：新实体的 UniqueId 来自 `Uuid.Instance`，
    ///   那是个 `StatefulRandom`，随机状态**属于同步状态**。单边生成一个单位会把
    ///   本机的随机流永久推快一格，之后每次原版生成的 id 都跟着错位。
    ///   ⇒ 红线：**mod 改变持久状态必须两台都做，或者两台都不做。**
    ///
    /// ★为什么借 RunCheatCommandGameCommand，而不是自己加一个 GameCommand★
    ///   指令是用 MemoryPack 的 union formatter 序列化的，新类型要占一个 union tag，
    ///   而那些 tag 是随游戏一起编译死的。硬塞一个既可能和未来版本撞号，
    ///   又会让两个不同 mod 版本之间的兼容性彻底不可控 —— 那是最容易炸的一步。
    ///
    ///   RunCheatCommandGameCommand 恰好把需要的四条全占齐了：
    ///     · IsSynchronized => true                     会被复制
    ///     · 载荷是 string + string[]                   任意参数
    ///     · 静态构造里已 RegisterFormatter()           tag 现成，不用碰序列化
    ///     · Create() 内部 GameCommandQueue.AddCommand  入队即广播，单机也走同一条路
    ///
    ///   我们不去动游戏的作弊指令表（CheatDatabase 是编译期代码生成的，
    ///   只暴露 IReadOnlyDictionary，本来也没有运行时注册接口）——
    ///   只是**拦下以 kgd. 开头的那些**自己处理，占一个我们自己的命名前缀。
    ///
    /// ★执行侧的两条铁律★
    ///   ① **必须完全同步**。原版的 ExecuteInternal 是 async void，一旦我们的处理
    ///      函数里出现 await，两台机器就可能落在不同的 tick 上 —— 反而制造不同步。
    ///      所以下面的补丁是 Prefix + return false，async 状态机根本不会启动。
    ///   ② **不许读本机设置**。发起方要把所有会影响结果的值解析成显式参数塞进 args；
    ///      执行侧只认 args。否则两台机器的 mod 设置一不一样，结果就分叉。
    /// </summary>
    internal static class CoopCommand
    {
        /// <summary>我们的命名前缀。改它等于换协议 —— 不要动。</summary>
        public const string Prefix = "kgd.";

        /// <summary>动作名 -> 处理函数。处理函数必须是同步的，见类注释铁律①。</summary>
        private static readonly Dictionary<string, Action<string[]>> _handlers =
            new Dictionary<string, Action<string[]>>(StringComparer.Ordinal);

        public static void Register(string verb, Action<string[]> handler)
        {
            if (string.IsNullOrEmpty(verb) || handler == null) return;
            _handlers[Prefix + verb] = handler;
        }

        /// <summary>
        /// 发一个动作。**两台机器都会执行**（单机则只有本机，走的仍是同一条路径）。
        ///
        /// 调用方要保证 args 里已经包含全部输入 —— 执行侧不许再读本机设置。
        /// </summary>
        public static void Send(string verb, params string[] args)
        {
            string cmd = Prefix + verb;
            try
            {
                if (!_handlers.ContainsKey(cmd))
                {
                    Main.LogError("[合作] 未注册的动作：" + cmd);
                    return;
                }
                Main.Log("[合作] 发出 " + cmd + " " + string.Join(" ", args ?? new string[0]));
                RunCheatCommandGameCommand.Create(cmd, args ?? new string[0]);
            }
            catch (Exception e)
            {
                // 发不出去就地执行，至少单机不受影响；联机下会不同步，但那时已经有别的问题了
                Main.LogError("[合作] 入队失败，退回本地执行：" + e.Message);
                Dispatch(cmd, args);
            }
        }

        /// <summary>由补丁调用。返回 true 表示这条是我们的、已处理。</summary>
        public static bool Dispatch(string cmd, string[] args)
        {
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            Action<string[]> h;
            if (!_handlers.TryGetValue(cmd, out h))
            {
                // 对方的 mod 版本更新、发来我们不认识的动作 —— 咽掉，不要抛。
                // 抛出去会中断原版的指令执行流程，比少做一件事严重得多。
                Main.LogError("[合作] 收到不认识的动作 " + cmd + "（对方 mod 版本可能不同）");
                return true;
            }
            try { h(args ?? new string[0]); }
            catch (Exception e) { Main.LogError("[合作] 执行 " + cmd + " 失败：" + e.Message); }
            return true;
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// 链路自检：不碰任何游戏状态，只在两台机器的日志里各打一行。
        ///
        /// 这是接任何真实动作之前**必须先过**的一关 —— 如果 ping 不能同时出现在
        /// 两边的日志里，说明通道本身就不通，后面把招募接上去也只是换个姿势不同步。
        /// </summary>
        public static void RegisterAll()
        {
            // 招募：两台机器各自生成同一个卫兵。
            //   参数全部由发起方解析好 —— 分型下标、精英下标、是否跳过名额上限。
            //   执行侧一个本机设置都不读，否则两个玩家的解锁开关不同就会分叉。
            Register("recruit", a =>
            {
                if (a == null || a.Length < 3) { Main.LogError("[合作] recruit 参数不足"); return; }
                int arch, ei; int skip;
                if (!int.TryParse(a[0], out arch) || !int.TryParse(a[1], out ei) || !int.TryParse(a[2], out skip))
                { Main.LogError("[合作] recruit 参数解析失败"); return; }
                UI.RetinueUI.ExecuteRecruit(arch, ei, skip != 0, a);
            });

            // 换船：船体档位会改 State.Size / 护盾 / 护甲 / 格子占位，全是同步状态。
            Register("refit", a =>
            {
                if (a == null || a.Length < 2) { Main.LogError("[合作] refit 参数不足"); return; }
                int tier;
                if (!int.TryParse(a[0], out tier)) { Main.LogError("[合作] refit 档位解析失败"); return; }
                UI.ShipYardUI.ExecuteRefit(tier, a[1]);
            });

            Register("shiprevert", a => UI.ShipYardUI.ExecuteRevert());

            // 遣散：删实体。和生成一样，只有一台做就会把随机流和实体表推歪。
            Register("dismissall", a => RetinueRegistry.DismissAll());

            Register("renameall", a => RetinueTest.RenameAll());

            // 遣散单个。★必须走指令通道★ 它删的是实体，只在发起方执行 = 双方卫队人数
            // 不一致，当场失步。对象不能跨机器传，所以传 UniqueId —— 那个值本身是
            // Uuid.Instance 这条同步随机流产出的，两边一致。
            Register("dismiss", a =>
            {
                if (a == null || a.Length < 1) { Main.LogError("[合作] dismiss 参数不足"); return; }
                string uid = a[0];
                var g = RetinueRegistry.ByUniqueId(uid);
                // 找不到不算错：对端可能已经处理过（例如两人同时点了同一个）
                if (g == null) { Main.Log("[合作] dismiss 找不到卫兵 " + uid + "（可能已被遣散）。"); return; }
                string who = g.CharacterName;
                try { RetinueRegistry.RemoveOne(g); Main.Log("[合作] 遣散 " + who + " (" + uid + ")"); }
                catch (Exception e) { Main.LogError("[合作] 遣散失败：" + e.Message); }
            });

            // 改名：CustomName 进哈希。空串 = 清掉自定义名、交回 mod 自动推导。
            Register("rename", a =>
            {
                if (a == null || a.Length < 2) { Main.LogError("[合作] rename 参数不足"); return; }
                string uid = a[0], want = a[1];
                var g = RetinueRegistry.ByUniqueId(uid);
                if (g == null) { Main.LogError("[合作] rename 找不到卫兵 " + uid); return; }
                try
                {
                    var d = g.GetOrCreate<Kingmaker.UnitLogic.Parts.PartUnitDescription>();
                    if (string.IsNullOrEmpty(want)) { d.SetName(null); RetinueTest.ApplyName(g); }
                    else                            { d.SetName(want); }
                    Main.Log("[合作] 改名 " + uid + " -> " + (string.IsNullOrEmpty(want) ? "(自动)" : want));
                }
                catch (Exception e) { Main.LogError("[合作] 改名失败：" + e.Message); }
            });

            // 核对设置：把本机的设置发给对端，对端逐项比对后列出差异。
            //   ★只读诊断，不改任何人的设置★ 船体加成那类被动规则是补丁运行时
            //   持续读本机设置算的，随指令发快照救不了 —— 只能让玩家看见差在哪，
            //   自己决定改不改、改哪边。
            Register("cfg", a =>
            {
                if (a == null || a.Length < 1) return;
                CoopSettings.ReceiveRemote(a[0], a, 1);
            });

            Register("ping", a =>
            {
                string who = (a != null && a.Length > 0) ? a[0] : "?";
                Main.Log("[合作] ★收到 ping★ 来自 " + who
                       + "　本机身份=" + (CoopState.IsHost ? "房主" : "加入方")
                       + "　—— 这一行必须在两台机器上都出现，才说明指令通道是通的。");
            });
        }
    }

    /// <summary>
    /// 拦下以 kgd. 开头的指令，自己处理，不进原版作弊执行器。
    ///
    /// ★为什么打在 ExecuteInternal 而不是 ExecuteImpl★
    ///   ExecuteInternal 是 `async void`，Prefix 返回 false 能让整个 async 状态机
    ///   **根本不启动** —— 我们的处理函数就在指令派发的那一刻同步跑完。
    ///   如果改打 ExecuteImpl，外面那层 await 仍然存在，续体什么时候跑就不在我们
    ///   手里了，而 lockstep 最忌讳的就是"什么时候跑不确定"。
    ///
    /// ★私有字段怎么取★
    ///   Harmony 的 `___fieldName` 约定直接注入私有字段，不用自己写反射，
    ///   也不会因为字段改名而静默取到 null（对不上会在打补丁时就报错）。
    /// </summary>
    [HarmonyPatch(typeof(RunCheatCommandGameCommand), "ExecuteInternal")]
    internal static class RunCheatCommandPatch
    {
        private static bool Prefix(string ___m_Command, string[] ___m_Args)
        {
            try
            {
                if (CoopCommand.Dispatch(___m_Command, ___m_Args)) return false;
            }
            catch (Exception e) { Main.LogError("[合作] 拦截失败：" + e.Message); }
            return true;   // 不是我们的，原样交还
        }
    }
}
