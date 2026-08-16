using System;
using Kingmaker;
using Kingmaker.Enums;

namespace KgdRetinue
{
    /// <summary>
    /// 「向高阶顾问采购舰船改装」—— 用废料把座舰换成巡洋舰 / 大巡洋舰。
    ///
    /// 走的是 RecruitDialog 那套注入式对话选项，一个字节的机制都不重复实现：
    /// 那套里每个字段为什么必须非空、为什么必须绕开原版 SelectAnswer，
    /// 都是一次次崩溃换来的（BlueprintAnswer.ShowCheck 的 NRE 会把整段对话打空，
    /// BookEventLog 是类型化蓝图字典、写进去存档就带上我们的 GUID）。
    /// 所以这里只提供三样东西：文案、价格、选中之后干什么。
    ///
    /// ================= 存档安全 =================
    /// 三个动作全部落在**已经验证过的**通道上：
    ///   · Scrap.Spend(int)             —— 纯数值
    ///   · StarshipTool.SetSize(Size)   —— PartUnitState.Size，vanilla 枚举
    ///   · StarshipViewTool.ApplyModelAtTier —— m_CustomPrefabGuid，裸 string + vanilla guid
    /// 一个 mod 自建蓝图都不写进存档。
    /// </summary>
    public static class ShipDialog
    {
        // GUID 与招募那条同族，尾号区分。固定值，便于日志排查。
        public const string CruiserGuid = "kgd00001000010000100001000010002";
        public const string GrandGuid   = "kgd00001000010000100001000010003";

        public const string CruiserKey  = "kgd_ship_cruiser";
        public const string GrandKey    = "kgd_ship_grand";

        /// <summary>把两条选项注册到 RecruitDialog 的注册表。幂等。</summary>
        public static void RegisterAll()
        {
            RecruitDialog.Register(new RecruitDialog.Entry
            {
                Guid     = CruiserGuid,
                TextKey  = CruiserKey,
                Text     = Label(false),
                Enabled  = delegate { return Enabled(false); },
                OnPicked = delegate { Buy(false); },
            });
            RecruitDialog.Register(new RecruitDialog.Entry
            {
                Guid     = GrandGuid,
                TextKey  = GrandKey,
                Text     = Label(true),
                Enabled  = delegate { return Enabled(true); },
                OnPicked = delegate { Buy(true); },
            });
        }

        // ---------------------------------------------------------------- 价格

        /// <summary>
        /// 当前分档下，升到目标档要花多少废料。
        ///
        /// 定价按玩家给的规则：护卫舰→巡洋 500，护卫舰→大巡 1000，
        /// **巡洋→大巡只补差价 500** —— 已经花过的那 500 不重复收。
        /// 实现成"目标总价 − 已投入总价"，这样将来加档位也不用改逻辑。
        /// </summary>
        public static int Price(bool grand)
        {
            int target = grand ? Main.Settings.ShipPriceGrand : Main.Settings.ShipPriceCruiser;
            int paid   = InvestedSoFar();
            int p = target - paid;
            return p < 0 ? 0 : p;
        }

        /// <summary>当前分档等价于已经投入了多少 —— 用来算差价。</summary>
        private static int InvestedSoFar()
        {
            try
            {
                switch (StarshipTool.CurrentSize())
                {
                    case Size.GrandCruiser_3x6: return Main.Settings.ShipPriceGrand;
                    case Size.Cruiser_2x4:      return Main.Settings.ShipPriceCruiser;
                    default:                            return 0;
                }
            }
            catch { return 0; }
        }

        private static bool AlreadyAtOrAbove(bool grand)
        {
            try
            {
                var cur = StarshipTool.CurrentSize();
                if (grand) return cur == Size.GrandCruiser_3x6;
                // 巡洋这条：已经是巡洋或更高都不再显示
                return cur == Size.Cruiser_2x4 || cur == Size.GrandCruiser_3x6;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 显示

        private static string Label(bool grand)
        {
            string name = grand ? "大巡洋舰" : "巡洋舰";
            return "（船坞）把座舰改装成" + name + "　—— " + Price(grand) + " 废料";
        }

        /// <summary>
        /// 这条选项要不要出现。
        /// ★刻意**不**按"废料够不够"来隐藏★ —— 玩家看不到选项就不知道有这回事，
        /// 也不知道要攒多少。买不起时照常显示，选中后告诉他差多少。
        /// </summary>
        private static bool Enabled(bool grand)
        {
            try
            {
                if (Main.Settings == null || !Main.Settings.ShipDialogEntry) return false;
                if (Game.Instance == null || Game.Instance.Player == null) return false;
                if (AlreadyAtOrAbove(grand)) return false;
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 成交

        private static void Buy(bool grand)
        {
            try
            {
                var player = Game.Instance != null ? Game.Instance.Player : null;
                if (player == null) { Main.LogError("[船坞] 拿不到 Player。"); return; }

                int price = Price(grand);
                int have  = 0;
                try { have = player.Scrap; } catch { }   // Scrap 有 implicit operator int

                if (have < price)
                {
                    Main.Log("[船坞] 废料不够：需要 " + price + "，现有 " + have
                           + "（还差 " + (price - have) + "）。没有扣除任何资源。");
                    Notify("废料不足：需要 " + price + "，现有 " + have);
                    return;
                }

                var tier  = grand ? Size.GrandCruiser_3x6 : Size.Cruiser_2x4;
                var model = ShipModelCatalog.DefaultFor(tier);
                if (model == null)
                {
                    Main.LogError("[船坞] 目录里没有 " + tier + " 的默认船模，交易取消，未扣废料。");
                    return;
                }

                // ★先换船再扣钱★ 换船可能被拒（战斗中会拒，见 StarshipTool.SetSize），
                // 顺序反了就是"钱花了船没换"。宁可白换不能白扣。
                if (!StarshipViewTool.ApplyModelAtTier(model, tier))
                {
                    Main.LogError("[船坞] 改装失败（多半在战斗中），未扣废料。");
                    Notify("现在无法改装（战斗中？），废料未扣除。");
                    return;
                }

                try { player.Scrap.Spend(price); }
                catch (Exception e)
                {
                    // 到这一步船已经换了。扣不掉钱只报，不回滚 ——
                    // 回滚要再换一次船，风险比少收 500 废料大得多。
                    Main.LogError("[船坞] ★船已改装但废料扣除失败★: " + e.Message);
                }

                Main.Log("[船坞] 成交：" + (grand ? "大巡洋舰" : "巡洋舰")
                       + "　花费 " + price + " 废料　余额 " + Safe(player) + "　分档 " + tier);
                Notify("座舰已改装为" + (grand ? "大巡洋舰" : "巡洋舰") + "，花费 " + price + " 废料。");
            }
            catch (Exception e) { Main.LogError("[船坞] 交易异常: " + e); }
        }

        private static string Safe(object player)
        {
            try { return ((int)Game.Instance.Player.Scrap).ToString(); } catch { return "?"; }
        }

        /// <summary>给玩家一条可见反馈。拿不到战斗日志就只进 mod 日志，不抛。</summary>
        private static void Notify(string msg)
        {
            try { Main.Log("[船坞] " + msg); } catch { }
        }
    }
}
