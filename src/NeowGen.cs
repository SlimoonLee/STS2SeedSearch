namespace SeedSearch;

/// <summary>捏奥开局三选项重放（Neow.GenerateInitialOptions，全解锁档案 + 单人）。</summary>
public static class NeowGen
{
    static readonly ulong NeowHash = Rng.HashString("NEOW");
    static readonly string[] CursePool = GameData.NeowCurse.Where(r => IsAllowedAtNeow(r, true)).ToArray();
    static readonly Dictionary<string, string[]> Exclusions = new()
    {
        ["CursedPearl"] = new[] { "GoldenPearl" },
        ["HeftyTablet"] = new[] { "ArcaneScroll" },
        ["LeafyPoultice"] = new[] { "NewLeaf" },
        ["PrecariousShears"] = new[] { "PreciseScissors" },
        ["NeowsSacrifice"] = new[] { "PhialHolster", "LostCoffer" },
    };
    static readonly string[][] PositivePools = BuildPositivePools();

    /// <summary>返回展示顺序 [正面A, 正面B, 诅咒]。多人/自定义局未建模。</summary>
    public static (string posA, string posB, string curse) Generate(string seed) => Generate(Rng.HashString(seed));

    public static (string posA, string posB, string curse) Generate(ulong seedHash)
    {
        // EventModel.BeginEvent: Rng(RunSeed + slot(0) + XxHash64("NEOW"))
        var rng = new Rng(seedHash + NeowHash);
        int curseIndex = rng.NextItem(CursePool.Length);
        string curse = CursePool[curseIndex];
        int mask = 0;
        if (curse != "LargeCapsule" && rng.NextBool()) mask |= 1;
        // The first positive is omitted for LargeCapsule; the other two rolls are unconditional.
        if (rng.NextBool()) mask |= 2;
        if (rng.NextBool()) mask |= 4;
        var candidates = PositivePools[curseIndex * 8 + mask];
        Span<int> order = stackalloc int[32];
        for (int i = 0; i < candidates.Length; i++) order[i] = i;
        for (int i = candidates.Length - 1; i > 0; i--)
        {
            int k = rng.NextInt(i + 1);
            (order[i], order[k]) = (order[k], order[i]);
        }
        return (candidates[order[0]], candidates[order[1]], curse);
    }

    static string[][] BuildPositivePools()
    {
        var pools = new string[CursePool.Length * 8][];
        for (int curseIndex = 0; curseIndex < CursePool.Length; curseIndex++)
        {
            string curse = CursePool[curseIndex];
            for (int mask = 0; mask < 8; mask++)
            {
                var candidates = new List<string>();
                foreach (string positive in GameData.NeowPositive)
                {
                    if (Exclusions.TryGetValue(curse, out var banned) && banned.Contains(positive)) continue;
                    if (IsAllowedAtNeow(positive, singlePlayer: true)) candidates.Add(positive);
                }
                if (curse != "LargeCapsule") candidates.Add((mask & 1) != 0 ? "LavaRock" : "SmallCapsule");
                candidates.Add((mask & 2) != 0 ? "NutritiousOyster" : "StoneHumidifier");
                candidates.Add((mask & 4) != 0 ? "NeowsTalisman" : "Pomander");
                pools[curseIndex * 8 + mask] = candidates.ToArray();
            }
        }
        return pools;
    }

    /// <summary>全解锁档案下仅人数条件生效。</summary>
    static bool IsAllowedAtNeow(string relic, bool singlePlayer) => relic switch
    {
        "WingedBoots" => singlePlayer,
        "MassiveScroll" => !singlePlayer,
        "SilverCrucible" => singlePlayer,
        _ => true,
    };
}

/// <summary>? 房间类型赔率机（UnknownMapPointOdds 转写，NextFloat、<= 比较、掷中重置/未掷中累加）。</summary>
public sealed class UnknownOdds
{
    static readonly (string, float)[] Base = { ("Monster", 0.1f), ("Elite", -1f), ("Treasure", 0.02f), ("Shop", 0.03f) };
    float[] _odds;
    Rng _rng;
    static readonly ulong StreamHash = Rng.HashString("unknown_map_point");

    public UnknownOdds(string seed) : this(Rng.HashString(seed)) { }

    public UnknownOdds(ulong seedHash)
    {
        _rng = new Rng(seedHash + StreamHash);
        _odds = Base.Select(e => e.Item2).ToArray();
    }

    public UnknownOdds DeepClone()
    {
        var copy = (UnknownOdds)MemberwiseClone();
        copy._rng = _rng.Clone();
        copy._odds = (float[])_odds.Clone();
        return copy;
    }

    /// <summary>掷出下一个 ? 房间类型；"Event" 为默认兜底。</summary>
    public string Roll()
    {
        float num = _rng.NextFloat();
        float acc = 0f;
        string result = "Event";
        for (int i = 0; i < Base.Length; i++)
        {
            float v = _odds[i];
            if (v >= 0f)
            {
                acc += v;
                if (num <= acc) { result = Base[i].Item1; break; }
            }
        }
        for (int i = 0; i < Base.Length; i++)
            _odds[i] = Base[i].Item1 == result ? Base[i].Item2 : _odds[i] + Base[i].Item2;
        return result;
    }
}
