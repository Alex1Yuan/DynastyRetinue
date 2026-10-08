using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DynastyRetinue
{
    /// <summary>Only strings in vanilla save settings; no custom fact, AssetId or saved CLR type.
    /// An empty journal still records the choice to skip bootstrap grants. No process-global cache:
    /// loading an earlier save in the same campaign must load its earlier growth decisions too.</summary>
    internal static class GuardGrowthStore
    {
        internal const string Prefix = "dynastyretinue.guard_growth.v1.";

        internal static bool HasHistory(Dictionary<string, object> values, string uid)
        { return values != null && !string.IsNullOrEmpty(uid) && values.ContainsKey(Prefix + uid); }

        private static SortedSet<string> Read(Dictionary<string, object> values, string uid)
        {
            if (values == null || string.IsNullOrEmpty(uid)) return null;
            object raw;
            if (!values.TryGetValue(Prefix + uid, out raw)) return new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                var text = raw as string;
                if (string.IsNullOrEmpty(text)) return null;
                var entries = JsonConvert.DeserializeObject<string[]>(text,
                    new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None });
                if (entries == null) return null;
                var result = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    int hash = entry == null ? -1 : entry.IndexOf('#');
                    int rank; Guid path;
                    if (hash != 32 || !Guid.TryParseExact(entry.Substring(0, hash), "N", out path)
                        || !int.TryParse(entry.Substring(hash + 1), out rank) || rank < 1 || rank > 55)
                        return null;
                    result.Add(entry);
                }
                return result;
            }
            catch { return null; }
        }

        internal static bool Touch(Dictionary<string, object> values, string uid)
        {
            var entries = Read(values, uid);
            if (entries == null) return false;
            if (!HasHistory(values, uid)) values[Prefix + uid] = "[]";
            return true;
        }

        internal static bool Mark(Dictionary<string, object> values, string uid, string path, int rank)
        {
            Guid parsed;
            if (!Guid.TryParseExact(path, "N", out parsed) || rank < 1 || rank > 55) return false;
            var entries = Read(values, uid);
            if (entries == null) return false;
            entries.Add(path.ToLowerInvariant() + "#" + rank);
            values[Prefix + uid] = JsonConvert.SerializeObject(entries, Formatting.None);
            return true;
        }

        internal static bool Skipped(Dictionary<string, object> values, string uid, string path, int rank)
        {
            var entries = Read(values, uid);
            // An unreadable journal must never cause catch-up grants.
            return entries == null || entries.Contains(path.ToLowerInvariant() + "#" + rank);
        }
    }
}
