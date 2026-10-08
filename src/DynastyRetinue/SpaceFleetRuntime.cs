using System;
using System.Collections.Generic;

namespace DynastyRetinue
{
    internal sealed class SpaceFleetRuntimeProfile
    {
        internal string EntryId;
        internal string HullGuid;
        internal int BroadsideExtraShots;
        internal int NonBroadsideExtraShots;
        internal int BroadsideExtraRange;
        internal int NonBroadsideExtraRange;
        internal int ShieldPct;
        internal int ArmourPct;
        internal int RamPct;
        internal readonly Dictionary<string, string> AbilityRoles
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal int Shots(bool broadside)
        {
            return broadside ? BroadsideExtraShots : NonBroadsideExtraShots;
        }

        internal int Range(bool broadside)
        {
            return broadside ? BroadsideExtraRange : NonBroadsideExtraRange;
        }

        internal bool TryGetRole(string abilityGuid, out string role)
        {
            return AbilityRoles.TryGetValue(abilityGuid ?? "", out role);
        }
    }
}
