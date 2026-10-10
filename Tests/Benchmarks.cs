using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.Loader;
using System.Text.Json;
using SeedSearch;

static class Benchmarks
{
    public static void MapModes(string baselinePath = null)
    {
        AssemblyLoadContext legacy = null;
        System.Reflection.Assembly assembly = null;
        if (baselinePath != null)
        {
            legacy = new AssemblyLoadContext("seedsearch-before-map-modes", true);
            legacy.Resolving += (_, name) => name.Name == "System.IO.Hashing" ? typeof(Rng).Assembly.GetReferencedAssemblies()
                .Where(a => a.Name == name.Name).Select(System.Reflection.Assembly.Load).First() : null;
            assembly = legacy.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
        }
        string[] seeds = Enumerable.Range(0, 1024).Select(i => SeedUtil.At((ulong)i, 20261008)).ToArray();
        var workloads = new[] {
            ("all2/floor10", new SearchConfig { routeMode = "all", minFightsChecked = 2, maxSearchFloor = 10 }),
            ("exists3+unknown1/floor10", new SearchConfig { routeMode = "exists", minFightsChecked = 3, unknownMonsterStreak = 1, maxSearchFloor = 10 }) };
        foreach (var (name, cfg) in workloads)
        {
            Dictionary<string, string> before = null;
            // all/X=2 completes at floor 2, so the old version's whole-act scope is equivalent.
            if (assembly != null && cfg.routeMode == "all")
            {
                var cfgType = assembly.GetType("SeedSearch.SearchConfig");
                object oldConfig = JsonSerializer.Deserialize(JsonSerializer.Serialize(cfg), cfgType);
                var seed = Expression.Parameter(typeof(string));
                var body = Expression.Convert(Expression.Call(assembly.GetType("SeedSearch.RunPlan").GetMethod("Evaluate"),
                    seed, Expression.Constant(oldConfig, cfgType)), typeof(object));
                before = Measure(name, "previous-full", Expression.Lambda<Func<string, object>>(body, seed).Compile());
            }
            cfg.mapMode = "full";
            var fullEvaluator = new SeedEvaluator(cfg);
            var full = Measure(name, "full", seed => fullEvaluator.Evaluate(seed));
            cfg.mapMode = "search";
            var simpleEvaluator = new SeedEvaluator(cfg);
            var simple = Measure(name, "search", seed => simpleEvaluator.Evaluate(seed));
            Equal(full, simple);
            if (before != null) Equal(before, full);
            Console.WriteLine($"PASS: {name} hit sets and complete JSON details match across compared modes.");
        }
        legacy?.Unload();

        Dictionary<string, string> Measure(string name, string version, Func<string, object> evaluate)
        {
            for (int i = 0; i < 24; i++) evaluate(seeds[i]);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var hits = new Dictionary<string, object>();
            foreach (string seed in seeds)
            {
                object hit = evaluate(seed);
                if (hit != null) hits.Add(seed, hit);
            }
            sw.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"{name} {version}: seeds={seeds.Length} hits={hits.Count} seconds={sw.Elapsed.TotalSeconds:F3} rate={seeds.Length / sw.Elapsed.TotalSeconds:F0}/s bytes/seed={bytes / (double)seeds.Length:F0}");
            // Serialization is outside the timed/allocation region.
            return hits.ToDictionary(h => h.Key, h => JsonSerializer.Serialize(h.Value, h.Value.GetType()));
        }
        static void Equal(Dictionary<string, string> first, Dictionary<string, string> second)
        {
            if (first.Count != second.Count || first.Any(h => !second.TryGetValue(h.Key, out var other) || other != h.Value))
                throw new Exception("Benchmark hit set or hit details changed");
        }
    }

    public static void Run(string baselinePath = null)
    {
        AssemblyLoadContext legacy = null;
        System.Reflection.Assembly assembly = null;
        if (baselinePath != null)
        {
            legacy = new AssemblyLoadContext("seedsearch-before", true);
            legacy.Resolving += (_, name) => name.Name == "System.IO.Hashing" ? typeof(Rng).Assembly.GetReferencedAssemblies()
                .Where(a => a.Name == name.Name).Select(System.Reflection.Assembly.Load).First() : null;
            assembly = legacy.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
        }
        string[] seeds = Enumerable.Range(0, 4000).Select(i => SeedUtil.At((ulong)i, 20261008)).ToArray();
        var workloads = new[] {
            ("coarse", 4000, new SearchConfig { minFightsChecked = 0, routeMode = "exists" }),
            ("map+all2", 192, new SearchConfig { minFightsChecked = 2, routeMode = "all" }),
            ("whitelist+all2", 1000, new SearchConfig { minFightsChecked = 2, routeMode = "all", allowedCards = new[] {
                "Deflect", "Backflip", "Footwork", "BladeDance", "Accuracy", "EscapePlan", "PiercingWail", "Prepared", "Untouchable", "DodgeAndRoll",
                "Predator", "Outbreak", "Survivor", "Slice", "Expertise", "LegSweep", "DeadlyPoison", "SuckerPunch", "PoisonedStab", "Neutralize" } }) };
        foreach (var (name, count, cfg) in workloads)
        {
            HashSet<string> before = null;
            if (assembly != null)
            {
                var cfgType = assembly.GetType("SeedSearch.SearchConfig");
                object oldConfig = JsonSerializer.Deserialize(JsonSerializer.Serialize(cfg), cfgType);
                var seed = Expression.Parameter(typeof(string));
                var body = Expression.Convert(Expression.Call(assembly.GetType("SeedSearch.RunPlan").GetMethod("Evaluate"),
                    seed, Expression.Constant(oldConfig, cfgType)), typeof(object));
                var oldEvaluate = Expression.Lambda<Func<string, object>>(body, seed).Compile();
                before = Measure(name, "before", count, oldEvaluate);
            }
            var prepared = new SeedEvaluator(cfg);
            var after = Measure(name, "after", count, seed => prepared.Evaluate(seed));
            if (before != null && !before.SetEquals(after)) throw new Exception($"Benchmark hit set changed for {name}");
        }
        legacy?.Unload();

        HashSet<string> Measure(string name, string version, int count, Func<string, object> evaluate)
        {
            for (int i = 0; i < 8; i++) evaluate(seeds[i]);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var hits = new HashSet<string>();
            for (int i = 0; i < count; i++) if (evaluate(seeds[i]) != null) hits.Add(seeds[i]);
            sw.Stop();
            Console.WriteLine($"{name} {version}: seeds={count} hits={hits.Count} seconds={sw.Elapsed.TotalSeconds:F3} rate={count / sw.Elapsed.TotalSeconds:F0}/s bytes/seed={(GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count:F0}");
            return hits;
        }
    }
}
