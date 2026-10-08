using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace DynastyRetinue
{
    /// <summary>Plain UID array in the vanilla save settings. Cache belongs to the save container,
    /// never to GameId: two saves in the same campaign must retain separate deployment choices.</summary>
    internal static class GuardReserveStore
    {
        internal const string Key = "dynastyretinue.guard_reserve.v1";
        private sealed class Cache
        {
            internal object Raw;
            internal bool Exists, Read, Valid;
            internal HashSet<string> Ids;
        }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Cache> Caches
            = new ConditionalWeakTable<Dictionary<string, object>, Cache>();

        private static Cache Read(Dictionary<string, object> values)
        {
            if (values == null) return null;
            object raw;
            bool exists = values.TryGetValue(Key, out raw);
            var cache = Caches.GetValue(values, delegate { return new Cache(); });
            if (cache.Read && cache.Exists == exists && ReferenceEquals(cache.Raw, raw)) return cache;
            cache.Read = true; cache.Exists = exists; cache.Raw = raw;
            cache.Valid = false; cache.Ids = new HashSet<string>(StringComparer.Ordinal);
            if (!exists) { cache.Valid = true; return cache; }
            try
            {
                var encoded = raw as string;
                if (string.IsNullOrEmpty(encoded)) return cache;
                var ids = JsonConvert.DeserializeObject<string[]>(encoded,
                    new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None });
                if (ids == null) return cache;
                foreach (string id in ids)
                {
                    if (string.IsNullOrEmpty(id)) return cache;
                    cache.Ids.Add(id);
                }
                cache.Valid = true;
            }
            catch { }
            return cache;
        }

        internal static bool Valid(Dictionary<string, object> values)
        { var c = Read(values); return c != null && c.Valid; }

        internal static bool Contains(Dictionary<string, object> values, string uid)
        {
            var c = Read(values);
            // Unreadable data must not silently deploy a guard the player left behind.
            return c != null && (!c.Valid || c.Ids.Contains(uid));
        }

        internal static string Snapshot(Dictionary<string, object> values)
        {
            var c = Read(values);
            return c != null && c.Valid ? (c.Raw as string ?? "") : null;
        }

        internal static bool Set(Dictionary<string, object> values, IEnumerable<string> targets,
            bool reserved, IEnumerable<string> rosterIds)
        {
            var c = Read(values);
            if (c == null || !c.Valid) return false;
            var next = new HashSet<string>(c.Ids, StringComparer.Ordinal);
            next.IntersectWith(rosterIds); // Drop dismissed/dead IDs only during an explicit edit.
            foreach (string id in targets)
                if (reserved) next.Add(id); else next.Remove(id);
            var ordered = new List<string>(next);
            ordered.Sort(StringComparer.Ordinal);
            string encoded = JsonConvert.SerializeObject(ordered, Formatting.None);
            values[Key] = encoded; // Serialize completely before replacing the immutable save value.
            c.Raw = encoded; c.Exists = true; c.Ids = next;
            return true;
        }
    }
}
