namespace SeedSearch;

/// <summary>GrabBag 逐位转写（权重、谓词不匹配时零抽取、GrabAndRemove）。</summary>
public sealed class GrabBag<T>
{
    List<(T item, double w)> _entries = new();
    double _total;

    public bool Any() => _entries.Count > 0;
    public void Add(T item, double w) { _entries.Add((item, w)); _total += w; }

    int GrabIndex(Rng rng)
    {
        double num = rng.NextDouble() * _total;
        double acc = 0.0;
        for (int i = 0; i < _entries.Count; i++)
        {
            acc += _entries[i].w;
            if (num < acc) return i;
        }
        return -1;
    }

    int GrabIndex(Rng rng, Func<T, bool> pred)
    {
        if (!_entries.Any(e => pred(e.item))) return -1;
        int num;
        do { num = GrabIndex(rng); } while (num >= 0 && !pred(_entries[num].item));
        return num;
    }

    public T GrabAndRemove(Rng rng, Func<T, bool> pred = null)
    {
        int idx = pred != null ? GrabIndex(rng, pred) : GrabIndex(rng);
        if (idx < 0) return default;
        var item = _entries[idx].item;
        _total -= _entries[idx].w;
        _entries.RemoveAt(idx);
        return item;
    }
}

public sealed class ActRooms
{
    public List<string> Events = new();
    public List<string> Normal = new();
    public List<string> Elite = new();
    public string Boss;
    public string Ancient;
}

/// <summary>玩家遗物袋：桶按首次出现序插入，PullFromFront 无 RNG 顺序取件。</summary>
public sealed class RelicBag
{
    Dictionary<string, List<string>> _buckets = new();
    readonly List<string> _order = new();
    static readonly string[] PullOrder = ["Shop", "Common", "Uncommon", "Rare"];

    /// <summary>遗物序列实际可取的候选；捏奥专属/事件/远古遗物不在这里。</summary>
    public static string[] SequenceRelicIds(string character)
    {
        var pools = GameData.RelicPools["SharedRelicPool"]
            .Concat(GameData.RelicPools.TryGetValue(character + "RelicPool", out var cp) ? cp : Array.Empty<string>());
        return pools.Where(r => GameData.RelicRarity.TryGetValue(r, out var rr)
                && rr is "Common" or "Uncommon" or "Rare" or "Shop")
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public RelicBag DeepClone()
    {
        var copy = (RelicBag)MemberwiseClone();
        copy._buckets = _buckets.ToDictionary(k => k.Key, k => new List<string>(k.Value));
        return copy;
    }

    public RelicBag(string character)
    {
        foreach (var r in SequenceRelicIds(character))
        {
            string rr = GameData.RelicRarity[r];
            if (!_buckets.TryGetValue(rr, out var list)) { list = new(); _buckets[rr] = list; _order.Add(rr); }
            list.Add(r);
        }
    }

    public void ShuffleAll(Rng rng)
    {
        foreach (var rr in _order) _buckets[rr].UnstableShuffle(rng);
    }

    public string Pull(string rarity)
    {
        if (_buckets.TryGetValue(rarity, out var b) && b.Count > 0)
        { var r = b[0]; b.RemoveAt(0); return r; }
        int start = Array.IndexOf(PullOrder, rarity);
        if (start < 0) start = -1;
        for (int i = start + 1; i < PullOrder.Length; i++)
            if (_buckets.TryGetValue(PullOrder[i], out var fallback) && fallback.Count > 0)
            { var r = fallback[0]; fallback.RemoveAt(0); return r; }
        return null;
    }
}

public static class QueueGen
{
    static readonly ulong StreamHash = Rng.HashString("up_front");
    static readonly int[] SharedBucketSizes = MakeBuckets(GameData.RelicPools["SharedRelicPool"], null)
        .Values.Select(list => list.Count).ToArray();
    static readonly Dictionary<string, int[]> PlayerBucketSizesCache = new(StringComparer.Ordinal);
    static readonly Dictionary<string, EncounterPools> EncounterPoolCache = GameData.Acts
        .ToDictionary(a => a.Name, a => new EncounterPools(a), StringComparer.Ordinal);

    static readonly HashSet<string> ShuffledRelicRarities = new(StringComparer.Ordinal)
        { "Common", "Uncommon", "Rare", "Shop" };

    public sealed class ActPreparation
    {
        public Rng QueueRng { get; }
        public RelicBag Bag { get; }
        internal ActPreparation(Rng queueRng, RelicBag bag) { QueueRng = queueRng; Bag = bag; }
    }

    /// <summary>
    /// 队列的精确摘要。它与完整 GenerateRooms 消耗完全相同的 RNG，
    /// 但只保留 Boss、先古和筛选需要的队列前缀，不创建事件/遭遇字符串列表。
    /// </summary>
    public sealed class QueueSummary
    {
        public string Boss { get; init; }
        public string Ancient { get; init; }
        public string[] NormalStrongPrefix { get; init; } = [];
        public string FirstElite { get; init; }
    }

    sealed class EncounterPools
    {
        public readonly EncDef[] Weak, Regular, Elites, Bosses;
        public EncounterPools(ActDef act)
        {
            Weak = act.Encounters.Where(e => e.RoomType == "Monster" && e.Weak).ToArray();
            Regular = act.Encounters.Where(e => e.RoomType == "Monster" && !e.Weak).ToArray();
            Elites = act.Encounters.Where(e => e.RoomType == "Elite").ToArray();
            Bosses = act.Encounters.Where(e => e.RoomType == "Boss").ToArray();
        }
    }
    /// <summary>
    /// 重放真实开局序列（SetUpNewSingleplayer/SetUpTest 同路径）：
    /// InitializeNewRun（共享遗物袋→玩家遗物袋）→ GenerateRooms
    /// （共享先古洗牌→幕2/3子集→幕1事件洗牌→弱怪/常规/精英 GrabBag→Boss→先古）。
    /// 假设：全解锁档案、单人、非教学局（NumberOfRuns&gt;0）。
    /// </summary>
    public static (ActRooms rooms, RelicBag bag) GenerateAct1(string seed, ActDef act, string character)
        => GenerateAct1(Rng.HashString(seed), act, character);

    public static (ActRooms rooms, RelicBag bag) GenerateAct1(ulong seedHash, ActDef act, string character)
    {
        var prep = PrepareAct1(seedHash, character);
        return (GenerateRooms(act, prep.QueueRng), prep.Bag);
    }

    /// <summary>
    /// 只重放开局队列之前的随机消耗。需要捏奥遗物效果、但尚未确定遭遇队列时可复用。
    /// </summary>
    public static ActPreparation PrepareAct1(ulong seedHash, string character, bool includeBag = true)
    {
        var up = new Rng(seedHash + StreamHash);

        // ① 共享遗物袋：全稀有度桶，按首次出现序洗牌
        foreach (int size in SharedBucketSizes)
            for (int i = size - 1; i > 0; i--) up.NextULong();
        // ② 玩家遗物袋：共享+角色池，过滤 Common/Uncommon/Rare/Shop
        RelicBag bag = null;
        if (includeBag)
        {
            bag = new RelicBag(character);
            bag.ShuffleAll(up);
        }
        else
        {
            // 不创建遗物对象，但保留每个桶的 Fisher-Yates 抽数，
            // 使 QueueRng 与完整准备路径逐位相同。
            foreach (int size in PlayerBucketSizes(character))
                for (int i = size - 1; i > 0; i--) up.NextULong();
        }
        // ③ 共享先古 [Darv] 洗牌(1元素0抽) + 幕2/3 子集各 1 抽
        var shared = new List<string> { "Darv" };
        shared.UnstableShuffle(up);
        int c2 = up.NextInt(shared.Count + 1);
        var sub2 = shared.Take(c2).ToList();
        shared = shared.Except(sub2).ToList();
        _ = up.NextInt(shared.Count + 1);

        return new ActPreparation(up, bag);
    }

    /// <summary>
    /// 只计算奖励/遭遇筛选真正需要的队列字段。调用方应传入 PrepareAct1 返回的
    /// QueueRng.Clone()，这样之后仍可用原 QueueRng 完整生成同一条队列。
    /// </summary>
    public static QueueSummary GenerateSummary(ActDef act, Rng queueRng,
        int normalStrongPrefixLength = 0, bool firstElite = false)
    {
        var pools = EncounterPoolCache[act.Name];
        var rng = queueRng;
        int eventCount = act.Events.Length + GameData.SharedEvents.Length;
        for (int i = eventCount - 1; i > 0; i--) rng.NextInt(i + 1);

        string[] strongPrefix = normalStrongPrefixLength > 0
            ? new string[Math.Min(normalStrongPrefixLength, act.Rooms - act.WeakCount)] : [];
        Span<int> bag = stackalloc int[64];
        int bagCount = 0;
        EncDef last = null;
        for (int i = 0; i < act.WeakCount; i++)
        {
            int picked = PullIndex(pools.Weak, bag, ref bagCount, rng, last);
            last = pools.Weak[picked];
        }
        // 完整生成器为弱怪和强怪分别创建 GrabBag；不能复用弱怪桶的剩余项。
        bagCount = 0;
        for (int i = act.WeakCount; i < act.Rooms; i++)
        {
            if (bagCount == 0) Refill(pools.Regular, bag, ref bagCount);
            int picked = PullIndex(pools.Regular, bag, ref bagCount, rng, last);
            last = pools.Regular[picked];
            int prefixIndex = i - act.WeakCount;
            if ((uint)prefixIndex < (uint)strongPrefix.Length) strongPrefix[prefixIndex] = last.Name;
        }

        string eliteName = null;
        bagCount = 0;
        last = null;
        for (int i = 0; i < 15; i++)
        {
            int picked = PullIndex(pools.Elites, bag, ref bagCount, rng, last);
            last = pools.Elites[picked];
            if (i == 0 && firstElite) eliteName = last.Name;
        }

        string boss = pools.Bosses[rng.NextItem(pools.Bosses.Length)].Name;
        string ancient = act.Ancients[rng.NextItem(act.Ancients.Length)];
        return new QueueSummary { Boss = boss, Ancient = ancient,
            NormalStrongPrefix = strongPrefix, FirstElite = eliteName };
    }

    public static ActRooms GenerateRooms(ActDef act, Rng queueRng) => GenerateOne(act, queueRng);

    static Dictionary<string, List<string>> MakeBuckets(IEnumerable<string> relics, HashSet<string> filter)
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var r in relics)
        {
            if (!GameData.RelicRarity.TryGetValue(r, out var rr)) continue;
            if (filter != null && !filter.Contains(rr)) continue;
            if (!d.TryGetValue(rr, out var list)) { list = new(); d[rr] = list; }
            list.Add(r);
        }
        return d;
    }

    static int[] PlayerBucketSizes(string character)
    {
        lock (PlayerBucketSizesCache)
        {
            if (PlayerBucketSizesCache.TryGetValue(character, out var sizes)) return sizes;
            var pools = GameData.RelicPools["SharedRelicPool"]
                .Concat(GameData.RelicPools.TryGetValue(character + "RelicPool", out var cp) ? cp : Array.Empty<string>());
            sizes = MakeBuckets(pools, ShuffledRelicRarities).Values.Select(list => list.Count).ToArray();
            PlayerBucketSizesCache[character] = sizes;
            return sizes;
        }
    }

    static void Refill(EncDef[] source, Span<int> bag, ref int count)
    {
        for (int i = 0; i < source.Length; i++) bag[i] = i;
        count = source.Length;
    }

    static int PullIndex(EncDef[] source, Span<int> bag, ref int count, Rng rng, EncDef last)
    {
        if (count == 0) Refill(source, bag, ref count);
        bool hasAllowed = false;
        for (int i = 0; i < count; i++)
            if (Allowed(source[bag[i]], last)) { hasAllowed = true; break; }

        int picked;
        if (!hasAllowed)
        {
            picked = rng.NextInt(count);
        }
        else
        {
            do { picked = rng.NextInt(count); }
            while (!Allowed(source[bag[picked]], last));
        }
        int result = bag[picked];
        for (int i = picked + 1; i < count; i++) bag[i - 1] = bag[i];
        count--;
        return result;
    }

    static bool Allowed(EncDef candidate, EncDef last) => last == null || !SharesTags(candidate, last) && candidate != last;

    /// <summary>ActModel.GenerateRooms 逐位转写。</summary>
    static ActRooms GenerateOne(ActDef act, Rng rng)
    {
        var rooms = new ActRooms();
        var events = act.Events.Concat(GameData.SharedEvents).ToList();
        rooms.Events.AddRange(events.UnstableShuffle(rng));

        var normalDefs = new List<EncDef>();
        var eliteDefs = new List<EncDef>();
        void Pull(GrabBag<EncDef> b, List<EncDef> sinkDefs, List<string> sink)
        {
            EncDef last = sinkDefs.Count > 0 ? sinkDefs[^1] : null;
            var e = b.GrabAndRemove(rng, c => !SharesTags(c, last) && c != last);
            if (e == null) e = b.GrabAndRemove(rng);
            if (e != null) { sinkDefs.Add(e); sink.Add(e.Name); }
        }

        var weakBag = new GrabBag<EncDef>();
        var weakArr = act.Weak.ToArray();
        for (int i = 0; i < act.WeakCount; i++)
        {
            if (!weakBag.Any()) foreach (var w in weakArr) weakBag.Add(w, 1.0);
            Pull(weakBag, normalDefs, rooms.Normal);
        }
        var regBag = new GrabBag<EncDef>();
        var regArr = act.Regular.ToArray();
        for (int i = act.WeakCount; i < act.Rooms; i++)
        {
            if (!regBag.Any()) foreach (var r in regArr) regBag.Add(r, 1.0);
            Pull(regBag, normalDefs, rooms.Normal);
        }
        var eliteBag = new GrabBag<EncDef>();
        var eliteArr = act.Elites.ToArray();
        for (int i = 0; i < 15; i++)
        {
            if (!eliteBag.Any()) foreach (var e in eliteArr) eliteBag.Add(e, 1.0);
            Pull(eliteBag, eliteDefs, rooms.Elite);
        }

        rooms.Boss = act.Bosses.ElementAt(rng.NextItem(act.Bosses.Count())).Name;
        var ancients = act.Ancients.ToList();
        rooms.Ancient = ancients[rng.NextItem(ancients.Count)];
        return rooms;
    }

    static bool SharesTags(EncDef a, EncDef b)
    {
        if (b == null) return false;
        foreach (var left in a.Tags)
            foreach (var right in b.Tags)
                if (left == right) return true;
        return false;
    }
}

public static class SeedUtil
{
    public const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const ulong SeedSpace = 2386420683693101056UL; // 34^12
    public static string Canonicalize(string seed) => (seed ?? "").Trim().ToUpperInvariant().Replace('O', '0').Replace('I', '1');

    // 仿射置换覆盖整个 34^12 空间；乘数与 34 互素，各线程按索引独立产生种子。
    public static string At(ulong index, ulong rngSeed)
        => FromIndex(PermutedIndex(index, rngSeed));

    public static ulong PermutedIndex(ulong index, ulong rngSeed)
    {
        ulong offset = rngSeed;
        offset = (offset ^ (offset >> 30)) * 13787848793156543929UL;
        offset = (offset ^ (offset >> 27)) * 10723151780598845931UL;
        offset ^= offset >> 31;
        return (ulong)(((UInt128)index * 6364136223846793005UL + offset) % SeedSpace);
    }

    /// <summary>直接对置换后的 12 位种子做 XXHash，避免为未命中的候选分配字符串。</summary>
    public static ulong HashAt(ulong index, ulong rngSeed) => HashAt(index, rngSeed, out _);

    public static ulong HashAt(ulong index, ulong rngSeed, out ulong permutedIndex)
    {
        permutedIndex = PermutedIndex(index, rngSeed);
        Span<byte> bytes = stackalloc byte[12];
        ulong value = permutedIndex;
        for (int i = 11; i >= 0; i--)
        {
            bytes[i] = (byte)Alphabet[(int)(value % 34)];
            value /= 34;
        }
        return Rng.HashBytes(bytes);
    }

    public static string FromIndex(ulong idx)
    {
        Span<char> chars = stackalloc char[12];
        for (int i = 11; i >= 0; i--) { chars[i] = Alphabet[(int)(idx % 34)]; idx /= 34; }
        return new string(chars);
    }
}
