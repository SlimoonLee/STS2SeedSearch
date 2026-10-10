using System.Numerics;
using System.Text.Json;
using SeedSearch;

static class RegressionTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    public static void Run(string root)
    {
        Assert(SeedUtil.Alphabet.Length == 34 && (BigInteger)SeedUtil.SeedSpace == BigInteger.Pow(34, 12), "Seed space/alphabet mismatch");
        Assert(BigInteger.GreatestCommonDivisor(6364136223846793005UL, SeedUtil.SeedSpace) == 1, "Seed permutation must be bijective");
        Assert(SeedUtil.Canonicalize("  ioZ  ") == "10Z", "Game canonicalization mismatch");
        var seeds = Enumerable.Range(0, 10000).Select(i => SeedUtil.At((ulong)i, 20261008)).ToArray();
        Assert(seeds.Distinct().Count() == seeds.Length && seeds.All(s => s.Length == 12 && s.All(SeedUtil.Alphabet.Contains)) && seeds.Any(s => s.Contains('Z')), "Generated seeds have duplicates/invalid characters");
        Assert(SeedUtil.At(0, 1) != SeedUtil.At(0, (1UL << 32) + 1), "Random key lost high bits");
        foreach (ulong index in Enumerable.Range(0, 256).Select(i => (ulong)i))
        {
            ulong hash = SeedUtil.HashAt(index, 20261008, out ulong mapped);
            Assert(hash == Rng.HashString(SeedUtil.FromIndex(mapped)), "Direct seed hash differs from string hash");
        }
        Rng.HashString(new string('种', 300));
        Assert(RunPlan.Norm("DEADLY_POISON") == "DeadlyPoison", "snake IDs not normalized");
        Throws<ArgumentException>(() => new SearchConfig { character = "Deprived" }.ValidatedCopy());
        Throws<ArgumentException>(() => new SearchConfig { routeMode = "invalid" }.ValidatedCopy());
        Throws<ArgumentException>(() => new SearchConfig { neowPositives = new[] { "LavaRock", "SmallCapsule" } }.ValidatedCopy());
        Throws<ArgumentException>(() => new SearchConfig { allowedCards = new[] { "typo" } }.ValidatedCopy());

        foreach (string seed in new[] { "X6MB8T8NLGQ5", "BBBBBBBBBBB2", "31YXNJNSU5LX" })
        {
            string path = Path.Combine(root, "docs", "validation", $"seed-{seed.ToLowerInvariant()}-engine-a10.jsonl");
            using var doc = JsonDocument.Parse(File.ReadLines(path).First(line => line.StartsWith("{\"type\":\"map\"")));
            var map = new ActMap(Game.Act("Overgrowth"), Rng.HashString(seed), false, false, false, 10);
            var nodes = doc.RootElement.GetProperty("rows").EnumerateArray().SelectMany(row => row.EnumerateArray()).ToArray();
            Assert(nodes.Length == map.GetAllMapPoints().Count(), $"Engine node count differs for {seed}");
            foreach (var node in nodes)
            {
                var point = map.GetPoint(node.GetProperty("col").GetInt32(), node.GetProperty("row").GetInt32());
                Assert(point != null && point.PointType.ToString() == node.GetProperty("type").GetString(), $"Engine map type differs for {seed}");
                var expected = node.GetProperty("children").EnumerateArray().Select(c => $"{c.GetProperty("row").GetInt32()},{c.GetProperty("col").GetInt32()}").Order();
                Assert(expected.SequenceEqual(point.Children.Select(c => $"{c.coord.row},{c.coord.col}").Order()), $"Engine edges differ for {seed}");
            }
            var (rooms, _) = QueueGen.GenerateAct1(seed, Game.Act("Overgrowth"), "Silent");
            Assert(Game.Snake(rooms.Boss) == doc.RootElement.GetProperty("boss").GetProperty("id").GetString(), $"Engine boss differs for {seed}");
        }
        Console.WriteLine("PASS: seed space, canonicalization, configuration validation, 3 independent engine maps/edges/bosses.");

        foreach (string seed in seeds.Take(16))
        {
            var cfgs = new[] {
                new SearchConfig { routeMode = "all", minFightsChecked = 2 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 2, checkFromFirstUnknown = false, neowChoice = 0 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 3, unknownMonsterStreak = 1, neowChoice = 2 },
                new SearchConfig { routeMode = "all", minFightsChecked = 0, unknownMonsterStreak = 1 } };
            foreach (var cfg in cfgs)
                Assert((new SeedEvaluator(cfg).Evaluate(seed) != null) == ReferenceRoutes.Evaluate(seed, cfg), $"Prefix search differs from exhaustive search: {seed} {cfg.routeMode} {cfg.minFightsChecked}");
        }
        string fixedSeed = "X6MB8T8NLGQ5";
        var one = new SeedEvaluator(new SearchConfig { routeMode = "exists", minFightsChecked = 1, checkFromFirstUnknown = false }).Evaluate(fixedSeed);
        Assert(one?.checkedFights == 1 && one.fights.Length == 1, "First-fight checking failed or checked extra rewards");
        var whitelist = one.fights[0].cards.Select(Game.Snake).ToArray();
        Assert(new SeedEvaluator(new SearchConfig { minFightsChecked = 1, allowedCards = whitelist }).Evaluate(fixedSeed) != null, "Whitelist snake IDs not matched");
        Assert(new SeedEvaluator(new SearchConfig { minFightsChecked = 1, allowedCards = whitelist.Take(2).ToArray() }).Evaluate(fixedSeed) == null, "Whitelist failed to reject");
        var allCards = GameData.CardPools["SilentCardPool"].Where(c => !GameData.CardMultiplayerOnly.Contains(c)).ToHashSet(StringComparer.Ordinal);
        var fastRewards = new RewardsReplay(fixedSeed, "Silent");
        var fullRewards = new RewardsReplay(fixedSeed, "Silent");
        Assert(fastRewards.FightCardsAllowed(false, 0, allCards), "Allowed-card fast path rejected an unrestricted reward");
        fullRewards.Fight(false, 0);
        var fastNext = fastRewards.Fight(false, 0);
        var fullNext = fullRewards.Fight(false, 0);
        Assert(JsonSerializer.Serialize(fastNext) == JsonSerializer.Serialize(fullNext), "Allowed-card fast path changed RNG sequence");
        var rejectRewards = new RewardsReplay(fixedSeed, "Silent");
        var firstReward = new RewardsReplay(fixedSeed, "Silent").Fight(false, 0);
        Assert(!rejectRewards.FightCardsAllowed(false, 0, new[] { firstReward.Cards[0].Name }.ToHashSet(StringComparer.Ordinal)), "Allowed-card fast path failed to reject a later card");
        var aliasEval = new SeedEvaluator(new SearchConfig { minFightsChecked = 0 });
        Assert(aliasEval.Evaluate(" ioz ").seed == aliasEval.Evaluate("10Z").seed, "Search bypassed game canonicalization");
        var (_, bag) = QueueGen.GenerateAct1(fixedSeed, Game.Act("Overgrowth"), "Silent");
        var clone = bag.DeepClone(); clone.Pull("Common");
        Assert(bag.Pull("Common") != clone.Pull("Common"), "Relic bag clone shares mutable state");
        var rewardFirstCards = GameData.CardPools["SilentCardPool"].Where(c => !GameData.CardMultiplayerOnly.Contains(c)).ToArray();
        foreach (string candidate in seeds.Take(32))
        {
            var unrestricted = new SeedEvaluator(new SearchConfig { routeMode = "all", minFightsChecked = 2 });
            var rewardFirst = new SeedEvaluator(new SearchConfig { routeMode = "all", minFightsChecked = 2, allowedCards = rewardFirstCards });
            Assert(JsonSerializer.Serialize(unrestricted.Evaluate(candidate)) == JsonSerializer.Serialize(rewardFirst.Evaluate(candidate)), "Reward-first queue deferral changed a hit");
        }
        Console.WriteLine("PASS: 64 comparisons against exhaustive routes, first-fight checking, snake whitelist, branch isolation.");

        string directory = Path.Combine(Path.GetTempPath(), "seedsearch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cfg = new SearchConfig { count = 500, minFightsChecked = 0, threads = 1, outFile = Path.Combine(directory, "single.jsonl") };
            var single = SearchEngine.Run(cfg);
            cfg.threads = 4; cfg.outFile = Path.Combine(directory, "parallel.jsonl");
            var parallel = SearchEngine.Run(cfg);
            Assert(single.Progress.Evaluated == 500 && parallel.Progress.Stages.PassAll == 500, "Search counters mismatch");
            Assert(File.ReadAllLines(Path.Combine(directory, "single.jsonl")).Order().SequenceEqual(File.ReadAllLines(cfg.outFile).Order()), "Threads changed results");
            string input = Path.Combine(directory, "input.jsonl");
            File.WriteAllText(input, "X6MB8T8NLGQ5\n\n{\"seed\":\"bbbbbbbbbbb2\"}\n ioz \n");
            cfg.seedListFile = input; cfg.count = 1; cfg.outFile = Path.Combine(directory, "listed.jsonl");
            var listed = SearchEngine.Run(cfg);
            Assert(listed.Progress.Evaluated == 3 && listed.Progress.Total == 3 && File.ReadLines(cfg.outFile).Select(SearchEngine.ParseSeed).ToHashSet().SetEquals(new[] { "X6MB8T8NLGQ5", "BBBBBBBBBBB2", "10Z" }), "Input list was ignored, truncated to random count, or completion progress was incorrect");
            string preserved = File.ReadAllText(input); cfg.outFile = input;
            Throws<ArgumentException>(() => SearchEngine.Run(cfg)); Assert(File.ReadAllText(input) == preserved, "Input was overwritten");
            cfg.seedListFile = ""; cfg.count = 100000; cfg.outFile = Path.Combine(directory, "canceled.jsonl");
            using var cts = new CancellationTokenSource();
            int seen = 0;
            var canceled = SearchEngine.Run(cfg, _ => { if (Interlocked.Increment(ref seen) == 8) cts.Cancel(); }, ct: cts.Token);
            Assert(canceled.Canceled && canceled.Progress.Evaluated < cfg.count && canceled.Progress.Hits == File.ReadLines(cfg.outFile).Count(), "Cancellation lost output or did not stop");
            cfg.outFile = "relative.jsonl"; cfg.seedListFile = "input.jsonl";
            string configPath = Path.Combine(directory, "config.json"); cfg.Save(configPath);
            var loaded = SearchConfig.Load(configPath);
            Assert(loaded.outFile == Path.Combine(directory, "relative.jsonl") && loaded.seedListFile == input, "Config-relative paths incorrect");
            Console.WriteLine("PASS: deterministic 1/4-thread results, streaming JSONL/text input, cancellation, output flushing, input protection, config-relative paths.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
