using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SeedSearch;

public sealed record SearchProgress(long Evaluated, long Hits, long? Total, TimeSpan Elapsed, SearchCounters Stages)
{
    public double SeedsPerSec => Evaluated / Math.Max(0.001, Elapsed.TotalSeconds);
    public TimeSpan? Remaining => Total.HasValue && Evaluated > 0
        ? TimeSpan.FromSeconds(Math.Max(0, Total.Value - Evaluated) / SeedsPerSec) : null;
}
public sealed record SearchResult(SearchProgress Progress, bool Canceled)
{
    public bool Resumed { get; init; }
    public bool RecoveredCheckpoint { get; init; }
    public bool RecoveredOutput { get; init; }
    public string CheckpointPath { get; init; }
    public string DiagnosticsPath { get; init; }
}

/// <summary>CLI/GUI 共同的搜索执行器，范围枚举可复现，名单流式读取，计数与回调限频。</summary>
public static class SearchEngine
{
    const ulong CheckpointBlockSize = 4096;

    /// <summary>
    /// 判断当前配置是否有可以安全接续的随机范围检查点。
    /// 这个只读探测与真正运行使用完全相同的指纹、范围和结果尾部校验，
    /// 因而 GUI 可以在启动时显示“从上次继续”，而不会把旧条件误当成当前条件。
    /// </summary>
    public static bool HasCompatibleCheckpoint(SearchConfig config)
    {
        try
        {
            var cfg = config.ValidatedCopy();
            if (cfg.seedListFile.Length > 0) return false;
            string output = Path.GetFullPath(cfg.outFile);
            string checkpointPath = SearchCheckpointStore.PathFor(output);
            if (!File.Exists(output) || (!File.Exists(checkpointPath) && !File.Exists(checkpointPath + ".bak"))) return false;
            var checkpoint = SearchCheckpointStore.Load(checkpointPath, out _);
            ulong end = cfg.findUntilHit ? SeedUtil.SeedSpace : cfg.startIndex + (ulong)cfg.count;
            string fingerprint = SearchCheckpointStore.Fingerprint(cfg, output);
            string legacyFingerprint = SearchCheckpointStore.Fingerprint(cfg, output, legacy: true);
            bool fingerprintMatches = checkpoint.Version == 1
                ? checkpoint.Fingerprint == legacyFingerprint
                : checkpoint.Version == 2 && checkpoint.RulesVersion == SearchCheckpointStore.RulesVersion
                    && checkpoint.Fingerprint == fingerprint;
            if (!fingerprintMatches || checkpoint.BaseIndex != cfg.startIndex || checkpoint.EndExclusive != end
                || checkpoint.BlockSize != CheckpointBlockSize) return false;
            if (checkpoint.Version == 2)
            {
                long length = new FileInfo(output).Length;
                if (length < checkpoint.OutputBytes) return false;
                if (checkpoint.OutputBytes > 0 && !string.Equals(
                    DurableFile.TailSha256(output, checkpoint.OutputBytes), checkpoint.OutputSha256,
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch
        {
            // A damaged/stale checkpoint is deliberately not offered as a
            // continuation.  The normal start path still reports its detailed
            // recovery error if the user explicitly selects that output.
            return false;
        }
    }

    public static SearchResult Run(SearchConfig config, Action<Hit> onHit = null,
        Action<SearchProgress> onProgress = null, CancellationToken ct = default)
    {
        var cfg = config.ValidatedCopy();
        ct.ThrowIfCancellationRequested();
        string output = Path.GetFullPath(cfg.outFile);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        FileStream outputLock;
        try { outputLock = DurableFile.AcquireLock(output); }
        catch (Exception error)
        {
            string logPath = SearchDiagnostics.RecordFailure(output, cfg, error,
                new SearchProgress(0, 0, null, TimeSpan.Zero, new()), output + ".checkpoint.json", "OutputLockFailure");
            throw new InvalidOperationException($"{error.Message}（排错记录：{logPath}）", error);
        }
        using (outputLock)
        {
            return RunLocked(cfg, onHit, onProgress, ct, output);
        }
    }

    static SearchResult RunLocked(SearchConfig cfg, Action<Hit> onHit, Action<SearchProgress> onProgress,
        CancellationToken ct, string output)
    {
        var evaluator = new SeedEvaluator(cfg);
        bool randomMode = cfg.seedListFile.Length == 0;
        ulong end = randomMode ? cfg.findUntilHit ? SeedUtil.SeedSpace : cfg.startIndex + (ulong)cfg.count : 0;
        string checkpointPath = randomMode ? SearchCheckpointStore.PathFor(output) : null;
        string fingerprint = randomMode ? SearchCheckpointStore.Fingerprint(cfg, output) : null;
        string legacyFingerprint = randomMode ? SearchCheckpointStore.Fingerprint(cfg, output, legacy: true) : null;
        SearchCheckpoint savedCheckpoint = null;
        bool resumed = false;
        bool recoveredCheckpoint = false;
        bool recoveredOutput = false;
        CheckpointTracker tracker = null;
        try
        {
            if (cfg.seedListFile.Length > 0 && !File.Exists(cfg.seedListFile))
                throw new FileNotFoundException("找不到输入种子名单。", cfg.seedListFile);
            if (randomMode)
            {
                if (cfg.resumeFromCheckpoint && (File.Exists(checkpointPath) || File.Exists(checkpointPath + ".bak")))
                {
                    savedCheckpoint = SearchCheckpointStore.Load(checkpointPath, out recoveredCheckpoint);
                    bool fingerprintMatches = savedCheckpoint.Version == 1
                        ? savedCheckpoint.Fingerprint == legacyFingerprint
                        : savedCheckpoint.Version == 2 && savedCheckpoint.RulesVersion == SearchCheckpointStore.RulesVersion
                            && savedCheckpoint.Fingerprint == fingerprint;
                    if (!fingerprintMatches
                        || savedCheckpoint.BaseIndex != cfg.startIndex || savedCheckpoint.EndExclusive != end
                        || savedCheckpoint.BlockSize != CheckpointBlockSize)
                        throw new InvalidOperationException("检查点与当前搜索条件、随机键或范围不一致；取消“自动续跑”或删除旧检查点后再开始。");
                    if (!File.Exists(output)) throw new FileNotFoundException("检查点对应的结果文件不存在；为避免丢失旧结果，未继续搜索。", output);
                    if (savedCheckpoint.Version == 2)
                    {
                        recoveredOutput = RecoverOutput(output, savedCheckpoint);
                        if (recoveredOutput)
                            SearchDiagnostics.RecordEvent(output, cfg, "OutputTruncated", "结果文件包含检查点之后的未提交尾部，已回退到最后安全字节。",
                                new SearchProgress(0, 0, null, TimeSpan.Zero, new()), checkpointPath);
                    }
                    resumed = true;
                }
                else if (!resumed && cfg.resumeFromCheckpoint && File.Exists(output) && new FileInfo(output).Length > 0)
                    throw new InvalidOperationException($"结果文件已存在但没有可用检查点：{output}。为避免覆盖旧结果，请删除/改名结果文件，或关闭“自动续跑”后明确从头开始。");
                tracker = new CheckpointTracker(cfg.startIndex, end, CheckpointBlockSize, savedCheckpoint);
            }
            else if (File.Exists(output) && new FileInfo(output).Length > 0)
                throw new InvalidOperationException($"名单模式的结果文件已有内容：{output}。为避免覆盖旧结果，请改用新文件或先移走旧文件。");
        }
        catch (Exception error)
        {
            string logPath = SearchDiagnostics.RecordFailure(output, cfg, error,
                new SearchProgress(0, 0, null, TimeSpan.Zero, new()), checkpointPath);
            throw new InvalidOperationException($"{error.Message}（排错记录：{logPath}）", error);
        }
        using var writer = new StreamWriter(output, resumed, new UTF8Encoding(false), 65536);
        object fileGate = new();
        object checkpointGate = new();
        var totals = new SearchCounters();
        long evaluated = 0, hits = 0, lastReport = 0, lastCheckpoint = -(long)cfg.checkpointIntervalSeconds * 1000L;
        int claimed = 0;
        var clock = Stopwatch.StartNew();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = cfg.threads == 0 ? Environment.ProcessorCount : cfg.threads,
            CancellationToken = ct
        };
        long? total = randomMode && !cfg.findUntilHit
            ? (long)((end - cfg.startIndex) - tracker.CompletedCandidates) : null;
        SearchProgress Snapshot() => new(Volatile.Read(ref evaluated), Volatile.Read(ref hits), total, clock.Elapsed, totals.Snapshot());
        void Report(bool force)
        {
            long now = clock.ElapsedMilliseconds, previous = Volatile.Read(ref lastReport);
            if (!force && (now - previous < 500 || Interlocked.CompareExchange(ref lastReport, now, previous) != previous)) return;
            lock (fileGate) writer.Flush();
            onProgress?.Invoke(Snapshot());
        }
        void Flush(Worker worker)
        {
            totals.Merge(worker.Stages);
            Interlocked.Add(ref evaluated, worker.Completed);
            worker.Stages = new();
            worker.Completed = 0;
        }
        void CommitHits(IReadOnlyList<Hit> blockHits)
        {
            if (blockHits.Count == 0) return;
            lock (fileGate)
            {
                foreach (var hit in blockHits) writer.WriteLine(JsonSerializer.Serialize(hit));
                // A hit is visible to the caller only after its JSON line is durable.
                DurableFile.Flush(writer);
            }
            foreach (var hit in blockHits) { Interlocked.Increment(ref hits); onHit?.Invoke(hit); }
        }
        void SaveCheckpoint(bool force, string status = "Running")
        {
            if (tracker == null) return;
            long now = clock.ElapsedMilliseconds, previous = Volatile.Read(ref lastCheckpoint);
            if (!force && now - previous < cfg.checkpointIntervalSeconds * 1000L) return;
            lock (fileGate)
            {
                DurableFile.Flush(writer);
                long outputBytes = new FileInfo(output).Length;
                var snapshot = tracker.Snapshot(fingerprint, status, Volatile.Read(ref evaluated), Volatile.Read(ref hits),
                    totals, clock.Elapsed.TotalSeconds, DurableFile.TailSha256(output), outputBytes);
                snapshot.Config = cfg;
                SearchCheckpointStore.SaveAtomic(checkpointPath, snapshot);
            }
            Volatile.Write(ref lastCheckpoint, now);
        }
        Worker Finish(Hit hit, ParallelLoopState loop, Worker worker, Action<Hit> sink)
        {
            worker.Completed++;
            if (hit != null && (!cfg.findUntilHit || Interlocked.CompareExchange(ref claimed, 1, 0) == 0))
            {
                // 只允许一个工作线程提交命中；找到为止模式下获胜块会在块结束时提交。
                if (cfg.findUntilHit) loop.Stop();
                sink(hit);
            }
            if (worker.Completed >= 64 || clock.ElapsedMilliseconds - Volatile.Read(ref lastReport) >= 500)
            {
                Flush(worker);
                Report(false);
            }
            return worker;
        }
        Worker Process(string seed, ParallelLoopState loop, Worker worker)
        {
            if (loop.IsStopped) return worker;
            ct.ThrowIfCancellationRequested();
            Hit hit;
            try { hit = evaluator.Evaluate(seed, worker.Stages, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new InvalidOperationException($"种子 {seed} 评估失败：{error.Message}", error); }
            return Finish(hit, loop, worker, value => CommitHits([value]));
        }
        Worker ProcessHash(ulong index, ParallelLoopState loop, Worker worker, Action<Hit> sink)
        {
            if (loop.IsStopped) return worker;
            ct.ThrowIfCancellationRequested();
            ulong hash = SeedUtil.HashAt(index, cfg.rngSeed, out ulong permutedIndex);
            Hit hit;
            try { hit = evaluator.EvaluateHash(hash, worker.Stages, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                string seed = SeedUtil.FromIndex(permutedIndex);
                throw new InvalidOperationException($"种子 {seed} 评估失败：{error.Message}", error);
            }
            // 未命中的候选不需要物化种子；命中写出前再恢复原来的可见种子。
            if (hit != null) hit = hit with { seed = SeedUtil.FromIndex(permutedIndex) };
            return Finish(hit, loop, worker, sink);
        }
        IEnumerable<SearchBlock> Blocks()
        {
            foreach (var pending in tracker.PendingRanges())
            {
                for (ulong ordinal = pending.Start; ordinal < pending.EndExclusive && Volatile.Read(ref claimed) == 0; ordinal++)
                {
                    ulong start = cfg.startIndex + ordinal * CheckpointBlockSize;
                    yield return new SearchBlock(ordinal, start, Math.Min(end, start + CheckpointBlockSize));
                }
            }
        }
        Worker ProcessBlock(SearchBlock block, ParallelLoopState loop, Worker worker)
        {
            var blockHits = new List<Hit>();
            bool completed = true;
            for (ulong index = block.Start; index < block.EndExclusive; index++)
            {
                if (loop.IsStopped || ct.IsCancellationRequested || cfg.findUntilHit && Volatile.Read(ref claimed) != 0)
                { completed = false; break; }
                worker = ProcessHash(index, loop, worker, blockHits.Add);
            }
            if (cfg.findUntilHit && blockHits.Count > 0)
            {
                CommitHits(blockHits);
            }
            else if (completed && !ct.IsCancellationRequested && !loop.IsStopped)
            {
                // 先提交整块结果，再推进检查点；半块结果会随本次运行丢弃并在恢复时重放。
                lock (checkpointGate)
                {
                    CommitHits(blockHits);
                    tracker.MarkComplete(block.Ordinal);
                    SaveCheckpoint(false);
                }
            }
            return worker;
        }
        bool canceled = false, failed = false;
        Exception failure = null;
        string diagnosticsPath = SearchDiagnostics.PathFor(output);
        Report(true);
        try
        {
            if (cfg.seedListFile.Length == 0)
                Parallel.ForEach(Blocks(), options, () => new Worker(), ProcessBlock, Flush);
            else
                Parallel.ForEach(File.ReadLines(cfg.seedListFile).Where(line => !string.IsNullOrWhiteSpace(line)), options,
                    () => new Worker(), (line, loop, worker) => loop.IsStopped ? worker : Process(ParseSeed(line), loop, worker), Flush);
            if (!cfg.findUntilHit || Volatile.Read(ref hits) == 0) total ??= Volatile.Read(ref evaluated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { canceled = true; }
        catch (Exception error)
        {
            failed = true; failure = error;
            string logPath = SearchDiagnostics.RecordFailure(output, cfg, error, Snapshot(), checkpointPath);
            throw new InvalidOperationException($"{error.Message}（排错记录：{logPath}）", error);
        }
        finally
        {
            Report(true);
            if (tracker != null)
            {
                bool finished = !canceled && !failed && (cfg.findUntilHit && Volatile.Read(ref hits) > 0
                    || tracker.CompletedCandidates >= end - cfg.startIndex);
                try
                {
                    lock (fileGate) DurableFile.Flush(writer);
                    if (finished)
                    {
                        if (File.Exists(checkpointPath)) File.Delete(checkpointPath);
                    }
                    else SaveCheckpoint(true, failed ? "Failed" : canceled ? "Stopped" : "Running");
                }
                catch (Exception saveError)
                {
                    SearchDiagnostics.RecordFailure(output, cfg, saveError, Snapshot(), checkpointPath, "PersistenceFailure",
                        failure?.Message ?? "保存检查点或结果文件失败");
                    if (!failed) throw;
                }
            }
        }
        return new SearchResult(Snapshot(), canceled)
        {
            Resumed = resumed, RecoveredCheckpoint = recoveredCheckpoint,
            RecoveredOutput = recoveredOutput,
            CheckpointPath = checkpointPath, DiagnosticsPath = diagnosticsPath
        };
    }

    public static string ParseSeed(string line)
    {
        line = line.Trim().TrimStart('\uFEFF');
        if (line.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(line);
            line = doc.RootElement.GetProperty("seed").GetString();
        }
        string seed = SeedUtil.Canonicalize(line);
        if (seed.Length == 0) throw new ArgumentException("名单中包含空种子。");
        return seed;
    }

    static bool RecoverOutput(string output, SearchCheckpoint checkpoint)
    {
        if (checkpoint.OutputBytes <= 0 && string.IsNullOrEmpty(checkpoint.OutputSha256)) return false;
        long actual = new FileInfo(output).Length;
        if (actual < checkpoint.OutputBytes)
            throw new InvalidDataException($"结果文件只有 {actual:N0} 字节，但检查点要求至少 {checkpoint.OutputBytes:N0} 字节；为避免跳过未保存结果，未继续搜索。");
        bool truncated = actual > checkpoint.OutputBytes;
        if (checkpoint.OutputBytes > 0 && !string.IsNullOrEmpty(checkpoint.OutputSha256)
            && !string.Equals(DurableFile.TailSha256(output, checkpoint.OutputBytes), checkpoint.OutputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("结果文件尾部校验失败，无法确认它与检查点对应；为避免重复或丢失结果，未继续搜索。");
        if (truncated) DurableFile.Truncate(output, checkpoint.OutputBytes);
        return truncated;
    }

    sealed class Worker
    {
        public SearchCounters Stages = new();
        public int Completed;
    }
    readonly record struct SearchBlock(ulong Ordinal, ulong Start, ulong EndExclusive);
}
