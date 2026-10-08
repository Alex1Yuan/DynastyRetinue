using System;
using System.Collections.Generic;

namespace DynastyRetinue
{
    internal sealed class FleetBudgetBreakdown
    {
        public long Hull;
        public long Refit;
        public long Shots;
        public long Range;
        public long Total { get { return FleetBudget.Add(FleetBudget.Add(Hull, Refit), FleetBudget.Add(Shots, Range)); } }
    }

    internal sealed class FleetBudgetRates
    {
        public int Frigate;
        public int Cruiser;
        public int GrandCruiser;
        public int RefitPerSlot;
        public int PerShot;
        public int PerRange;
    }

    internal static class FleetBudget
    {
        internal static int Limit()
        {
            return ProfitFactorGate.Current();
        }

        internal static long Used(IEnumerable<SpaceFleetEntry> entries)
        {
            long total = 0;
            if (entries == null) return total;
            foreach (var entry in entries)
                total = Add(total, Cost(entry).Total);
            return total;
        }

        internal static FleetBudgetBreakdown Cost(SpaceFleetEntry entry)
        {
            return Cost(entry, CurrentRates());
        }

        internal static FleetBudgetBreakdown Cost(SpaceFleetEntry entry, FleetBudgetRates rates)
        {
            var result = new FleetBudgetBreakdown();
            var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
            if (hull == null || rates == null) return result;

            result.Hull = hull.Class == 2 ? NonNegative(rates.GrandCruiser)
                : hull.Class == 1 ? NonNegative(rates.Cruiser) : NonNegative(rates.Frigate);
            result.Refit = Mul(SpaceFleetCatalog.ModifiedOpenSlotCount(entry),
                NonNegative(rates.RefitPerSlot));
            result.Shots = Add(
                Mul(Mul(hull.BroadsideWeaponSlots, ClampBonus(entry.BroadsideExtraShots,
                    SpaceFleetCatalog.MaxExtraShots)), NonNegative(rates.PerShot)),
                Mul(Mul(hull.NonBroadsideWeaponSlots, ClampBonus(entry.NonBroadsideExtraShots,
                    SpaceFleetCatalog.MaxExtraShots)), NonNegative(rates.PerShot)));
            result.Range = Add(
                Mul(Mul(hull.BroadsideWeaponSlots, ClampBonus(entry.BroadsideExtraRange,
                    SpaceFleetCatalog.MaxExtraRange)), NonNegative(rates.PerRange)),
                Mul(Mul(hull.NonBroadsideWeaponSlots, ClampBonus(entry.NonBroadsideExtraRange,
                    SpaceFleetCatalog.MaxExtraRange)), NonNegative(rates.PerRange)));
            return result;
        }

        internal static FleetBudgetRates DefaultRates()
        {
            return new FleetBudgetRates
            {
                Frigate = 10, Cruiser = 20, GrandCruiser = 30,
                RefitPerSlot = 2, PerShot = 2, PerRange = 1
            };
        }

        internal static FleetBudgetRates CurrentRates()
        {
            var settings = Main.Settings;
            if (settings == null) return DefaultRates();
            return new FleetBudgetRates
            {
                Frigate = NonNegative(settings.FleetPfFrigate),
                Cruiser = NonNegative(settings.FleetPfCruiser),
                GrandCruiser = NonNegative(settings.FleetPfGrandCruiser),
                RefitPerSlot = NonNegative(settings.FleetPfRefitPerSlot),
                PerShot = NonNegative(settings.FleetPfPerShot),
                PerRange = NonNegative(settings.FleetPfPerRange)
            };
        }

        /// <summary>
        /// 唯一编辑闸。减配、同价替换与遣散始终允许；正成本操作要求 PF 可读且不超限。
        /// 部署和战中读档不得调用本方法。
        /// </summary>
        internal static bool CanApply(long currentTotal, long candidateTotal, out string reason)
        {
            reason = "";
            if (candidateTotal <= currentTotal) return true;
            int limit = Limit();
            if (limit < 0)
            {
                reason = L.T("利润因子暂时不可读取；现有舰队不受影响，但不能增加占用");
                return false;
            }
            if (candidateTotal > limit)
            {
                reason = L.F("舰队 PF 占用将达到 {0}/{1}", candidateTotal, limit);
                return false;
            }
            return true;
        }

        internal static int HullCost(int shipClass)
        {
            var rates = CurrentRates();
            if (shipClass == 2) return rates.GrandCruiser;
            if (shipClass == 1) return rates.Cruiser;
            return rates.Frigate;
        }

        internal static int RefitPerSlot() { return CurrentRates().RefitPerSlot; }
        internal static int PerShot() { return CurrentRates().PerShot; }
        internal static int PerRange() { return CurrentRates().PerRange; }

        internal static int ClampBonus(int value, int hardMax)
        {
            if (value <= 0) return 0;
            return value > hardMax ? hardMax : value;
        }

        internal static long Add(long a, long b)
        {
            if (a >= long.MaxValue - b) return long.MaxValue;
            return a + b;
        }

        internal static long Mul(long a, long b)
        {
            if (a <= 0 || b <= 0) return 0;
            if (a > long.MaxValue / b) return long.MaxValue;
            return a * b;
        }

        private static int NonNegative(int value)
        {
            return value < 0 ? 0 : value;
        }
    }
}
