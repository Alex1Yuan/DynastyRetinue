using System;
using System.Collections.Generic;
using UnityEngine;
using Kingmaker;
using Kingmaker.Enums;

namespace DynastyRetinue
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
        public const string YardKey  = "dynasty_ship_yard";

        public static void RegisterAll()
        {
            RecruitDialog.Register(new RecruitDialog.Entry
            {
                Guid       = YardGuid,
                TextKey    = YardKey,
                Text       = delegate { return L.T("（船坞）关于座舰的改装事宜……"); },
                Enabled    = delegate { return Main.Settings != null && Main.Settings.ShipDialogEntry; },
                // ★1.0.67 起也改成"选中即关对话"，和招募统一★
                //   原来是 true（留在对话里，好让顾问成交后还能说话，关窗回到顾问面前）。
                //   但留在对话里就意味着答案列表不重建 —— 刚点过的那一条**不变暗**，
                //   而原版点过的都会变暗，同一个列表两套显示逻辑。
                //   试过在选中时刷新、在关窗时刷新（1.0.63 / 1.0.64），都没能让它变暗。
                //   取舍：成交后回到顾问面前是"偶尔更顺手"，显示不一致是"每次都看得见"。
                //   代价：改装完会被带出对话，要重新和顾问说话。
                // KeepDialog 保持默认 false
                OnPicked   = UI.ShipYardUI.OpenFromDialog,
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
            int cruiser = Main.Settings.ShipPriceCruiser;
            // ★夹住：大巡总价不得低于巡洋总价★
            // 收费是差价制（PriceTo = 目标总价 − 已投入总价）。大巡价低于巡洋价会让
            // PriceTo(大巡) 变成负数 ⇒ 从巡洋"升级"到大巡反而**退钱**，
            // 而降级回巡洋又要收钱 —— 玩家可以反复横跳刷废料。
            // 夹在这里而不是 UI 里：PriceTo / RefundOnRevert / 两个窗口 / 面板
            // 全都经过 TotalFor，这是唯一必经之路；拦在 UI 里只要漏一处就破功。
            int grand = Main.Settings.ShipPriceGrand;
            if (grand < cruiser) grand = cruiser;

            if (sz == Size.GrandCruiser_3x6) return grand;
            if (sz == Size.Cruiser_2x4)      return cruiser;
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
        /// <summary>
        /// 换到 target 档的**净费用**。可以是负数 —— 那就是退款。
        ///
        /// ★别 clamp 到 0★ v0.44.0 就是那么写的，于是大巡→巡洋算出 500-1000 = -500
        /// 被吃成 0：免费降级但一分不退（玩家实测）。而升级只补差价的规则要成立，
        /// 降级就必须对称地退差价，否则"升上去再降回来"会白吞 500。
        /// </summary>
        public static int PriceTo(Size target)
        {
            return TotalFor(target) - TotalFor(Current());
        }

        /// <summary>净费用的人话说法。</summary>
        public static string PriceLabel(Size target)
        {
            int p = PriceTo(target);
            if (p > 0) return L.F("{0} 废料", p);
            if (p < 0) return L.F("退还 {0} 废料", -p);
            return L.T("无需补价");
        }

        /// <summary>还原到原本那档能退多少 —— 按当前档的总投入全额退。</summary>
        public static int RefundOnRevert()
        {
            int r = TotalFor(Current()) - TotalFor(OriginalSize());
            return r < 0 ? 0 : r;
        }

        public static string SizeName(Size s)
        {
            if (s == Size.GrandCruiser_3x6) return L.T("大巡洋舰");
            if (s == Size.Cruiser_2x4)      return L.T("巡洋舰");
            if (s == Size.Frigate_1x2)      return L.T("护卫舰");
            if (s == Size.Raider_1x1)       return L.T("劫掠舰");
            return s.ToString();
        }

        public static int Scrap()
        {
            try { return Game.Instance.Player.Scrap; } catch { return 0; }
        }

        // ---------------------------------------------------------------- 支持名单

        /// <summary>
        /// 一份"改装方案" = **(目标分档, 船体) 这一对**，不是单独一条船体。
        ///
        /// ★为什么必须成对★ 目录里 Dictator 的 Tier 写的是 Cruiser_2x4 —— 它本来就是
        /// 巡洋舰船体，我们只是把它按大巡的尺寸放大来用：
        ///     DefaultFor(GrandCruiser_3x6) => Cruiser_ImperialDictator
        /// 所以"船体的原生档"和"你买到的档"根本不是一回事。
        /// v0.43.1 把两者当成一回事，后果是 Dictator 被自己的判据判成"未调整好"、
        /// 而大巡那一组里只剩没校准的混沌/运输舰 ⇒ **大巡这档实际上买不到**。
        /// </summary>
        public sealed class Offer
        {
            public Size Tier;          // 买到手是什么档（决定价格、格子占位、加成）
            public ShipModel Model;    // 用哪个船体外观
            public bool Supported;     // 校准过、允许更换
        }

        /// <summary>
        /// 全部方案：**校准过的排前面**，其余照常列出但不给按钮。
        /// 列而不藏 —— 让玩家知道有这些船、也知道为什么点不了。
        /// </summary>
        public static List<Offer> Offers()
        {
            var list = new List<Offer>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tier in new[] { Size.Cruiser_2x4, Size.GrandCruiser_3x6 })
            {
                ShipModel d = null;
                try { d = ShipModelCatalog.DefaultFor(tier); } catch { }
                if (d == null) continue;
                list.Add(new Offer { Tier = tier, Model = d, Supported = true });
                taken.Add(tier + "|" + d.PrefabAssetId);
            }

            bool all = Main.Settings != null && Main.Settings.ShipYardUnlockAll;
            foreach (var m in ShipModelCatalog.All)
            {
                if (m == null) continue;
                if (taken.Contains(m.Tier + "|" + m.PrefabAssetId)) continue;
                // 护卫舰档不列：回原生船走底部那条「还原为原样」，语义更清楚也能退款
                if (m.Tier != Size.Cruiser_2x4 && m.Tier != Size.GrandCruiser_3x6) continue;
                list.Add(new Offer { Tier = m.Tier, Model = m, Supported = all });
            }
            return list;
        }

        /// <summary>
        /// 「未校准」的提示语。★是属性不是 const★ —— const 在编译期就定死了，
        /// 没法过 L.T；而 ShipYardUI 直接拿它拼字符串，只有在这里本地化，
        /// 两个窗口才会一起变英文。调用点写法不变（都是运行期取值）。
        /// </summary>
        public static string UnsupportedHint
        {
            get { return L.T("未调整好（挂点与缩放未在这条船体上校准）"); }
        }

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
        public static bool IsSupported(Size tier, ShipModel m)
        {
            if (m == null) return false;
            if (Main.Settings != null && Main.Settings.ShipYardUnlockAll) return true;
            if (tier != Size.Cruiser_2x4 && tier != Size.GrandCruiser_3x6) return false;
            try
            {
                // ★按目标分档查默认船体★ 不能拿 m.Tier 查 —— Dictator 的 m.Tier 是
                // Cruiser_2x4，而它正是大巡那档的默认船体。
                var def = ShipModelCatalog.DefaultFor(tier);
                return def != null && string.Equals(def.PrefabAssetId, m.PrefabAssetId,
                                                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 成交

        /// <summary>按方案改装。tier 是**买到手的档**，可能和 m.Tier 不同（大巡=放大的巡洋船体）。</summary>
        public static string BuyOffer(Size tier, ShipModel m)
        {
            try
            {
                if (m == null) return L.T("船坞里没有这份图纸。");
                // ★兜底放在这里而不是 UI 里★ 两个窗口共用这条路，
                // 任何一边漏了判断都不会让未校准的船体真的换上去。
                if (!IsSupported(tier, m))
                    return L.F("这条船体船坞还没调校好，暂不承接。（{0}）", UnsupportedHint);
                int price = PriceTo(tier);          // 负数 = 该退给玩家
                int have  = Scrap();
                if (price > 0 && have < price)
                    return L.F("废料不够 —— 需要 {0}，账上只有 {1}。（一枚都没扣。）", price, have);

                // ★先换船再扣钱★ 换船可能被拒（战斗中 StarshipTool.SetSize 会拒），
                // 顺序反了就是"钱花了船没换"。宁可白换不能白扣。
                if (!StarshipViewTool.ApplyModelAtTier(m, tier))
                    return L.T("现在动不了船坞（在战斗中？）。废料未扣除。");

                if (price > 0)
                {
                    try { Game.Instance.Player.Scrap.Spend(price); }
                    catch (Exception e) { Main.LogError("[船坞] ★船已改装但废料扣除失败★: " + e.Message); }
                }
                else if (price < 0)
                {
                    // 降级退差价。和升级只补差价是同一条规则的两半 ——
                    // 只做一半的话，"升上去再降回来"会白吞玩家 500。
                    try { Game.Instance.Player.Scrap.Receive(-price); }
                    catch (Exception e) { Main.LogError("[船坞] ★船已改装但退款失败★: " + e.Message); }
                }
                Main.Log("[船坞] 成交 -> " + m.Hull + " @ " + tier + "　净费用 " + price + "　余额 " + Scrap());
                // ★三句各自成句★ 不要"前半段 + 三选一的尾巴"那种拼法：
                // 英文里收款/退款/免费三种说法的语序都不一样，拆成片段必然错位。
                if (price > 0)
                    return L.F("改装完成。您的座舰现在是一艘{0}（船体：{1}），船坞收讫 {2} 单位废料。",
                               SizeName(tier), m.HullName, price);
                if (price < 0)
                    return L.F("改装完成。您的座舰现在是一艘{0}（船体：{1}），船坞退还 {2} 单位废料。",
                               SizeName(tier), m.HullName, -price);
                return L.F("改装完成。您的座舰现在是一艘{0}（船体：{1}），本次无需补价。",
                           SizeName(tier), m.HullName);
            }
            catch (Exception e) { Main.LogError("[船坞] 交易异常: " + e); return L.T("船坞出了点岔子，交易未完成。"); }
        }

        /// <summary>升级到 target 档的默认船体。</summary>
        public static string Buy(Size target)
        {
            try
            {
                if (Current() == target) return L.F("座舰已经是{0}了。", SizeName(target));

                int price = PriceTo(target);
                int have  = Scrap();
                if (have < price)
                    return L.F("废料不够 —— 需要 {0}，账上只有 {1}。还差 {2}。（一枚都没扣。）",
                               price, have, price - have);

                var model = ShipModelCatalog.DefaultFor(target);
                if (model == null) return L.T("船坞里没有对应的船体图纸，交易取消，废料未扣。");

                // ★先换船再扣钱★ 换船可能被拒（战斗中 StarshipTool.SetSize 会拒），
                // 顺序反了就是"钱花了船没换"。宁可白换不能白扣。
                if (!StarshipViewTool.ApplyModelAtTier(model, target))
                    return L.T("现在动不了船坞（在战斗中？）。废料未扣除。");

                try { Game.Instance.Player.Scrap.Spend(price); }
                catch (Exception e) { Main.LogError("[船坞] ★船已改装但废料扣除失败★: " + e.Message); }

                Main.Log("[船坞] 成交 -> " + SizeName(target) + "　花费 " + price + "　余额 " + Scrap());
                return L.F("改装完成。您的座舰现在是一艘{0}了，船坞收讫 {1} 单位废料。",
                           SizeName(target), price);
            }
            catch (Exception e) { Main.LogError("[船坞] 交易异常: " + e); return L.T("船坞出了点岔子，交易未完成。"); }
        }

        /// <summary>还原成玩家原本那条船，并退还废料。</summary>
        public static string Revert()
        {
            try
            {
                var orig = OriginalSize();
                if (Current() == orig) return L.F("座舰本来就是{0}，无需还原。", SizeName(orig));

                int refund = RefundOnRevert();

                // 还原走 RevertAll：它同时把 m_CustomPrefabGuid 清空、把 Size 设回 OriginalSize。
                // 只改一样会留下"新模型 + 旧档位"或反过来的中间态。
                if (!StarshipViewTool.RevertAll())
                    return L.T("现在动不了船坞（在战斗中？）。什么都没改。");

                if (refund > 0)
                {
                    try { Game.Instance.Player.Scrap.Receive(refund); }
                    catch (Exception e) { Main.LogError("[船坞] 退款失败: " + e.Message); }
                }
                Main.Log("[船坞] 已还原为 " + SizeName(orig) + "　退款 " + refund + "　余额 " + Scrap());
                return L.F("已按原样复原。您的座舰重新是一艘{0}，船坞退还 {1} 单位废料。",
                           SizeName(orig), refund);
            }
            catch (Exception e) { Main.LogError("[船坞] 还原异常: " + e); return L.T("船坞出了点岔子，还原未完成。"); }
        }
    }
}
