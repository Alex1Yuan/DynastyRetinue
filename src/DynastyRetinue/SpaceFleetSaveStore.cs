using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Kingmaker;
using Newtonsoft.Json;

namespace DynastyRetinue
{
    /// <summary>
    /// 名册以纯 JSON 字符串写入原版 InGameSettings，随 .zks 内 settings.json 保存。
    /// 只缓存当前原版容器；同战役旧档也有独立容器，不能按 GameId 复用名册。
    /// 存档不包含本 mod 的 CLR 类型，读取与编辑均不依赖外部 Settings.xml。
    /// </summary>
    internal static class SpaceFleetSaveStore
    {
        internal const string StorageKey = "dynastyretinue.space_fleet.v1";

        private sealed class Cache
        {
            internal bool Read;
            internal bool HasValue;
            internal string GameId;
            internal object Raw;
            internal SpaceFleetRosterState Roster;
        }

        private static readonly ConditionalWeakTable<Dictionary<string, object>, Cache> Caches
            = new ConditionalWeakTable<Dictionary<string, object>, Cache>();
        private static readonly JsonSerializerSettings Codec = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            Formatting = Formatting.None
        };

        internal static SpaceFleetRosterState Get(InGameSettings settings, string gameId, bool create)
        {
            if (settings == null || settings.List == null || string.IsNullOrEmpty(gameId)) return null;
            var values = settings.List;
            var cache = Caches.GetValue(values, delegate { return new Cache(); });
            object raw;
            bool exists = values.TryGetValue(StorageKey, out raw);
            if (!cache.Read || cache.HasValue != exists
                || !string.Equals(cache.GameId, gameId, StringComparison.Ordinal)
                || !ReferenceEquals(cache.Raw, raw))
            {
                cache.Read = true;
                cache.HasValue = exists;
                cache.GameId = gameId;
                cache.Raw = raw;
                cache.Roster = null;
                if (exists)
                {
                    try
                    {
                        var encoded = raw as string;
                        if (string.IsNullOrEmpty(encoded))
                            throw new InvalidOperationException("名册快照不是有效字符串");
                        var roster = JsonConvert.DeserializeObject<SpaceFleetRosterState>(encoded, Codec);
                        if (roster == null || !string.Equals(roster.GameId, gameId, StringComparison.Ordinal))
                            throw new InvalidOperationException("名册快照不属于当前战役");
                        cache.Roster = roster;
                    }
                    catch (Exception e)
                    {
                        // 同一容器/同一原始值只报一次；无法读取时不覆盖已有快照。
                        Main.LogError("[舰队存档] 无法读取当前存档名册: " + e.Message);
                    }
                }
            }
            if (cache.Roster != null || !create || exists) return cache.Roster;

            cache.Roster = new SpaceFleetRosterState
            {
                DataVersion = SpaceFleetCatalog.RosterDataVersion,
                GameId = gameId,
                Initialized = true,
                NextId = 1
            };
            if (TryWrite(settings, cache.Roster)) return cache.Roster;
            cache.Roster = null;
            return null;
        }

        internal static bool TryWrite(InGameSettings settings, SpaceFleetRosterState roster)
        {
            if (settings == null || settings.List == null || roster == null
                || roster.DataVersion > SpaceFleetCatalog.RosterDataVersion) return false;
            Cache cache;
            if (!Caches.TryGetValue(settings.List, out cache)
                || !ReferenceEquals(cache.Roster, roster)
                || !string.Equals(cache.GameId, roster.GameId, StringComparison.Ordinal)) return false;
            object current;
            bool exists = settings.List.TryGetValue(StorageKey, out current);
            if (cache.HasValue != exists || !ReferenceEquals(cache.Raw, current)) return false;
            try
            {
                // 先完整编码，再替换一个不可变字符串。原版保存器不会遍历正在编辑的名册。
                string encoded = JsonConvert.SerializeObject(roster, Codec);
                settings.List[StorageKey] = encoded;
                cache.Raw = encoded;
                cache.HasValue = true;
                return true;
            }
            catch (Exception e)
            {
                Main.LogError("[舰队存档] 更新当前存档名册失败: " + e.Message);
                return false;
            }
        }
    }
}
