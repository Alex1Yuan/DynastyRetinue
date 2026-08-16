using System;
using UnityEngine;
using Kingmaker;
using Kingmaker.Enums;

namespace KgdRetinue
{
    /// <summary>
    /// 船坞：用废料改装座舰。**一条对话选项 + 一个子菜单窗口**。
    ///
    /// 为什么不做成两条并列的对话选项（v0.38.0 那样）：
    ///   · 价格随当前分档变（巡洋→大巡只补差价），并列选项要各自维护文案；
    ///   · 还原/退款需要第三条，主菜单会被我们塞满；
    ///   · 成交后要有顾问的台词，而并列选项一选就得关对话。
    /// 一条入口 + 自己的窗口，这三件事都变成普通 UI 逻辑，不用去造 BlueprintCue
    /// （造 cue 的风险和造 answer 同级：任何一个引用字段为 null 都会把整段对话打空）。
    ///
    /// 对话**不关闭** —— Entry.KeepDialog = true。玩家关掉窗口就回到顾问面前，
    /// 而不是被一脚踢出对话。
    ///
    /// ================= 存档安全 =================
    ///   · Scrap.Spend/Receive(int)                —— 纯数值
    ///   · StarshipTool.SetSize(Size)              —— vanilla 枚举
    ///   · StarshipViewTool.ApplyModelAtTier/RevertAll —— m_CustomPrefabGuid，裸 string
    /// 一个 mod 自建蓝图都不写进存档。
    /// </summary>
    public static class ShipDialog
    {
        public const string YardGuid = "kgd00001000010000100001000010002";
        public const string YardKey  = "kgd_ship_yard";

        public static void RegisterAll()
        {
            RecruitDialog.Register(new RecruitDialog.Entry
            {
                Guid       = YardGuid,
                TextKey    = YardKey,
                Text       = delegate { return "（船坞）关于座舰的改装事宜……"; },
                Enabled    = delegate { return Main.Settings != null && Main.Settings.ShipDialogEntry; },
                KeepDialog = true,          // 留在对话里，好让顾问说完话
                OnPicked   = UI.ShipYardUI.Open,
            });
        }

        // ---------------------------------------------------------------- 价格

        /// <summary>玩家这条船**原本**是什么档 —— 还原的目标，也是"没投入过"的基准。</summary>
        public static Size OriginalSize()
        {
            try
            {
                var s = StarshipViewTool.PlayerShip;
                return s != null ? s.OriginalSize : Size.Frigate_1x2;
            }
            catch { return Size.Frigate_1x2; }
        }

        /// <summary>某个分档对应的"总投入"。还原退款和升级差价都从这里推。</summary>
        public static int TotalFor(Size sz)
        {
            if (Main.Settings == null) return 0;
            if (sz == Size.GrandCruiser_3x6) return Main.Settings.ShipPriceGrand;
            if (sz == Size.Cruiser_2x4)      return Main.Settings.ShipPriceCruiser;
            return 0;   // 原生档（护卫舰等）不算投入
        }

        public static Size Current()
        {
            try { return StarshipTool.CurrentSize(); } catch { return OriginalSize(); }
        }

        /// <summary>
        /// 升到 target 还要补多少。**只补差价** —— 已经花过的不重复收。
        /// 巡洋(已付500) → 大巡(总价1000) = 500，正是玩家要的规则。
        /// </summary>
        public static int PriceTo(Size target)
        {
            int p = TotalFor(target) - TotalFor(Current());
            return p < 0 ? 0 : p;
        }

        /// <summary>还原到原本那档能退多少 —— 按当前档的总投入全额退。</summary>
        public static int RefundOnRevert()
        {
            int r = TotalFor(Current()) - TotalFor(OriginalSize());
            return r < 0 ? 0 : r;
        }

        public static string SizeName(Size s)
        {
            if (s == Size.GrandCruiser_3x6) return "大巡洋舰";
            if (s == Size.Cruiser_2x4)      return "巡洋舰";
            if (s == Size.Frigate_1x2)      return "护卫舰";
            if (s == Size.Raider_1x1)       return "劫掠舰";
            return s.ToString();
        }

        public static int Scrap()
        {
            try { return Game.Instance.Player.Scrap; } catch { return 0; }
        }

        // ---------------------------------------------------------------- 支持名单

        /// <summary>
        /// 这条船体是不是**校准过**的。
        ///
        /// 目录里那些船体（混沌战列巡洋舰、Universe 运输舰…）prefab 都能加载，
        /// 但挂点集合、缩放基准、舰首位置全都没在它们身上验过 ——
        /// 放出去只会让玩家撞上"炮飘在虚空/船大得离谱"这类我们已经花了很多轮才在
        /// Gothic 和 Dictator 上摆平的问题。
        ///
        /// 所以默认只开放：**每档的默认船体**（巡洋=Gothic、大巡=Dictator），
        /// 加上"还原为原样"回到玩家自己那条原生船。其余照常列出但不给按钮，
        /// 写明"未调整好" —— 让玩家知道有这些船、也知道为什么点不了，
        /// 比直接藏起来诚实。
        ///
        /// 想试的人可以在面板打开「解除船体限制」。
        /// </summary>
        public static bool IsSupported(ShipModel m)
        {
            if (m == null) return false;
            if (Main.Settings != null && Main.Settings.ShipYardUnlockAll) return true;
            if (m.Tier != Size.Cruiser_2x4 && m.Tier != Size.GrandCruiser_3x6) return false;
            try
            {
                var def = ShipModelCatalog.DefaultFor(m.Tier);
                return def != null && string.Equals(def.PrefabAssetId, m.PrefabAssetId,
                                                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public const string UnsupportedHint = "未调整好（挂点与缩放未在这条船体上校准）";

        // ---------------------------------------------------------------- 成交

        /// <summary>换成指定船体（含它自己的档位）。返回给玩家看的一句话。</summary>
        public static string BuyModel(ShipModel m)
        {
            try
            {
                if (m == null) return "船坞里没有这份图纸。";
                // ★兜底放在这里而不是 UI 里★ 两个窗口共用这条路，
                // 任何一边漏了判断都不会让未校准的船体真的换上去。
                if (!IsSupported(m))
                    return "这条船体船坞还没调校好，暂不承接。（" + UnsupportedHint + "）";
                int price = PriceTo(m.Tier);
                int have  = Scrap();
                if (have < price)
                    return "废料不够 —— 需要 " + price + "，账上只有 " + have + "。（一枚都没扣。）";

                // ★先换船再扣钱★ 换船可能被拒（战斗中 StarshipTool.SetSize 会拒），
                // 顺序反了就是"钱花了船没换"。宁可白换不能白扣。
                if (!StarshipViewTool.ApplyModelAtTier(m, m.Tier))
                    return "现在动不了船坞（在战斗中？）。废料未扣除。";

                if (price > 0)
                {
                    try { Game.Instance.Player.Scrap.Spend(price); }
                    catch (Exception e) { Main.LogError("[船坞] ★船已改装但废料扣除失败★: " + e.Message); }
                }
                Main.Log("[船坞] 成交 -> " + m.Hull + "（" + m.Tier + "）　花费 " + price + "　余额 " + Scrap());
                return "改装完成。您的座舰现在是一艘「" + m.Hull + "」，"
                     + (price > 0 ? "船坞收讫 " + price + " 单位废料。" : "本次无需补价。");
            }
            catch (Exception e) { Main.LogError("[船坞] 交易异常: " + e); return "船坞出了点岔子，交易未完成。"; }
        }

        /// <summary>升级到 target 档的默认船体。</summary>
        public static string Buy(Size target)
        {
            try
            {
                if (Current() == target) return "座舰已经是" + SizeName(target) + "了。";

                int price = PriceTo(target);
                int have  = Scrap();
                if (have < price)
                    return "废料不够 —— 需要 " + price + "，账上只有 " + have
                         + "。还差 " + (price - have) + "。（一枚都没扣。）";

                var model = ShipModelCatalog.DefaultFor(target);
                if (model == null) return "船坞里没有对应的船体图纸，交易取消，废料未扣。";

                // ★先换船再扣钱★ 换船可能被拒（战斗中 StarshipTool.SetSize 会拒），
                // 顺序反了就是"钱花了船没换"。宁可白换不能白扣。
                if (!StarshipViewTool.ApplyModelAtTier(model, target))
                    return "现在动不了船坞（在战斗中？）。废料未扣除。";

                try { Game.Instance.Player.Scrap.Spend(price); }
                catch (Exception e) { Main.LogError("[船坞] ★船已改装但废料扣除失败★: " + e.Message); }

                Main.Log("[船坞] 成交 -> " + SizeName(target) + "　花费 " + price + "　余额 " + Scrap());
                return "改装完成。您的座舰现在是一艘" + SizeName(target) + "了，"
                     + "船坞收讫 " + price + " 单位废料。";
            }
            catch (Exception e) { Main.LogError("[船坞] 交易异常: " + e); return "船坞出了点岔子，交易未完成。"; }
        }

        /// <summary>还原成玩家原本那条船，并退还废料。</summary>
        public static string Revert()
        {
            try
            {
                var orig = OriginalSize();
                if (Current() == orig) return "座舰本来就是" + SizeName(orig) + "，无需还原。";

                int refund = RefundOnRevert();

                // 还原走 RevertAll：它同时把 m_CustomPrefabGuid 清空、把 Size 设回 OriginalSize。
                // 只改一样会留下"新模型 + 旧档位"或反过来的中间态。
                if (!StarshipViewTool.RevertAll())
                    return "现在动不了船坞（在战斗中？）。什么都没改。";

                if (refund > 0)
                {
                    try { Game.Instance.Player.Scrap.Receive(refund); }
                    catch (Exception e) { Main.LogError("[船坞] 退款失败: " + e.Message); }
                }
                Main.Log("[船坞] 已还原为 " + SizeName(orig) + "　退款 " + refund + "　余额 " + Scrap());
                return "已按原样复原。您的座舰重新是一艘" + SizeName(orig) + "，"
                     + "船坞退还 " + refund + " 单位废料。";
            }
            catch (Exception e) { Main.LogError("[船坞] 还原异常: " + e); return "船坞出了点岔子，还原未完成。"; }
        }
    }
}
