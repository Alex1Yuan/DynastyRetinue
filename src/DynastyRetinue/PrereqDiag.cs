using System;
using System.Collections.Generic;
using System.Reflection;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Progression.Features;

namespace DynastyRetinue
{
    /// <summary>
    /// 前置诊断 —— 回答"这条天赋为什么一直不可选"。
    ///
    /// 起因：PsyRating4（特级灵能者）在 Assassin / Strategist 上始终是 B 类
    ///（出现在候选里、但永远不可选），而同样结构的 Executioner 却能点出来。
    /// 蓝图 blob 里只有组件 ID，读不到阈值，静态分析到此为止 ——
    /// 所以改成运行时把每个 Prerequisite 单独 evaluate 一遍，直接打出是哪一条没过。
    ///
    /// 实现取舍：PrerequisiteFact / PrerequisiteLevel 这两个最常见也最有信息量的
    /// 用具体类型读字段（Fact/MinRank/Level 都是 public），能打出"需要 X，现在 Y"；
    /// 其余类型（Composite 之类）走反射调 Meet/MeetsInternal，至少给出通过与否。
    /// 全程只读，不改任何状态。
    /// </summary>
    public static class PrereqDiag
    {
        /// <summary>前置是否**全部**满足。求值不出来的条目按"不满足"算 —— 宁可漏补，不可乱补。</summary>
        public static bool AllMet(BaseUnitEntity guard, BlueprintFeature bp)
        {
            if (guard == null || bp == null) return false;
            try
            {
                bool any = false;
                var comps = bp.ComponentsArray;
                if (comps != null)
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        if (c.GetType().Name.IndexOf("Prerequisite", StringComparison.Ordinal) < 0) continue;
                        any = true;
                        if (!Ok(guard, c)) return false;
                    }
                foreach (var p in Flatten(Member(bp, "Prerequisites")))
                {
                    any = true;
                    if (!Ok(guard, p)) return false;
                }
                return any;   // 一条前置都没有的，不走补选（那类多半另有门控）
            }
            catch { return false; }
        }

        private static bool Ok(BaseUnitEntity guard, object c)
        {
            string tn = c.GetType().Name;
            try
            {
                if (tn == "PrerequisiteFact")
                {
                    var fact = Prop(c, "Fact") as BlueprintUnitFact;
                    int min = 1; try { min = (int)Field(c, "MinRank"); } catch { }
                    var f = guard.Facts.Get(fact);
                    return f != null && f.GetRank() >= Math.Max(min, 1);
                }
                if (tn == "PrerequisiteLevel")
                    return guard.Progression.CharacterLevel >= (int)Field(c, "Level");
                var r = TryMeet(c, guard);
                return r.HasValue && r.Value;
            }
            catch { return false; }
        }

        /// <summary>逐条评估 bp 的前置，返回可读结论。没有前置返回空串。</summary>
        public static string Explain(BaseUnitEntity guard, BlueprintFeature bp)
        {
            if (guard == null || bp == null) return "";
            var parts = new List<string>();
            try
            {
                // 两处都要看：
                //  ① ComponentsArray —— 老式 Prerequisite_Obsolete 挂在这
                //  ② bp.Prerequisites（PrerequisitesList）—— 新式 PrerequisiteFact/Level/Composite 在这。
                // v0.9.4 只扫了 ①，结果 PsyRating4 一条都没打出来。
                var comps = bp.ComponentsArray;
                if (comps != null)
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        string tn = c.GetType().Name;
                        if (tn.IndexOf("Prerequisite", StringComparison.Ordinal) < 0) continue;
                        parts.Add(One(guard, c, tn));
                    }

                foreach (var p in Flatten(Member(bp, "Prerequisites")))
                    parts.Add(One(guard, p, p.GetType().Name));
            }
            catch (Exception e) { return "（前置诊断失败: " + e.Message + "）"; }
            return parts.Count == 0 ? "" : string.Join("  |  ", parts.ToArray());
        }

        /// <summary>
        /// 把 PrerequisitesList 里的条目摊平。
        ///
        /// 它的内部结构没有反编译到手，所以走反射：遍历所有字段/属性，
        /// 凡是可枚举的就把里面类型名带 Prerequisite 的挑出来。
        /// Composite 会嵌套，所以递归一层。
        /// </summary>
        private static IEnumerable<object> Flatten(object node)
        {
            var res = new List<object>();
            Collect(node, res, 0);
            return res;
        }

        private static void Collect(object node, List<object> outp, int depth)
        {
            if (node == null || depth > 3 || outp.Count > 40) return;
            var t = node.GetType();
            if (t.Name.IndexOf("Prerequisite", StringComparison.Ordinal) >= 0
                && t.Name.IndexOf("List", StringComparison.Ordinal) < 0)
            {
                outp.Add(node);
                // Composite 内部还有子条目，继续往下挖
            }
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                foreach (var f in t.GetFields(BF)) Dive(f.GetValue(node), outp, depth);
                foreach (var p in t.GetProperties(BF))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null;
                    try { v = p.GetValue(node, null); } catch { }
                    Dive(v, outp, depth);
                }
            }
            catch { }
        }

        private static void Dive(object v, List<object> outp, int depth)
        {
            if (v == null || v is string) return;
            var vt = v.GetType();
            // 枚举要排掉：PrerequisiteComposite 上的 And/Or 字段类型名里也带 Prerequisite，
            // 不排的话会被当成一条真前置收进来，打出没法求值的 "FeatureComposition ?"。
            if (vt.IsPrimitive || vt.IsEnum) return;
            var seq = v as System.Collections.IEnumerable;
            if (seq != null)
            {
                foreach (var it in seq) Collect(it, outp, depth + 1);
                return;
            }
            if (vt.Name.IndexOf("Prerequisite", StringComparison.Ordinal) >= 0)
                Collect(v, outp, depth + 1);
        }

        private static string One(BaseUnitEntity guard, object c, string tn)
        {
            try
            {
                if (tn == "PrerequisiteFact")
                {
                    var fact = Prop(c, "Fact") as BlueprintUnitFact;
                    int min = 1; try { min = (int)Field(c, "MinRank"); } catch { }
                    int have = -1;
                    try
                    {
                        var f = guard.Facts.Get(fact);
                        have = f == null ? 0 : f.GetRank();
                    }
                    catch { }
                    string nm = fact == null ? "?" : (string.IsNullOrEmpty(fact.Name) ? fact.name : fact.Name);
                    bool ok = have >= Math.Max(min, 1);
                    return "需[" + nm + (min > 1 ? " rank>=" + min : "") + "] 现有" + (have < 0 ? "?" : have.ToString())
                           + (ok ? " ✓" : " ✗");
                }
                if (tn == "PrerequisiteLevel")
                {
                    int need = (int)Field(c, "Level");
                    int now = guard.Progression.CharacterLevel;
                    return "需等级>=" + need + " 现" + now + (now >= need ? " ✓" : " ✗");
                }
                // 其它类型（Composite 等）：反射调求值方法，至少给通过与否
                bool? r = TryMeet(c, guard);
                string cap = TryCaption(c);
                return tn.Replace("Prerequisite", "") + (string.IsNullOrEmpty(cap) ? "" : "[" + cap + "]")
                       + (r.HasValue ? (r.Value ? " ✓" : " ✗") : " ?");
            }
            catch (Exception e) { return tn + "(读取失败:" + e.Message + ")"; }
        }

        private static bool? TryMeet(object c, BaseUnitEntity guard)
        {
            foreach (var name in new[] { "Meet", "Meets", "MeetsInternal" })
            {
                try
                {
                    var m = c.GetType().GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m == null) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(guard)) continue;
                    return (bool)m.Invoke(c, new object[] { guard });
                }
                catch { }
            }
            return null;
        }

        private static string TryCaption(object c)
        {
            foreach (var name in new[] { "GetCaption", "GetCaptionInternal" })
            {
                try
                {
                    var m = c.GetType().GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m == null || m.GetParameters().Length != 0) continue;
                    var s = m.Invoke(c, null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            return "";
        }

        /// <summary>先按属性取，取不到再按字段取。
        /// BlueprintFeature.Prerequisites 是**字段**不是属性（bp_feature.cs:53），
        /// v0.9.5 只查了属性，结果整棵前置树根本没走到，诊断一条都不打。</summary>
        private static object Member(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var p = o.GetType().GetProperty(name, BF);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch { }
            try
            {
                var f = o.GetType().GetField(name, BF);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        private static object Prop(object o, string name)
        {
            var p = o.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(o, null);
        }

        private static object Field(object o, string name)
        {
            var f = o.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }
    }
}
