using SeedSearch;

static class EncounterFilterTests
{
    public static void Run()
    {
        SummaryMatchesFullQueue();
        string seed = Enumerable.Range(0, 512).Select(i => SeedUtil.At((ulong)i, 20261008))
            .First(s => ActSelection.Pick(s, false, false) == "Overgrowth");
        var act = Game.Act("Overgrowth");
        var (rooms, _) = QueueGen.GenerateAct1(seed, act, "Silent");
        var strong = EncounterFilters.StrongQueue(act, rooms);
        Assert(strong.Length == act.Rooms - act.WeakCount, "Strong queue did not start after weak encounters");
        Assert(strong.Distinct().Count() == strong.Length, "Strong queue unexpectedly repeated an encounter");
        Assert(rooms.Elite.Count >= 2 && strong.Length >= 5, "Act queue fixture is too short");

        var strongWhitelist = strong.Take(5).ToArray();
        var queueConfig = new SearchConfig { act1 = "Overgrowth", underdocksUnlocked = false,
            normalStrongPrefixLength = 2, normalStrongAllow = strongWhitelist,
            firstEliteAllow = new[] { rooms.Elite[0] }, minFightsChecked = 0 };
        var queueHit = new SeedEvaluator(queueConfig).Evaluate(seed);
        Assert(queueHit != null && queueHit.normalQueue.Skip(act.WeakCount).Take(2).SequenceEqual(strong.Take(2))
            && queueHit.eliteQueue[0] == rooms.Elite[0]
            && queueConfig.ValidatedCopy().normalStrongAllow.Length == 5,
            "Strong-prefix whitelist/first-elite filters or output queue failed");

        var wrongStrong = strong.Skip(2).First();
        Assert(new SeedEvaluator(new SearchConfig { act1 = "Overgrowth", underdocksUnlocked = false,
            normalStrongPrefixLength = 2, normalStrongAllow = new[] { wrongStrong } }).Evaluate(seed) == null,
            "Strong-prefix filter accepted a monster outside the requested prefix");
        var wrongElite = rooms.Elite.Skip(1).First();
        Assert(new SeedEvaluator(new SearchConfig { act1 = "Overgrowth", underdocksUnlocked = false,
            firstEliteAllow = new[] { wrongElite } }).Evaluate(seed) == null,
            "First-elite filter accepted the wrong first elite");

        Invalid(new SearchConfig { normalStrongPrefixLength = 13 });
        Invalid(new SearchConfig { normalStrongAllow = new[] { "DefinitelyNotAnEncounter" } });
        Invalid(new SearchConfig { firstEliteAllow = new[] { "DefinitelyNotAnElite" } });

        string avoidSeed = null, blockSeed = null;
        foreach (string candidate in Enumerable.Range(0, 512).Select(i => SeedUtil.At((ulong)i, 20261008)))
        {
            if (ActSelection.Pick(candidate, false, false) != "Overgrowth") continue;
            var map = new ActMap(act, Rng.HashString(candidate), false, false, false, 10);
            if (map.HasPathToBossAvoiding(MapPointType.Elite)) avoidSeed ??= candidate;
            else blockSeed ??= candidate;
            if (avoidSeed != null && blockSeed != null) break;
        }
        Assert(avoidSeed != null && blockSeed != null, "Could not find both elite-block map topologies");
        var blockConfig = new SearchConfig { act1 = "Overgrowth", underdocksUnlocked = false, eliteBlock = true, minFightsChecked = 0 };
        Assert(new SeedEvaluator(blockConfig).Evaluate(avoidSeed) == null, "Elite-block filter accepted a bypass route");
        var blockingHit = new SeedEvaluator(blockConfig).Evaluate(blockSeed);
        Assert(blockingHit != null && blockingHit.eliteQueue != null, "Elite-block filter rejected a map where every route meets an elite");

        int mixed = 0, matchingExists = 0, matchingAll = 0;
        foreach (string candidate in Enumerable.Range(0, 64).Select(i => SeedUtil.At((ulong)i, 20261008)))
        {
            var map = new ActMap(Game.Act("Overgrowth"), Rng.HashString(candidate), false, false, false, 10);
            var paths = Paths(map.StartingMapPoint).ToArray();
            bool hasGood = paths.Any(NoRestBeforeElite), hasBad = paths.Any(p => !NoRestBeforeElite(p));
            if (hasGood && hasBad) mixed++;
            foreach (string routeMode in new[] { "exists", "all" })
            {
                var cfg = new SearchConfig { underdocksUnlocked = false, routeMode = routeMode, minFightsChecked = 0,
                    maxSearchFloor = 1, noRestBeforeFirstElite = true };
                bool expected = ReferenceRoutes.Evaluate(candidate, cfg);
                bool actual = new SeedEvaluator(cfg).Evaluate(candidate) != null;
                Assert(expected == actual, $"No-fire-before-first-elite differs from full-route oracle ({routeMode}): {candidate}");
                if (actual)
                {
                    if (routeMode == "exists") matchingExists++; else matchingAll++;
                }
            }
        }
        Assert(mixed > 0 && matchingExists > 0, "No-fire condition did not cover valid/invalid exists routes");
        Console.WriteLine($"PASS: strong-pool prefix whitelist, first-elite group, elite-block topology, no-fire-before-first-elite route checks and validation ({mixed} mixed maps, {matchingExists} exists/{matchingAll} all matches).");
    }

    static void SummaryMatchesFullQueue()
    {
        foreach (var act in GameData.Acts.Where(a => a.Index == 0))
        {
            foreach (string seed in Enumerable.Range(0, 128).Select(i => SeedUtil.At((ulong)i, 20261008)))
            {
                ulong hash = Rng.HashString(seed);
                var skipped = QueueGen.PrepareAct1(hash, "Silent", includeBag: false);
                var fullPrep = QueueGen.PrepareAct1(hash, "Silent", includeBag: true);
                Assert(skipped.QueueRng.S0 == fullPrep.QueueRng.S0 && skipped.QueueRng.S1 == fullPrep.QueueRng.S1
                    && skipped.QueueRng.S2 == fullPrep.QueueRng.S2 && skipped.QueueRng.S3 == fullPrep.QueueRng.S3
                    && skipped.QueueRng.Counter == fullPrep.QueueRng.Counter,
                    $"Bag-skipping preparation changed queue RNG: {act.Name}/{seed}");
                var summary = QueueGen.GenerateSummary(act, skipped.QueueRng.Clone(), 5, true);
                var rooms = QueueGen.GenerateRooms(act, fullPrep.QueueRng);
                Assert(summary.Boss == rooms.Boss && summary.Ancient == rooms.Ancient,
                    $"Queue summary boss/ancient mismatch: {act.Name}/{seed}");
                Assert(summary.NormalStrongPrefix.SequenceEqual(rooms.Normal.Skip(act.WeakCount).Take(5)),
                    $"Queue summary strong prefix mismatch: {act.Name}/{seed}");
                Assert(summary.FirstElite == rooms.Elite[0], $"Queue summary first elite mismatch: {act.Name}/{seed}");
            }
        }
    }

    static bool NoRestBeforeElite(List<MapPoint> path)
    {
        bool elite = false;
        foreach (var point in path.Skip(1))
        {
            if (point.PointType == MapPointType.RestSite && !elite) return false;
            if (point.PointType == MapPointType.Elite) elite = true;
        }
        return true;
    }
    static IEnumerable<List<MapPoint>> Paths(MapPoint node)
    {
        if (node.PointType == MapPointType.Boss) { yield return new() { node }; yield break; }
        foreach (var child in node.Children)
            foreach (var tail in Paths(child)) { tail.Insert(0, node); yield return tail; }
    }
    static void Invalid(SearchConfig config)
    {
        try { config.ValidatedCopy(); }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid encounter filter was accepted");
    }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
