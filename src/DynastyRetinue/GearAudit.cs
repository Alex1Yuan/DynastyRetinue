using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker.Blueprints.Items.Armors;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Items.Slots;

namespace DynastyRetinue
{
    /// <summary>
    /// 把在册卫兵**实际穿在身上**的装备逐槽导出。
    ///
    /// ★为什么不能看配表★
    ///   archetypes.json 说的是"该发什么"，和"最后穿上了什么"是两回事：
    ///     · 回退链可能落到第二、第三个候选（资格不够 / 剧情层级没解锁）
    ///     · 槽位冲突时后发的会挤掉先发的
    ///     · 双手武器会占掉副手
    ///   排查"这条线怎么比别人脆"这类问题，只有读装备栏才作数。
    ///
    /// ★护甲数值从蓝图取★
    ///   BlueprintItemArmor.DamageAbsorption / DamageDeflection 自带 override 判断
    ///   （没覆写就回落到 ArmorType 的值），所以直接读它就是最终值。
    /// </summary>
    internal static class GearAudit
    {
        public static string LastPath;

        public static string Export()
        {
            try
            {
                var guards = RetinueRegistry.All();
                var sb = new StringBuilder();
                sb.Append("卫兵装备清单　v").Append(BuildManifest.Version)
                  .Append("　共 ").Append(guards.Count).Append(" 名").AppendLine().AppendLine();

                // 按分型归组，方便横向对比同一条线
                var byArch = new Dictionary<int, List<BaseUnitEntity>>();
                for (int i = 0; i < guards.Count; i++)
                {
                    int ai = RetinueRegistry.ArchetypeOf(guards[i]);
                    List<BaseUnitEntity> list;
                    if (!byArch.TryGetValue(ai, out list)) { list = new List<BaseUnitEntity>(); byArch[ai] = list; }
                    list.Add(guards[i]);
                }

                foreach (var kv in byArch)
                {
                    var arch = kv.Key >= 0 ? Archetypes.Get(kv.Key) : null;
                    sb.Append("========== ").Append(arch != null ? arch.Name : "未知分型")
                      .Append("　").Append(kv.Value.Count).Append(" 名 ==========").AppendLine();

                    for (int i = 0; i < kv.Value.Count; i++) Dump(sb, kv.Value[i], arch);
                    sb.AppendLine();
                }

                string dir = Main.ModEntry != null ? Main.ModEntry.Path : ".";
                string path = System.IO.Path.Combine(dir, "dynasty_gear.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                LastPath = path;
                Main.Log("[装备清单] 已导出 " + guards.Count + " 名卫兵 -> " + path);
                return path;
            }
            catch (Exception e) { Main.LogError("[装备清单] 导出失败: " + e); return null; }
        }

        private static void Dump(StringBuilder sb, BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            try
            {
                bool elite = false;
                try { elite = arch != null && GearTool.EliteDefOf(g, arch) != null; } catch { }
                int lv = g.Progression != null ? g.Progression.CharacterLevel : 0;

                sb.Append("  ").Append(g.CharacterName ?? "(未命名)")
                  .Append("　lv").Append(lv)
                  .Append(elite ? "　★精英" : "　普通")
                  .Append("　蓝图=").Append(g.Blueprint != null ? g.Blueprint.name : "?")
                  .AppendLine();

                var body = g.Body;
                if (body == null) { sb.AppendLine("      无 Body"); return; }

                int n = 0;
                foreach (ItemSlot slot in body.EquipmentSlots)
                {
                    if (slot == null) continue;
                    ItemEntity it = slot.MaybeItem;
                    if (it == null) continue;
                    n++;
                    string slotName = slot.GetType().Name.Replace("Slot", "");
                    string itemName = it.Blueprint != null ? it.Blueprint.name : "?";
                    string extra = "";
                    var ar = it.Blueprint as BlueprintItemArmor;
                    if (ar != null)
                    {
                        // ★这就是排查"谁更脆"要看的两个数★
                        extra = "　吸收 " + ar.DamageAbsorption + "　偏转 " + ar.DamageDeflection
                              + (ar.Type != null ? "　[" + ar.Type.name + "]" : "");
                    }
                    sb.Append("      ").Append(slotName.PadRight(14)).Append(itemName).Append(extra).AppendLine();
                }
                if (n == 0) sb.AppendLine("      ★装备栏是空的★");
            }
            catch (Exception e) { sb.Append("      读取失败: ").Append(e.Message).AppendLine(); }
        }
    }
}
