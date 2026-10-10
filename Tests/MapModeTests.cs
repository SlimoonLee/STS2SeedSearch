using System.Text.Json;
using SeedSearch;

static class MapModeTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Invalid(SearchConfig cfg)
    {
        try { cfg.ValidatedCopy(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid map settings were accepted");
    }

    public static void Run()
    {
        var legacy = JsonSerializer.Deserialize<SearchConfig>("{\"minFightsChecked\":1}").ValidatedCopy();
        Assert(legacy.maxSearchFloor == 15 && legacy.mapMode == "search", "Old configurations changed search scope");
        Assert(new SearchConfig { mapMode = " FULL " }.ValidatedCopy().mapMode == "full", "Map mode was not normalized");
        Invalid(new SearchConfig { mapMode = "partial" });
        Invalid(new SearchConfig { maxSearchFloor = 0 });
        Invalid(new SearchConfig { maxSearchFloor = 16 });
        Invalid(new SearchConfig { maxSearchFloor = 2, minFightsChecked = 3 });
        Invalid(new SearchConfig { maxSearchFloor = 2, minFightsChecked = 0, unknownMonsterStreak = 3 });

        const string fixedSeed = "X6MB8T8NLGQ5";
        var act = Game.Act(ActSelection.Pick(fixedSeed, true, true));
        var (rooms, _) = QueueGen.GenerateAct1(fixedSeed, act, "Silent");
        string boss = Game.Snake(rooms.Boss);
        string otherBoss = act.Bosses.Select(b => Game.Snake(b.Name)).First(b => b != boss);
        foreach (string mode in new[] { "search", "full" })
        {
            var timing = (ActMap.TGen, ActMap.TAssign, ActMap.TPrune, ActMap.TPost);
            var hit = new SeedEvaluator(new SearchConfig { mapMode = mode, maxSearchFloor = 10,
                minFightsChecked = 0, bossAllow = new[] { boss } }).Evaluate(fixedSeed);
            Assert(hit?.boss == boss && hit.route.Length == 0, "Boss-only filtering failed");
            var rejected = new SeedEvaluator(new SearchConfig { mapMode = mode, maxSearchFloor = 10,
                minFightsChecked = 2, bossAllow = new[] { otherBoss } }).Evaluate(fixedSeed);
            Assert(rejected == null && timing == (ActMap.TGen, ActMap.TAssign, ActMap.TPrune, ActMap.TPost),
                "Boss filtering generated a map");
        }

        var missed = new SearchConfig { routeMode = "exists", minFightsChecked = 1,
            checkFromFirstUnknown = true, maxSearchFloor = 1 };
        long post = ActMap.TPost, gen = ActMap.TGen;
        Assert(new SeedEvaluator(missed).Evaluate(fixedSeed) == null && ActMap.TGen > gen && ActMap.TPost == post,
            "Search mode laid out a rejected map or skipped required generation");
        missed.mapMode = "full";
        Assert(new SeedEvaluator(missed).Evaluate(fixedSeed) == null && ActMap.TPost > post, "Full mode did not lay out the map");
        post = ActMap.TPost;
        var immediate = new SeedEvaluator(new SearchConfig { minFightsChecked = 1, maxSearchFloor = 1 }).Evaluate(fixedSeed);
        Assert(immediate?.route.Length == 1 && immediate.checkedFights == 1 && ActMap.TPost > post,
            "Inclusive floor limit or hit layout failed");

        string[] seeds = Enumerable.Range(0, 24).Select(i => SeedUtil.At((ulong)i, 20261008)).ToArray();
        int comparisons = 0, hits = 0, misses = 0;
        bool coordinatesChanged = false;
        foreach (string seed in seeds)
        {
            foreach (string actName in new[] { "Overgrowth", "Underdocks" })
            {
                post = ActMap.TPost;
                var simple = new ActMap(Game.Act(actName), Rng.HashString(seed), false, false, false, 10, applyLayout: false);
                Assert(ActMap.TPost == post, "Deferred map applied layout eagerly");
                var points = simple.GetAllMapPoints().Append(simple.StartingMapPoint).Append(simple.BossMapPoint)
                    .Select(p => (point: p, type: p.PointType, row: p.coord.row, col: p.coord.col,
                        children: p.Children.ToHashSet(), parents: p.parents.ToHashSet())).ToArray();
                simple.CompleteLayout();
                foreach (var point in points)
                {
                    Assert(point.type == point.point.PointType && point.row == point.point.coord.row
                        && point.children.SetEquals(point.point.Children) && point.parents.SetEquals(point.point.parents),
                        "Layout changed graph topology or room types");
                    coordinatesChanged |= point.col != point.point.coord.col;
                }
                var full = new ActMap(Game.Act(actName), Rng.HashString(seed), false, false, false, 10);
                Assert(MapJson(simple) == MapJson(full), "Delayed coordinates differ from full map");
                post = ActMap.TPost;
                simple.CompleteLayout();
                Assert(ActMap.TPost == post && MapJson(simple) == MapJson(full), "Layout completion was not idempotent");
            }

            var cases = new[] {
                new SearchConfig { routeMode = "all", minFightsChecked = 2, maxSearchFloor = 10, neowChoice = 0 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 2, checkFromFirstUnknown = false, maxSearchFloor = 5 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 3, unknownMonsterStreak = 1, maxSearchFloor = 10, neowChoice = 2 },
                new SearchConfig { routeMode = "all", minFightsChecked = 0, unknownMonsterStreak = 1, maxSearchFloor = 5 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 1, checkFromFirstUnknown = true, maxSearchFloor = 1 },
                new SearchConfig { routeMode = "all", minFightsChecked = 1, maxSearchFloor = 1 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 0, unknownMonsterStreak = 2, maxSearchFloor = 10 },
                new SearchConfig { routeMode = "exists", minFightsChecked = 1, checkFromFirstUnknown = true, maxSearchFloor = 10 } };
            foreach (var cfg in cases)
            {
                // Alternate forced acts to cover both maps independently of act-selection RNG.
                cfg.underdocksUnlocked = comparisons % 2 == 0;
                cfg.underdocksDiscovered = false;
                var simpleHit = new SeedEvaluator(cfg).Evaluate(seed);
                Assert((simpleHit != null) == ReferenceRoutes.Evaluate(seed, cfg), $"Floor-limited search differs from exhaustive routes: {seed} {comparisons}");
                cfg.mapMode = "full";
                var fullHit = new SeedEvaluator(cfg).Evaluate(seed);
                Assert(JsonSerializer.Serialize(simpleHit) == JsonSerializer.Serialize(fullHit), $"Map modes differ in hit details/coordinates: {seed} {comparisons}");
                if (simpleHit == null) misses++;
                else
                {
                    hits++;
                    Assert(simpleHit.route.All(room => int.Parse(room.Split(',')[0]) <= cfg.maxSearchFloor), "Returned route exceeded floor limit");
                }
                comparisons++;
            }
        }
        Assert(coordinatesChanged && hits > 0 && misses > 0, "Map-mode comparison did not cover changed coordinates, hits and misses");

        // Locate the first satisfiable floor through the independent full-route oracle,
        // then verify that the cutoff includes that floor and excludes the previous one.
        bool boundaryChecked = false;
        foreach (string seed in seeds)
        {
            var cfg = new SearchConfig { routeMode = "exists", minFightsChecked = 0, unknownMonsterStreak = 1 };
            if (!ReferenceRoutes.Evaluate(seed, cfg)) continue;
            for (int floor = 2; floor <= 15; floor++)
            {
                cfg.maxSearchFloor = floor;
                if (!ReferenceRoutes.Evaluate(seed, cfg)) continue;
                Assert(new SeedEvaluator(cfg).Evaluate(seed)?.route.Length == floor, "First satisfying floor was excluded");
                cfg.maxSearchFloor = floor - 1;
                Assert(new SeedEvaluator(cfg).Evaluate(seed) == null && !ReferenceRoutes.Evaluate(seed, cfg), "Incomplete goals were accepted at cutoff");
                boundaryChecked = true;
                break;
            }
            if (boundaryChecked) break;
        }
        Assert(boundaryChecked, "No positive unknown-room boundary case was tested");
        Console.WriteLine($"PASS: Boss-only/rejected Boss skip maps; deferred layout/idempotence; {comparisons} floor-limited exhaustive/full/search comparisons ({hits} hits, {misses} misses); inclusive cutoff; old-config scope.");
    }

    static string MapJson(ActMap map) => JsonSerializer.Serialize(map.GetAllMapPoints()
        .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).Select(p => new {
            p.coord.row, p.coord.col, type = p.PointType.ToString(),
            children = p.Children.Select(c => $"{c.coord.row},{c.coord.col}").Order(StringComparer.Ordinal) }));
}
