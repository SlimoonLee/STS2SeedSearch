namespace SeedSearch;

/// <summary>第一幕遭遇队列条件；队列在建图前生成，因此可以先于奖励和路线筛选。</summary>
public static class EncounterFilters
{
    public static bool QueueMatches(ActDef act, QueueGen.QueueSummary summary, SearchConfig cfg)
    {
        if (cfg.normalStrongAllow.Length > 0
            && !cfg.normalStrongAllow.All(summary.NormalStrongPrefix.Contains)) return false;
        return cfg.firstEliteAllow.Length == 0
            || summary.FirstElite != null && cfg.firstEliteAllow.Contains(summary.FirstElite);
    }

    public static bool QueueMatches(ActDef act, ActRooms rooms, SearchConfig cfg)
    {
        if (cfg.normalStrongAllow.Length > 0)
        {
            int count = Math.Min(cfg.normalStrongPrefixLength, rooms.Normal.Count - act.WeakCount);
            var prefix = rooms.Normal.Skip(act.WeakCount).Take(count).ToHashSet(StringComparer.Ordinal);
            if (!cfg.normalStrongAllow.All(prefix.Contains)) return false;
        }
        return cfg.firstEliteAllow.Length == 0 || rooms.Elite.Count > 0 && cfg.firstEliteAllow.Contains(rooms.Elite[0]);
    }

    public static string[] StrongQueue(ActDef act, ActRooms rooms) => rooms.Normal.Skip(act.WeakCount).ToArray();
}
