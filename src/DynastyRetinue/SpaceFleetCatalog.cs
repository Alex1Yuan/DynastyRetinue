using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.Enums;
using Kingmaker.Items.Slots;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Warhammer.SpaceCombat.Blueprints;
using Warhammer.SpaceCombat.Blueprints.Slots;

namespace DynastyRetinue
{
    internal sealed class SpaceFleetSlotDef
    {
        public string Key;
        public string OriginalItemGuid;
        public bool IsWeapon;
        public WeaponSlotType WeaponType;
        public int WeaponIndex;
    }

    internal sealed class SpaceFleetWeaponDef
    {
        public string ItemGuid;
        public string AbilityGuid;
        public string Role;
        public WeaponSlotType[] AllowedSlots;
    }

    internal sealed class SpaceFleetHullDef
    {
        public int Class;
        public int Capacity;
        public string Name;
        public string BlueprintGuid;
        public Size Size;
        public string OriginalFactionGuid;
        public string BrainGuid;
        public string PrefabAssetId;
        public int BroadsideWeaponSlots;
        public int NonBroadsideWeaponSlots;
        public SpaceFleetSlotDef[] Slots;
        public string[] NativeChildShipGuids = new string[0];
    }

    /// <summary>
    /// 海战舰队唯一槽位目录。这里只有固定的支持舰与已验证原装拓扑；
    /// UI、预算、payload 和实际插装必须共用这份定义，不能各自解释槽位。
    /// </summary>
    internal static class SpaceFleetCatalog
    {
        internal const int RosterDataVersion = 3;
        internal const int MaxExtraShots = 4;
        internal const int MaxExtraRange = 8;

        internal const string FrigateGuid = "0c893e03a8a34415a4eeb59d7fc2e34a";
        internal const string ProwLanceFrigateGuid = "46d0b800ee874f79923cf078764acd21";
        internal const string CruiserGuid = "b220571fee6e4593868e9363401ebdfc";
        internal const string GrandCruiserGuid = "f29445dd202d4d98b9bc2ca0e4ce6192";
        internal const string PirateCruiserGuid = "c41bae2a60d24abd99d0663c439f37e1";
        internal const string ChaosGrandCruiserGuid = "c8537d688ad243b7a1c7d9f5c4c637c9";

        internal static readonly SpaceFleetWeaponDef[] RefitWeapons =
        {
            Refit("be242f51ce154c2ca3f80bce46ddde1c", "0277b7ed853b45bd8ced19152549fffb", "lance-prow", WeaponSlotType.Prow),
            Refit("ea7915bdd9b949f8864bb3c6d7b96ead", "029a3469ff0c42e7ae82ea7be69c3170", "lance-prow", WeaponSlotType.Prow),
            Refit("95200d41fd964283b29c9c67880fe12a", "e0a57e5237fa4bd6ad30ccb3e5c3332a", "lance-dorsal", WeaponSlotType.Dorsal),
            Refit("1a2ba3cdac504d7d8d0590619c669f37", "78a2a587f1f54b9e8d61caccc1007c97", "macro-dorsal", WeaponSlotType.Dorsal),
            Refit("6518daf8214046c9a928cafc8a06715a", "fff1e6afd2e648008910de64852bac2d", "macro-dorsal", WeaponSlotType.Dorsal),
            Refit("7bdc12cf63124a86b41cc7ca929d015d", "e9fc2a755be34a32999cd0444b88d3ca", "macro-side", WeaponSlotType.Port, WeaponSlotType.Starboard),
            Refit("4f1fc55e29314ddcbc3ee34b459db589", "38b3a02d43484cfba6ea188f17ea0763", "macro-side", WeaponSlotType.Port, WeaponSlotType.Starboard),
            Refit("9a8850f0b6604093a9ffbcfdcceeb00b", "02ea1871911548849faa21e57eb8a928", "macro-side", WeaponSlotType.Port, WeaponSlotType.Starboard),
            Refit("f755371dfed041328d6059095b1b233a", "ea20718e8d4c47a7bc9b1c3c6d491bb3", "macro-side", WeaponSlotType.Port, WeaponSlotType.Starboard),
            Refit("ac6b7fde51814bbfa915a11e13d44cbb", "7cf609ea88004b71b56dd81ac937d14d", "macro-side", WeaponSlotType.Port, WeaponSlotType.Starboard)
        };

        private static readonly string[] ComponentKeys =
        {
            "component:PlasmaDrives",
            "component:VoidShieldGenerator",
            "component:AugerArray",
            "component:ArmorPlating"
        };

        private static readonly SpaceFleetHullDef[] Hulls =
        {
            Hull(0, 1, "剑级护卫舰", FrigateGuid, Size.Frigate_1x2,
                "b3979964b7a34dbca355c7a05eeb3f5c",
                "c534f2c31ac94ac8adb0409e42674a16",
                "a6bcda106bf8fd44da4286ee04a3ad8f",
                new[]
                {
                    "d5bf0322251c49efa60de5543a8b42a9",
                    "5066a9d95a0a4a3787e9e3e569505e60",
                    "8979e844fa504183a12b7104f8f7d1d0",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Port, 0, "9a8850f0b6604093a9ffbcfdcceeb00b"),
                    Weapon(WeaponSlotType.Starboard, 0, "9a8850f0b6604093a9ffbcfdcceeb00b")
                }),
            Hull(0, 1, "舰首光矛护卫舰", ProwLanceFrigateGuid, Size.Frigate_1x2,
                "419c5165bae99664b92252b7dcc0a2db",
                "53b6f261df644fd6b3313ee53e80df6f",
                "a6bcda106bf8fd44da4286ee04a3ad8f",
                new[]
                {
                    "d5bf0322251c49efa60de5543a8b42a9",
                    "af716bb78c5645f791758f53756a8c25",
                    "617767497a4947539ddc491ee4cc280f",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Dorsal, 0, "6518daf8214046c9a928cafc8a06715a"),
                    Weapon(WeaponSlotType.Prow, 0, "ea7915bdd9b949f8864bb3c6d7b96ead")
                }),
            Hull(1, 2, "巡洋舰", CruiserGuid, Size.Cruiser_2x4,
                "419c5165bae99664b92252b7dcc0a2db",
                "11dcfb2c30974dfdb0cb9bf6bf49717c",
                "67017c4dd1d5c1c40979ce2fc1cd38b2",
                new[]
                {
                    "6040ad7e9305401f8e4a7b2496125ee8",
                    "d61478c85ee043a0b3af3974b0dfd465",
                    "617767497a4947539ddc491ee4cc280f",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Port, 0, "488e68fd98544e9993e3bdd27a23b416"),
                    Weapon(WeaponSlotType.Port, 1, "488e68fd98544e9993e3bdd27a23b416"),
                    Weapon(WeaponSlotType.Starboard, 0, "488e68fd98544e9993e3bdd27a23b416"),
                    Weapon(WeaponSlotType.Starboard, 1, "488e68fd98544e9993e3bdd27a23b416"),
                    Weapon(WeaponSlotType.Prow, 0, "ec42b6f848db450fbc935931ee365f6b")
                }),
            Hull(2, 3, "大巡洋舰", GrandCruiserGuid, Size.GrandCruiser_3x6,
                "0f539babafb47fe4586b719d02aff7c4",
                "954cee2535994732aab39aff83e0cf7a",
                "0ea91ee80d7b01a44b3cad74efbc8a72",
                new[]
                {
                    "4e2844aed165481bb1229ab465c90cd0",
                    "748fa88b93dc491f91af3d8593583336",
                    "617767497a4947539ddc491ee4cc280f",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Dorsal, 0, "ef43b2e7bba440a68931ab2c454aaded"),
                    Weapon(WeaponSlotType.Dorsal, 1, "ef43b2e7bba440a68931ab2c454aaded"),
                    Weapon(WeaponSlotType.Port, 0, "4f1fc55e29314ddcbc3ee34b459db589"),
                    Weapon(WeaponSlotType.Starboard, 0, "4f1fc55e29314ddcbc3ee34b459db589")
                }),
            // 完整原生型号：载机/撤退能力来自蓝图 AddFacts，保留而不加入换装目录。
            Hull(1, 2, "海盗 Dictator 级巡洋舰", PirateCruiserGuid, Size.Cruiser_2x4,
                "0f539babafb47fe4586b719d02aff7c4",
                "5bf5340b86bd4c4382767222bce04af0",
                "8c34d0a2f4987134c8a625612476e22d",
                new[]
                {
                    "4e2844aed165481bb1229ab465c90cd0",
                    "1c5aa383f0c648498950c99d946bdcad",
                    "31d94baf5a694af2b15bd7ba8443c9b7",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Port, 0, "4f1fc55e29314ddcbc3ee34b459db589"),
                    Weapon(WeaponSlotType.Port, 1, "4f1fc55e29314ddcbc3ee34b459db589"),
                    Weapon(WeaponSlotType.Starboard, 0, "4f1fc55e29314ddcbc3ee34b459db589"),
                    Weapon(WeaponSlotType.Starboard, 1, "4f1fc55e29314ddcbc3ee34b459db589"),
                    Weapon(WeaponSlotType.Port, 2, "3f4e4b7279e7433db5dc44012b056310"),
                    Weapon(WeaponSlotType.Starboard, 2, "3f4e4b7279e7433db5dc44012b056310")
                }, "98a148e3643a4bfdb538c6a100e265bb"),
            Hull(2, 3, "混沌战列巡洋舰", ChaosGrandCruiserGuid, Size.GrandCruiser_3x6,
                "0f539babafb47fe4586b719d02aff7c4",
                "4b2fb551dbd043b3afefc807be89fc0b",
                "0da2b98b8cef1b8498dad3ecb12cfb6b",
                new[]
                {
                    "f376a3a62c3842f486ad02cbe431f1f1",
                    "2add086b20d54d9f87a09eb6daeff11b",
                    "6ba2393fbb634fa6a309e9d53f69a3b8",
                    ""
                },
                new[]
                {
                    Weapon(WeaponSlotType.Dorsal, 0, "4caf3520422148e8b5c2ec352ae09602"),
                    Weapon(WeaponSlotType.Port, 0, "4c642f9e138b49e7bc1147a25a04d79a"),
                    Weapon(WeaponSlotType.Port, 1, "4c642f9e138b49e7bc1147a25a04d79a"),
                    Weapon(WeaponSlotType.Starboard, 0, "4c642f9e138b49e7bc1147a25a04d79a"),
                    Weapon(WeaponSlotType.Starboard, 1, "4c642f9e138b49e7bc1147a25a04d79a")
                }, "4f9fe1dac5614d98b7e2b38d2c684a7a")
        };

        internal static SpaceFleetHullDef Find(string blueprintGuid)
        {
            if (string.IsNullOrEmpty(blueprintGuid)) return null;
            for (int i = 0; i < Hulls.Length; i++)
                if (string.Equals(Hulls[i].BlueprintGuid, blueprintGuid,
                    StringComparison.OrdinalIgnoreCase))
                    return Hulls[i];
            return null;
        }

        internal static string DisplayName(SpaceFleetHullDef hull)
        {
            if (hull == null) return L.T("未知舰型");
            if (string.Equals(hull.BlueprintGuid, FrigateGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("剑级护卫舰");
            if (string.Equals(hull.BlueprintGuid, ProwLanceFrigateGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("舰首光矛护卫舰");
            // 舰级由分组标题表达；型号按蓝图身份显示，不能把原生运输舰叫成座舰改装的 Dictator。
            if (string.Equals(hull.BlueprintGuid, CruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("帝国 Gothic 级巡洋舰");
            if (string.Equals(hull.BlueprintGuid, GrandCruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("帝国 Universe 级质量运输舰");
            if (string.Equals(hull.BlueprintGuid, PirateCruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("海盗 Dictator 级巡洋舰");
            if (string.Equals(hull.BlueprintGuid, ChaosGrandCruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("混沌战列巡洋舰");
            if (hull.Class == 1) return L.T("巡洋舰");
            if (hull.Class == 2) return L.T("大巡洋舰");
            return L.T("护卫舰");
        }

        internal static string NativeAbilityNotes(SpaceFleetHullDef hull)
        {
            if (hull == null) return "";
            if (string.Equals(hull.BlueprintGuid, PirateCruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("原生舰载机；海盗低血量撤退");
            if (string.Equals(hull.BlueprintGuid, ChaosGrandCruiserGuid, StringComparison.OrdinalIgnoreCase))
                return L.T("原生舰载机");
            return "";
        }

        internal static SpaceFleetHullDef[] All()
        {
            return (SpaceFleetHullDef[])Hulls.Clone();
        }

        internal static SpaceFleetSlotDef FindSlot(SpaceFleetHullDef hull, string key)
        {
            if (hull == null || hull.Slots == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < hull.Slots.Length; i++)
                if (string.Equals(hull.Slots[i].Key, key, StringComparison.Ordinal))
                    return hull.Slots[i];
            return null;
        }

        internal static SpaceFleetWeaponDef FindRefitWeapon(string itemGuid)
        {
            if (string.IsNullOrEmpty(itemGuid)) return null;
            for (int i = 0; i < RefitWeapons.Length; i++)
                if (string.Equals(RefitWeapons[i].ItemGuid, itemGuid,
                    StringComparison.OrdinalIgnoreCase)) return RefitWeapons[i];
            return null;
        }

        internal static bool CanInstallWeapon(SpaceFleetHullDef hull, string slotKey,
            string itemGuid, out SpaceFleetWeaponDef weapon)
        {
            weapon = FindRefitWeapon(itemGuid);
            var slot = FindSlot(hull, slotKey);
            if (weapon == null || slot == null || !slot.IsWeapon) return false;
            bool allowed = false;
            for (int i = 0; i < weapon.AllowedSlots.Length; i++)
                if (weapon.AllowedSlots[i] == slot.WeaponType) { allowed = true; break; }
            string failure;
            return allowed && ValidateRefitWeapon(weapon, out failure);
        }

        internal static bool ValidateRefitWeapon(SpaceFleetWeaponDef weapon,
            out string failure)
        {
            failure = "";
            if (weapon == null || string.IsNullOrEmpty(weapon.ItemGuid)
                || string.IsNullOrEmpty(weapon.AbilityGuid)
                || string.IsNullOrEmpty(weapon.Role))
            { failure = "目录字段为空"; return false; }
            var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarshipWeapon>(weapon.ItemGuid);
            if (bp == null) { failure = "武器蓝图不可用"; return false; }
            if (weapon.AllowedSlots == null || weapon.AllowedSlots.Length == 0)
            { failure = "目录未声明槽位"; return false; }
            for (int i = 0; i < weapon.AllowedSlots.Length; i++)
                if (bp.AllowedSlots == null || !bp.AllowedSlots.Contains(weapon.AllowedSlots[i]))
                { failure = "蓝图不允许目录槽位 " + weapon.AllowedSlots[i]; return false; }
            BlueprintAbility ability = null;
            try
            {
                ability = bp.Abilities.FirstOrDefault(x => x != null && string.Equals(
                    x.AssetGuid.ToString(), weapon.AbilityGuid, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            if (ability == null) { failure = "武器不含目录 target ability"; return false; }
            return SpaceFleetAiProfiles.Validate(weapon.Role, ability, out failure);
        }

        // Code.dll 的 StarshipEquipmentSlot<T> 使用 Blueprint 类型约束。
        // 只映射目录的四个主组件，不枚举隐藏装备槽。
        internal static Type ComponentBlueprintType(SpaceFleetSlotDef slot)
        {
            if (slot == null || slot.IsWeapon) return null;
            switch (slot.Key)
            {
                case "component:PlasmaDrives": return typeof(BlueprintItemPlasmaDrives);
                case "component:VoidShieldGenerator": return typeof(BlueprintItemVoidShieldGenerator);
                case "component:AugerArray": return typeof(BlueprintItemAugerArray);
                case "component:ArmorPlating": return typeof(BlueprintItemArmorPlating);
                default: return null;
            }
        }

        internal static bool IsOpenSlot(SpaceFleetSlotDef slot)
        { return slot != null && (slot.IsWeapon || ComponentBlueprintType(slot) != null); }

        internal static ItemSlot ComponentSlot(
            Warhammer.SpaceCombat.StarshipLogic.Equipment.HullSlots slots, SpaceFleetSlotDef slot)
        {
            if (slots == null || ComponentBlueprintType(slot) == null) return null;
            switch (slot.Key)
            {
                case "component:PlasmaDrives": return slots.PlasmaDrives;
                case "component:VoidShieldGenerator": return slots.VoidShieldGenerator;
                case "component:AugerArray": return slots.AugerArray;
                case "component:ArmorPlating": return slots.ArmorPlating;
                default: return null;
            }
        }

        internal static bool TryResolveItem(SpaceFleetHullDef hull, string slotKey,
            string itemGuid, out BlueprintStarshipItem item, out SpaceFleetWeaponDef weapon)
        {
            item = null;
            weapon = null;
            var slot = FindSlot(hull, slotKey);
            if (!IsOpenSlot(slot)) return false;
            bool original = string.Equals(itemGuid ?? "", slot.OriginalItemGuid ?? "",
                StringComparison.OrdinalIgnoreCase);
            // 空值只表示原装空槽；不允许卸空非空原装槽。
            if (string.IsNullOrEmpty(itemGuid)) return original;
            try { item = ResourcesLibrary.TryGetBlueprint<BlueprintStarshipItem>(itemGuid); }
            catch { return false; }
            if (item == null) return false;
            if (slot.IsWeapon)
                return item is BlueprintStarshipWeapon
                    && (original || CanInstallWeapon(hull, slotKey, itemGuid, out weapon));
            return ComponentBlueprintType(slot).IsInstanceOfType(item);
        }

        internal static bool ValidateOriginalComponents(BlueprintStarship bp,
            SpaceFleetHullDef hull, out string failure)
        {
            failure = "";
            if (bp == null || bp.HullSlots == null || hull == null)
            { failure = "主组件槽不可用"; return false; }
            foreach (var slot in hull.Slots)
            {
                if (slot == null || slot.IsWeapon) continue;
                BlueprintStarshipItem item;
                switch (slot.Key)
                {
                    case "component:PlasmaDrives": item = bp.HullSlots.PlasmaDrives; break;
                    case "component:VoidShieldGenerator": item = bp.HullSlots.VoidShieldGenerator; break;
                    case "component:AugerArray": item = bp.HullSlots.AugerArray; break;
                    case "component:ArmorPlating": item = bp.HullSlots.ArmorPlating; break;
                    default: failure = "主组件目录含隐藏槽 " + slot.Key; return false;
                }
                if (!string.Equals(item != null ? item.AssetGuid.ToString() : "",
                    slot.OriginalItemGuid ?? "", StringComparison.OrdinalIgnoreCase)
                    || (item != null && !ComponentBlueprintType(slot).IsInstanceOfType(item)))
                { failure = "原装主组件不匹配 " + slot.Key; return false; }
            }
            return true;
        }

        internal static int ModifiedOpenSlotCount(SpaceFleetEntry entry)
        {
            var hull = entry != null ? Find(entry.BlueprintGuid) : null;
            if (hull == null || entry.Loadout == null || entry.Loadout.Count == 0) return 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int result = 0;
            for (int i = 0; i < entry.Loadout.Count; i++)
            {
                var choice = entry.Loadout[i];
                if (choice == null || string.IsNullOrEmpty(choice.SlotKey)
                    || string.IsNullOrEmpty(choice.ItemGuid)
                    || !seen.Add(choice.SlotKey))
                    continue;
                var slot = FindSlot(hull, choice.SlotKey);
                if (slot == null) continue;
                if (!string.Equals(slot.OriginalItemGuid ?? "", choice.ItemGuid,
                    StringComparison.OrdinalIgnoreCase))
                    result++;
            }
            return result;
        }

        private static SpaceFleetHullDef Hull(int cls, int capacity, string name,
            string guid, Size size, string factionGuid, string brainGuid,
            string prefabAssetId, string[] components, SpaceFleetSlotDef[] weapons,
            params string[] nativeChildShipGuids)
        {
            if (components == null || components.Length != ComponentKeys.Length)
                throw new ArgumentException("舰船主组件原装表必须恰好有 4 项", "components");
            var slots = new List<SpaceFleetSlotDef>(ComponentKeys.Length + weapons.Length);
            for (int i = 0; i < ComponentKeys.Length; i++)
                slots.Add(new SpaceFleetSlotDef
                {
                    Key = ComponentKeys[i], OriginalItemGuid = components[i] ?? "", IsWeapon = false,
                    WeaponType = WeaponSlotType.None, WeaponIndex = -1
                });
            slots.AddRange(weapons);

            int broadside = 0;
            for (int i = 0; i < weapons.Length; i++)
                if (weapons[i].WeaponType == WeaponSlotType.Port
                    || weapons[i].WeaponType == WeaponSlotType.Starboard)
                    broadside++;

            return new SpaceFleetHullDef
            {
                Class = cls,
                Capacity = capacity,
                Name = name,
                BlueprintGuid = guid,
                Size = size,
                OriginalFactionGuid = factionGuid,
                BrainGuid = brainGuid,
                PrefabAssetId = prefabAssetId,
                BroadsideWeaponSlots = broadside,
                NonBroadsideWeaponSlots = weapons.Length - broadside,
                Slots = slots.ToArray(),
                NativeChildShipGuids = nativeChildShipGuids ?? new string[0]
            };
        }

        private static SpaceFleetWeaponDef Refit(string itemGuid, string abilityGuid,
            string role, params WeaponSlotType[] slots)
        {
            return new SpaceFleetWeaponDef
            {
                ItemGuid = itemGuid,
                AbilityGuid = abilityGuid,
                Role = role,
                AllowedSlots = slots ?? new WeaponSlotType[0]
            };
        }

        private static SpaceFleetSlotDef Weapon(WeaponSlotType type, int index,
            string originalGuid)
        {
            return new SpaceFleetSlotDef
            {
                Key = "weapon:" + type + ":" + index,
                OriginalItemGuid = originalGuid,
                IsWeapon = true,
                WeaponType = type,
                WeaponIndex = index
            };
        }
    }
}
