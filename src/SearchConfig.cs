using System.Text.Json;

namespace SeedSearch;

public sealed class SearchConfig
{
    public string character { get; set; } = "Silent";
    public bool singlePlayer { get; set; } = true;
    public int ascension { get; set; } = 10;
    public string act1 { get; set; } = "";
    public bool underdocksUnlocked { get; set; } = true;
    public bool underdocksDiscovered { get; set; } = true;
    public int neowChoice { get; set; } = -1;
    public string[] bossAllow { get; set; } = [];
    // 第一幕遭遇队列条件；空数组/false 表示不限制。
    public int normalStrongPrefixLength { get; set; }
    public string[] normalStrongAllow { get; set; } = [];
    public string[] firstEliteAllow { get; set; } = [];
    public bool eliteBlock { get; set; }
    public bool noRestBeforeFirstElite { get; set; }
    public string[] neowPositives { get; set; } = [];
    public string[] neowCurses { get; set; } = [];
    public int unknownMonsterStreak { get; set; }
    public string[] allowedCards { get; set; } = [];
    // 只检查遗物袋实际取出的物品：捏奥选项本身不属于此白名单。
    public string[] allowedRelics { get; set; } = [];
    public int minFightsChecked { get; set; } = 3;
    public string routeMode { get; set; } = "all";
    public bool checkFromFirstUnknown { get; set; } = true;
    public string mapMode { get; set; } = "search";  // search=延迟坐标整理的精确简图；full=完整地图对照
    public int maxSearchFloor { get; set; } = 15;    // 旧配置仍覆盖整幕；GUI 新建条件默认前10层
    // 兼容旧配置；精确搜索不再截断路线，避免漏掉后面的可行路线。
    public int maxRoutes { get; set; }
    public bool findUntilHit { get; set; }          // 不受 count 限制，找到一个后自动停止
    public bool resumeFromCheckpoint { get; set; } = true; // 检测到同一范围的检查点时自动续跑
    public int checkpointIntervalSeconds { get; set; } = 2; // 自动持久化间隔；允许重扫最近少量未提交块
    public int count { get; set; } = 100000;
    public string seedListFile { get; set; } = "";
    public ulong rngSeed { get; set; } = 20261008;
    public ulong startIndex { get; set; }
    public int threads { get; set; }
    public string outFile { get; set; } = "hits.jsonl";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true, WriteIndented = true
    };
    public static SearchConfig Load(string path)
    {
        SearchConfig cfg;
        try { cfg = Read(path); }
        catch (Exception error) when ((error is IOException or JsonException or InvalidDataException) && File.Exists(path + ".bak"))
        { cfg = Read(path + ".bak"); }
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(cfg.outFile)) cfg.outFile = Path.GetFullPath(cfg.outFile, directory);
        if (!string.IsNullOrWhiteSpace(cfg.seedListFile)) cfg.seedListFile = Path.GetFullPath(cfg.seedListFile, directory);
        return cfg.ValidatedCopy();
    }
    static SearchConfig Read(string path) => JsonSerializer.Deserialize<SearchConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("配置文件不能为空。");
    public void Save(string path) => DurableFile.WriteAtomic(path, JsonSerializer.Serialize(ValidatedCopy(), JsonOptions));

    public SearchConfig ValidatedCopy()
    {
        var cfg = (SearchConfig)MemberwiseClone();
        cfg.character = new[] { "Ironclad", "Silent", "Defect", "Regent", "Necrobinder" }
            .FirstOrDefault(c => c.Equals(character, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未知职业：{character}");
        if (!singlePlayer) throw new ArgumentException("当前重放仅支持单人局。");
        if (ascension is < 0 or > 10) throw new ArgumentException("进阶必须在 0–10 之间。");
        if (!findUntilHit && count <= 0) throw new ArgumentException("搜索数量必须大于 0。");
        if (threads is < 0 or > 1024) throw new ArgumentException("线程数必须在 0–1024 之间（0=自动）。");
        if (checkpointIntervalSeconds is < 1 or > 60) throw new ArgumentException("自动保存间隔必须在 1–60 秒之间。");
        if (minFightsChecked is < 0 or > 15 || unknownMonsterStreak is < 0 or > 15)
            throw new ArgumentException("战斗数和问号连出怪数必须在 0–15 之间。");
        cfg.mapMode = (mapMode ?? "").Trim().ToLowerInvariant();
        if (cfg.mapMode is not ("search" or "full")) throw new ArgumentException("地图模式必须为 search 或 full。");
        if (maxSearchFloor is < 1 or > 15) throw new ArgumentException("搜索截止层必须在 1–15 之间（开局战斗为第1层）。");
        if (minFightsChecked > maxSearchFloor || unknownMonsterStreak > maxSearchFloor)
            throw new ArgumentException("检查战斗数和问号连出怪数不能超过搜索截止层；截止层与战斗数是两个独立条件。");
        if (neowChoice is < -1 or > 2) throw new ArgumentException("捏奥选项必须为 -1、0、1 或 2。");
        cfg.routeMode = (routeMode ?? "").Trim().ToLowerInvariant();
        if (cfg.routeMode is not ("all" or "exists")) throw new ArgumentException("路线模式必须为 all 或 exists。");
        cfg.act1 = string.IsNullOrWhiteSpace(act1) ? "" : GameData.Acts.FirstOrDefault(a => a.Index == 0
            && a.Name.Equals(act1.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
            ?? throw new ArgumentException($"未知第一幕：{act1}");
        if (cfg.act1 == "Underdocks" && !underdocksUnlocked || cfg.act1 == "Overgrowth" && underdocksUnlocked && !underdocksDiscovered)
            throw new ArgumentException("第一幕过滤与暗港解锁/发现状态冲突。");
        cfg.bossAllow = Normalize(bossAllow, GameData.Acts.Where(a => a.Index == 0 && (cfg.act1 == "" || a.Name == cfg.act1))
            .SelectMany(a => a.Bosses).Select(b => Game.Snake(b.Name)), "Boss");
        var acts = GameData.Acts.Where(a => a.Index == 0 && (cfg.act1 == "" || a.Name == cfg.act1)).ToArray();
        cfg.normalStrongAllow = Normalize(normalStrongAllow, acts.SelectMany(a => a.Regular).Select(e => e.Name), "强怪");
        cfg.firstEliteAllow = Normalize(firstEliteAllow, acts.SelectMany(a => a.Elites).Select(e => e.Name), "第一个精英");
        if (normalStrongPrefixLength is < 0 or > 12)
            throw new ArgumentException("强怪池前缀长度必须在 0–12 之间；0 表示按所选组合长度检查。");
        if (cfg.normalStrongAllow.Length > 0)
        {
            if (cfg.normalStrongPrefixLength == 0) cfg.normalStrongPrefixLength = cfg.normalStrongAllow.Length;
            if (cfg.normalStrongPrefixLength < cfg.normalStrongAllow.Length)
                throw new ArgumentException("强怪池前缀长度不能小于所选怪物组合数量。");
        }
        else if (cfg.normalStrongPrefixLength != 0)
            throw new ArgumentException("指定强怪池前缀长度时，必须至少选择一个强怪；清空长度即可取消条件。");
        cfg.neowPositives = Normalize(neowPositives, GameData.NeowPositive.Where(r => r != "MassiveScroll")
            .Concat(new[] { "LavaRock", "SmallCapsule", "NutritiousOyster", "StoneHumidifier", "NeowsTalisman", "Pomander" }), "捏奥正面选项");
        if (cfg.neowPositives.Length == 1) throw new ArgumentException("两个捏奥正面选项都须在白名单内，请至少选择两项，或清空以取消限制。");
        if (cfg.neowPositives.Length == 2 && new[] { new[] { "LavaRock", "SmallCapsule" }, new[] { "NutritiousOyster", "StoneHumidifier" }, new[] { "NeowsTalisman", "Pomander" } }
            .Any(pair => pair.All(cfg.neowPositives.Contains))) throw new ArgumentException("所选两个正面遗物互斥，无法同时出现，请增加其他正面选项。");
        cfg.neowCurses = Normalize(neowCurses, GameData.NeowCurse, "捏奥诅咒");
        cfg.allowedCards = Normalize(allowedCards, Game.FilterableCards(cfg.character), "单人可筛选卡牌");
        cfg.allowedRelics = Normalize(allowedRelics, RelicBag.SequenceRelicIds(cfg.character), "遗物序列");
        if (neowChoice == 2 && cfg.allowedCards.Length > 0 && cfg.neowCurses.Length > 0 && cfg.neowCurses.All(NeowEffects.HasUnmodeledCascade))
            throw new ArgumentException("所选诅咒均为尚未完整重放的连锁遗物，无法确认卡牌白名单。请选择其他诅咒，或更改实际选择的捏奥项。");
        cfg.seedListFile = (seedListFile ?? "").Trim();
        cfg.outFile = (outFile ?? "").Trim();
        if (cfg.outFile.Length == 0) throw new ArgumentException("请选择结果输出文件。");
        if (cfg.seedListFile.Length > 0 && Path.GetFullPath(cfg.seedListFile).Equals(Path.GetFullPath(cfg.outFile), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输入名单与输出文件不能相同，避免覆盖输入。");
        if (cfg.seedListFile.Length == 0 && (startIndex >= SeedUtil.SeedSpace
            || !findUntilHit && (UInt128)startIndex + (uint)count > SeedUtil.SeedSpace))
            throw new ArgumentException("搜索范围超出种子空间。");
        return cfg;
    }

    static string[] Normalize(string[] values, IEnumerable<string> valid, string label)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in valid) { lookup[id] = id; lookup[Game.Snake(id)] = id; }
        return (values ?? []).Select(value => lookup.TryGetValue((value ?? "").Trim(), out var id) ? id
            : throw new ArgumentException($"未知{label}：{value}")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}

public sealed class SearchCounters
{
    public long Total, PassNeow, PassAct, PassBoss, PassEncounter, PassRewards, PassAll;
    internal void Merge(SearchCounters other)
    {
        Interlocked.Add(ref Total, other.Total); Interlocked.Add(ref PassNeow, other.PassNeow);
        Interlocked.Add(ref PassAct, other.PassAct); Interlocked.Add(ref PassBoss, other.PassBoss); Interlocked.Add(ref PassEncounter, other.PassEncounter);
        Interlocked.Add(ref PassRewards, other.PassRewards); Interlocked.Add(ref PassAll, other.PassAll);
    }
    public SearchCounters Snapshot() => new()
    {
        Total = Volatile.Read(ref Total), PassNeow = Volatile.Read(ref PassNeow), PassAct = Volatile.Read(ref PassAct),
        PassBoss = Volatile.Read(ref PassBoss), PassEncounter = Volatile.Read(ref PassEncounter), PassRewards = Volatile.Read(ref PassRewards), PassAll = Volatile.Read(ref PassAll)
    };
    public override string ToString() => $"评估 {Total:N0} → 捏奥 {PassNeow:N0} → 第一幕 {PassAct:N0} → Boss {PassBoss:N0} → 遭遇 {PassEncounter:N0} → 奖励预筛 {PassRewards:N0} → 条件通过 {PassAll:N0}";
}
