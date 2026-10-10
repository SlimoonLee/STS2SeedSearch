using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeedSearch;

public sealed class SearchCheckpoint
{
    public int Version { get; set; } = 2;
    public string RulesVersion { get; set; } = SearchCheckpointStore.RulesVersion;
    public string Fingerprint { get; set; } = "";
    public string Checksum { get; set; } = "";
    public string Status { get; set; } = "Running";
    public SearchConfig Config { get; set; }
    public ulong BaseIndex { get; set; }
    public ulong EndExclusive { get; set; }
    public ulong BlockSize { get; set; }
    public ulong CompletedCandidates { get; set; }
    public List<CheckpointRange> CompletedBlocks { get; set; } = [];
    public long OutputBytes { get; set; }
    public string OutputSha256 { get; set; } = "";
    public long Hits { get; set; }
    public long Evaluated { get; set; }
    public SearchCounters Stages { get; set; } = new();
    public double ElapsedSeconds { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class CheckpointRange
{
    public ulong Start { get; set; }
    public ulong EndExclusive { get; set; }
}

internal sealed class CheckpointTracker
{
    readonly SortedList<ulong, ulong> _ranges = new();
    readonly ulong _baseIndex, _endExclusive, _blockSize;
    ulong _completedCandidates;

    public CheckpointTracker(ulong baseIndex, ulong endExclusive, ulong blockSize, SearchCheckpoint checkpoint = null)
    {
        if (endExclusive < baseIndex || blockSize == 0) throw new InvalidDataException("检查点范围或块大小无效。");
        _baseIndex = baseIndex; _endExclusive = endExclusive; _blockSize = blockSize;
        if (checkpoint == null) return;
        ulong previousEnd = 0;
        foreach (var range in checkpoint.CompletedBlocks ?? throw new InvalidDataException("检查点缺少完成范围。"))
        {
            if (range.Start < previousEnd || range.Start >= range.EndExclusive || range.EndExclusive > BlockCount)
                throw new InvalidDataException("检查点完成范围越界、重叠或未排序；未跳过任何未经确认的范围。");
            _ranges.Add(range.Start, range.EndExclusive);
            previousEnd = range.EndExclusive;
            _completedCandidates = checked(_completedCandidates + CandidatesInBlocks(range.Start, range.EndExclusive));
        }
        if (_completedCandidates != checkpoint.CompletedCandidates)
            throw new InvalidDataException("检查点完成数量与范围不一致。");
    }

    public ulong BlockCount => (_endExclusive - _baseIndex) / _blockSize + ((_endExclusive - _baseIndex) % _blockSize == 0 ? 0UL : 1UL);
    public ulong CompletedCandidates { get { lock (_ranges) return _completedCandidates; } }

    int Predecessor(ulong ordinal)
    {
        int low = 0, high = _ranges.Count - 1, result = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_ranges.Keys[middle] <= ordinal) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return result;
    }

    public bool IsComplete(ulong ordinal)
    {
        lock (_ranges)
        {
            int prior = Predecessor(ordinal);
            return prior >= 0 && ordinal < _ranges.Values[prior];
        }
    }

    public bool MarkComplete(ulong ordinal)
    {
        lock (_ranges)
        {
            if (ordinal >= BlockCount) throw new ArgumentOutOfRangeException(nameof(ordinal));
            int prior = Predecessor(ordinal);
            if (prior >= 0 && ordinal < _ranges.Values[prior]) return false;
            ulong start = ordinal, end = ordinal + 1;
            if (prior >= 0 && _ranges.Values[prior] == start)
            {
                start = _ranges.Keys[prior];
                _ranges.RemoveAt(prior);
            }
            int next = Predecessor(start) + 1;
            if (next < _ranges.Count && _ranges.Keys[next] == end)
            {
                end = _ranges.Values[next];
                _ranges.RemoveAt(next);
            }
            _ranges.Add(start, end);
            _completedCandidates = checked(_completedCandidates + CandidatesInBlocks(ordinal, ordinal + 1));
            return true;
        }
    }

    // Enumerate gaps, not every old block. Resuming a huge prefix is O(ranges).
    public IEnumerable<CheckpointRange> PendingRanges()
    {
        KeyValuePair<ulong, ulong>[] saved;
        lock (_ranges) saved = _ranges.ToArray();
        ulong next = 0;
        foreach (var pair in saved)
        {
            if (next < pair.Key) yield return new() { Start = next, EndExclusive = pair.Key };
            next = pair.Value;
        }
        if (next < BlockCount) yield return new() { Start = next, EndExclusive = BlockCount };
    }

    public SearchCheckpoint Snapshot(string fingerprint, string status = "Running", long evaluated = 0, long hits = 0,
        SearchCounters stages = null, double elapsedSeconds = 0, string outputSha256 = "", long outputBytes = 0)
    {
        lock (_ranges)
            return new()
            {
                Fingerprint = fingerprint, RulesVersion = SearchCheckpointStore.RulesVersion,
                Status = status, BaseIndex = _baseIndex, EndExclusive = _endExclusive,
                BlockSize = _blockSize, CompletedCandidates = _completedCandidates,
                CompletedBlocks = _ranges.Select(pair => new CheckpointRange { Start = pair.Key, EndExclusive = pair.Value }).ToList(),
                Evaluated = evaluated, Hits = hits, Stages = stages?.Snapshot() ?? new(),
                ElapsedSeconds = elapsedSeconds, OutputSha256 = outputSha256, OutputBytes = outputBytes,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
    }

    ulong CandidatesInBlocks(ulong start, ulong end)
    {
        ulong first = _baseIndex + start * _blockSize;
        ulong last = Math.Min(_endExclusive, _baseIndex + end * _blockSize);
        return last - first;
    }
}

internal static class SearchCheckpointStore
{
    // Change this whenever candidate membership or seed enumeration changes.
    public const string RulesVersion = "v0.111.0-card-window-2";
    public static readonly JsonSerializerOptions JsonOptions = new(SearchConfig.JsonOptions) { IncludeFields = true };
    public static string PathFor(string output) => Path.GetFullPath(output + ".checkpoint.json");

    static string ChecksumFor(SearchCheckpoint checkpoint)
    {
        string previous = checkpoint.Checksum;
        checkpoint.Checksum = "";
        try { return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(checkpoint, JsonOptions)))); }
        finally { checkpoint.Checksum = previous; }
    }

    static SearchCheckpoint Read(string path)
    {
        try
        {
            var checkpoint = JsonSerializer.Deserialize<SearchCheckpoint>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("检查点为空。");
            if (checkpoint.Version is not (1 or 2)) throw new InvalidDataException("不支持的检查点版本。");
            if (checkpoint.Version == 2 && checkpoint.Checksum != ChecksumFor(checkpoint))
                throw new InvalidDataException("检查点校验失败，可能写入不完整或内容已损坏。");
            if (checkpoint.Version == 2 && (checkpoint.OutputBytes < 0 || checkpoint.Hits < 0
                || !double.IsFinite(checkpoint.ElapsedSeconds) || checkpoint.ElapsedSeconds < 0
                || checkpoint.Status is not ("Running" or "Stopped" or "Failed" or "Completed" or "Found")))
                throw new InvalidDataException("检查点元数据无效；未跳过任何未经确认的范围。");
            _ = new CheckpointTracker(checkpoint.BaseIndex, checkpoint.EndExclusive, checkpoint.BlockSize, checkpoint);
            return checkpoint;
        }
        catch (JsonException error) { throw new InvalidDataException($"检查点格式无效：{error.Message}", error); }
    }

    public static SearchCheckpoint Load(string path) => Load(path, out _);
    public static SearchCheckpoint Load(string path, out bool fromBackup)
    {
        fromBackup = false;
        try { return Read(path); }
        catch (Exception primary) when (primary is IOException or JsonException or InvalidDataException)
        {
            if (!File.Exists(path + ".bak")) throw new InvalidDataException($"检查点无法读取：{path}。旧文件已保留。", primary);
            try { var checkpoint = Read(path + ".bak"); fromBackup = true; return checkpoint; }
            catch (Exception backup) { throw new InvalidDataException("主检查点与备份均无法恢复；旧结果和范围文件已保留。", new AggregateException(primary, backup)); }
        }
    }

    public static void SaveAtomic(string path, SearchCheckpoint checkpoint, bool keepBackup = true)
    {
        checkpoint.Checksum = ChecksumFor(checkpoint);
        DurableFile.WriteAtomic(path, JsonSerializer.Serialize(checkpoint, JsonOptions), keepBackup);
    }

    public static string Fingerprint(SearchConfig config, string output, bool legacy = false)
    {
        var normalized = config.ValidatedCopy();
        normalized.resumeFromCheckpoint = false;
        normalized.outFile = Path.GetFullPath(output);
        if (normalized.seedListFile.Length > 0) normalized.seedListFile = Path.GetFullPath(normalized.seedListFile);
        var json = JsonNode.Parse(JsonSerializer.Serialize(normalized, SearchConfig.JsonOptions)).AsObject();
        json.Remove("checkpointIntervalSeconds");
        if (!legacy)
        {
            json.Remove("threads");
            if (normalized.findUntilHit) json.Remove("count");
            json["rulesVersion"] = RulesVersion;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToJsonString(SearchConfig.JsonOptions));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
