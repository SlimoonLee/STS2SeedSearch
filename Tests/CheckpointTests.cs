using System.Text.Json;
using SeedSearch;

static class CheckpointTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    static SearchResult StopAfterACompletedBlock(SearchConfig cfg, string output)
    {
        string checkpoint = output + ".checkpoint.json";
        // 批量摘要优化后，百万级范围可能在取消计时器之前完成；逐步放大，
        // 保证测试验证的是“完整块检查点”，而不是某个机器的瞬时吞吐。
        for (int count = cfg.count; count <= 1000000000; count = count > 500000000 ? 1000000001 : count * 2)
        {
            cfg.count = count;
            if (File.Exists(output)) File.Delete(output);
            if (File.Exists(checkpoint)) File.Delete(checkpoint);
            // Allow cold JIT/startup to finish one block before cancellation;
            // the assertion is about durable block recovery, not a race in the test timer.
            using var cts = new CancellationTokenSource(1000);
            var result = SearchEngine.Run(cfg, ct: cts.Token);
            if (File.Exists(checkpoint))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(checkpoint));
                int lines = File.Exists(output) ? File.ReadLines(output).Count() : 0;
                if (doc.RootElement.GetProperty("CompletedBlocks").GetArrayLength() > 0
                    && lines > 0 && lines < cfg.count) return result;
            }
        }
        throw new Exception("Checkpoint test could not interrupt after a completed block");
    }

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "seedsearch-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "hits.jsonl");
            var cfg = new SearchConfig { count = 100000, minFightsChecked = 0, threads = 1, outFile = output, resumeFromCheckpoint = true };
            var partial = StopAfterACompletedBlock(cfg, output);
            string checkpoint = output + ".checkpoint.json";
            Assert(partial.Canceled && File.Exists(checkpoint), "Canceled range did not leave a checkpoint");
            Assert(File.ReadLines(output).Count() > 0 && File.ReadLines(output).Count() < cfg.count, "Partial output was not block committed");
            using (var checkpointDoc = JsonDocument.Parse(File.ReadAllText(checkpoint)))
                Assert(checkpointDoc.RootElement.GetProperty("CompletedBlocks").GetArrayLength() > 0, "Checkpoint did not record a completed block");

            // Simulate a power-loss window: output contains an uncommitted tail,
            // while the primary checkpoint is damaged. The atomic backup must
            // be selected and the tail must be rolled back before replay.
            File.Copy(checkpoint, checkpoint + ".bak", true);
            File.AppendAllText(output, "partial-uncommitted-line\n");
            File.WriteAllText(checkpoint, "{corrupt");
            var resumed = SearchEngine.Run(cfg);
            string[] lines = File.ReadAllLines(output);
            Assert(resumed.Resumed && resumed.RecoveredCheckpoint && resumed.RecoveredOutput && !resumed.Canceled
                && !File.Exists(checkpoint) && lines.Length == cfg.count,
                "Resume did not recover the checkpoint/output tail or finish the remaining range");
            Assert(lines.Select(SearchEngine.ParseSeed).Distinct().Count() == cfg.count, "Resume duplicated or lost a seed result");

            // A stale checkpoint must never be applied to changed filters.
            var secondOutput = Path.Combine(directory, "mismatch.jsonl");
            var second = new SearchConfig { count = 100000, minFightsChecked = 0, threads = 1, outFile = secondOutput, resumeFromCheckpoint = true };
            _ = StopAfterACompletedBlock(second, secondOutput);
            second.ascension = 9;
            try { _ = SearchEngine.Run(second); }
            catch (InvalidOperationException)
            {
                string errors = secondOutput + ".errors.jsonl";
                Assert(File.Exists(errors) && File.ReadAllText(errors).Contains("Failure", StringComparison.Ordinal), "Search failure did not leave diagnostics");
                Console.WriteLine("PASS: checkpoint resume, block commit, backup/tail recovery, deduplication, and stale-config diagnostics."); return;
            }
            throw new Exception("Changed search filters incorrectly reused a checkpoint");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
