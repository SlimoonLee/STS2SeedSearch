using System.Collections.Concurrent;
using System.Text.Json;
using SeedSearch;

static class FindUntilHitTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Invalid(SearchConfig cfg)
    {
        try { cfg.ValidatedCopy(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid search range was accepted");
    }

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "seedsearch-find-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var act = Game.Act("Overgrowth");
            var (firstRooms, _) = QueueGen.GenerateAct1(SeedUtil.At(0, 20261008), act, "Silent");
            string targetBoss = act.Bosses.Select(b => Game.Snake(b.Name)).First(b => b != Game.Snake(firstRooms.Boss));
            var cfg = new SearchConfig { count = 1, minFightsChecked = 0, underdocksUnlocked = false,
                bossAllow = new[] { targetBoss }, threads = 1, outFile = Path.Combine(directory, "bounded.jsonl") };
            var bounded = SearchEngine.Run(cfg);
            Assert(bounded.Progress.Evaluated == 1 && bounded.Progress.Hits == 0, "Test's first seed unexpectedly matched");

            cfg.findUntilHit = true;
            var evaluator = new SeedEvaluator(cfg);
            int expectedIndex = Enumerable.Range(1, 127).First(i => evaluator.Evaluate(SeedUtil.At((ulong)i, cfg.rngSeed)) != null);
            foreach (int threads in new[] { 1, 4 })
            {
                cfg.threads = threads; cfg.outFile = Path.Combine(directory, $"first-{threads}.jsonl");
                int callbacks = 0;
                var updates = new ConcurrentQueue<SearchProgress>();
                var result = SearchEngine.Run(cfg, _ => Interlocked.Increment(ref callbacks), updates.Enqueue);
                string[] lines = File.ReadAllLines(cfg.outFile);
                Assert(!result.Canceled && result.Progress.Evaluated > cfg.count && result.Progress.Hits == 1
                    && callbacks == 1 && lines.Length == 1 && result.Progress.Stages.Total == result.Progress.Evaluated,
                    "Find-until-hit respected count, duplicated a hit, or failed to finalize counters/output");
                var hit = JsonSerializer.Deserialize<Hit>(lines[0]);
                Assert(hit.boss == targetBoss && evaluator.Evaluate(hit.seed) != null, "Saved seed does not satisfy filters");
                Assert(updates.All(p => p.Total == null && p.Remaining == null), "Unbounded search invented a total or ETA");
                if (threads == 1)
                    Assert(result.Progress.Evaluated == expectedIndex + 1 && hit.seed == SeedUtil.At((ulong)expectedIndex, cfg.rngSeed), "Single-thread search did not stop at its first matching seed");
            }

            // All seeds match: concurrent workers must still save and deliver exactly one hit.
            cfg.bossAllow = []; cfg.count = 0; cfg.threads = 8;
            cfg.startIndex = (ulong)int.MaxValue + 100;
            cfg.outFile = Path.Combine(directory, "racing.jsonl");
            int delivered = 0;
            var racing = SearchEngine.Run(cfg, _ => Interlocked.Increment(ref delivered));
            Assert(racing.Progress.Hits == 1 && delivered == 1 && File.ReadLines(cfg.outFile).Count() == 1 && !racing.Canceled,
                "Concurrent first-hit claims did not select exactly one winner");
            cfg.threads = 1; cfg.outFile = Path.Combine(directory, "large-index.jsonl");
            var largeIndex = SearchEngine.Run(cfg);
            Assert(largeIndex.Progress.Evaluated == 1 && SearchEngine.ParseSeed(File.ReadAllText(cfg.outFile)) == SeedUtil.At(cfg.startIndex, cfg.rngSeed),
                "Unbounded enumeration truncated its 64-bit starting index");
            string configPath = Path.Combine(directory, "saved.json"); cfg.Save(configPath);
            Assert(SearchConfig.Load(configPath).findUntilHit, "First-hit mode was lost on save/load");
            var durableConfig = new SearchConfig { count = 123, outFile = Path.Combine(directory, "durable.jsonl") };
            durableConfig.Save(configPath); durableConfig.count = 456; durableConfig.Save(configPath);
            File.WriteAllText(configPath, "{corrupt");
            Assert(SearchConfig.Load(configPath).count == 123, "Corrupt config did not recover its atomic backup");

            string input = Path.Combine(directory, "input.txt");
            File.WriteAllText(input, "X6MB8T8NLGQ5\nBBBBBBBBBBB2\n{invalid json after the hit}\n");
            cfg.seedListFile = input; cfg.outFile = Path.Combine(directory, "listed-first.jsonl");
            var listed = SearchEngine.Run(cfg);
            Assert(!listed.Canceled && listed.Progress.Evaluated == 1 && listed.Progress.Hits == 1
                && SearchEngine.ParseSeed(File.ReadAllText(cfg.outFile)) == "X6MB8T8NLGQ5", "List mode did not stop after its first hit");

            // A valid, deliberately impossible Boss filter gives a no-hit stream for cancellation/EOF tests.
            cfg.bossAllow = new[] { Game.Act("Underdocks").Bosses.Select(b => Game.Snake(b.Name))
                .First(b => !act.Bosses.Any(a => Game.Snake(a.Name) == b)) };
            File.WriteAllText(input, "X6MB8T8NLGQ5\nBBBBBBBBBBB2\n31YXNJNSU5LX\n");
            cfg.threads = 4; cfg.outFile = Path.Combine(directory, "list-empty.jsonl");
            var exhaustedList = SearchEngine.Run(cfg);
            Assert(!exhaustedList.Canceled && exhaustedList.Progress.Evaluated == 3 && exhaustedList.Progress.Total == 3
                && exhaustedList.Progress.Hits == 0 && new FileInfo(cfg.outFile).Length == 0, "No-hit list did not terminate at EOF");

            cfg.seedListFile = ""; cfg.startIndex = 0; cfg.outFile = Path.Combine(directory, "canceled.jsonl");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var canceled = SearchEngine.Run(cfg, onProgress: p => { if (p.Evaluated >= 64) cts.Cancel(); }, ct: cts.Token);
                Assert(canceled.Canceled && canceled.Progress.Evaluated > 1 && canceled.Progress.Hits == 0
                    && canceled.Progress.Total == null && new FileInfo(cfg.outFile).Length == 0, "No-hit unbounded search could not be manually canceled");
            }

            cfg.startIndex = SeedUtil.SeedSpace - 2; cfg.count = int.MaxValue;
            cfg.outFile = Path.Combine(directory, "space-end.jsonl");
            var spaceEnd = SearchEngine.Run(cfg);
            Assert(!spaceEnd.Canceled && spaceEnd.Progress.Evaluated == 2 && spaceEnd.Progress.Hits == 0, "Seed-space boundary overflowed or used the inactive count");
            Invalid(new SearchConfig { findUntilHit = true, startIndex = SeedUtil.SeedSpace });
            Invalid(new SearchConfig { findUntilHit = false, count = 0 });
            Invalid(new SearchConfig { count = 3, startIndex = SeedUtil.SeedSpace - 2 });
            Console.WriteLine($"PASS: find-until-hit exceeds count (first matching index {expectedIndex}); 1/4/8-thread single output/callback; no total/ETA; cancellation; list hit/EOF; 64-bit index/space boundary; config persistence.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
