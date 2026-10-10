using System.Diagnostics;
using System.Text;

namespace SeedSearch;

/// <summary>
/// StandardActMap + MapPathPruning + MapPostProcessing 的逐位转写（v0.111.0）。
/// MapPoint.parents/Children 用引用相等 HashSet 保持插入序；GetAllMapPoints 列优先。
/// </summary>
public sealed class ActMap
{
    public readonly HashSet<MapPoint> startMapPoints = new();   // 同上：身份哈希桶序与引擎一致
    public readonly MapPoint[,] Grid;
    public readonly MapPoint BossMapPoint, StartingMapPoint;
    public MapPoint SecondBossMapPoint;
    readonly MapPointTypeCounts _counts;
    readonly int _len;
    readonly Rng _rng;
    bool _replaceTreasure;
    bool _layoutApplied;

    static readonly HashSet<MapPointType> LowerRestrictions = new() { MapPointType.RestSite, MapPointType.Elite };
    static readonly HashSet<MapPointType> UpperRestrictions = new() { MapPointType.RestSite };
    static readonly HashSet<MapPointType> ParentRestrictions = new() { MapPointType.Elite, MapPointType.RestSite, MapPointType.Treasure, MapPointType.Shop };
    static readonly HashSet<MapPointType> SiblingRestrictions = new() { MapPointType.RestSite, MapPointType.Monster, MapPointType.Unknown, MapPointType.Elite, MapPointType.Shop };

    public sealed class MapPointTypeCounts
    {
        public int NumOfElites { get; set; }
        public int NumOfShops { get; set; }
        public int NumOfUnknowns { get; set; }
        public int NumOfRests { get; set; }
    }

    public static long TGen, TAssign, TPrune, TPost, TPaths;

    public ActMap(ActDef act, ulong seed, bool multiplayer, bool replaceTreasure, bool hasSecondBoss, int ascension, bool applyLayout = true)
    {
        var sw = Stopwatch.StartNew();
        _rng = new Rng(seed, $"act_{act.Index + 1}_map");
        _len = act.Rooms - (multiplayer ? 1 : 0) + 1;
        _replaceTreasure = replaceTreasure;
        Grid = new MapPoint[7, _len];
        int elites = (int)Math.Round(5f * (ascension >= 1 ? 1.6f : 1f));
        int rests = act.HasRestGauss
            ? _rng.NextGaussianInt(act.RestGauss.A, act.RestGauss.B, act.RestGauss.C, act.RestGauss.D)
            : _rng.NextInt(act.RestUniform.A, act.RestUniform.B);
        int unknowns = _rng.NextGaussianInt(12, 1, 10, 14) - (act.UnknownMinus ? 1 : 0);
        _counts = new MapPointTypeCounts { NumOfElites = elites, NumOfShops = 3, NumOfRests = rests, NumOfUnknowns = unknowns };
        BossMapPoint = new MapPoint(7 / 2, GetRowCount());
        StartingMapPoint = new MapPoint(7 / 2, 0);
        if (hasSecondBoss) SecondBossMapPoint = new MapPoint(7 / 2, GetRowCount() + 1);
        sw.Restart(); GenerateMap(); Interlocked.Add(ref TGen, sw.ElapsedTicks);
        sw.Restart(); AssignPointTypes(); Interlocked.Add(ref TAssign, sw.ElapsedTicks);
        sw.Restart(); PruneAndRepair(); Interlocked.Add(ref TPrune, sw.ElapsedTicks);
        if (applyLayout) CompleteLayout();
    }

    /// <summary>只整理坐标，不改变房间类型、连边或 RNG；简图只在命中后补齐，保持导出坐标一致。</summary>
    public void CompleteLayout()
    {
        if (_layoutApplied) return;
        var sw = Stopwatch.StartNew();
        MapPostProcessing.CenterGrid(Grid); MapPostProcessing.SpreadAdjacentMapPoints(Grid); MapPostProcessing.StraightenPaths(Grid);
        _layoutApplied = true;
        Interlocked.Add(ref TPost, sw.ElapsedTicks);
    }

    public int GetRowCount() => Grid.GetLength(1);
    public int GetColumnCount() => Grid.GetLength(0);

    public IEnumerable<MapPoint> GetAllMapPoints()
    {
        for (int c = 0; c < GetColumnCount(); c++)
            for (int r = 0; r < GetRowCount(); r++)
                if (Grid[c, r] != null) yield return Grid[c, r];
    }

    public MapPoint GetPoint(int col, int row)
    {
        if (col == BossMapPoint.coord.col && row == BossMapPoint.coord.row) return BossMapPoint;
        if (SecondBossMapPoint != null && col == SecondBossMapPoint.coord.col && row == SecondBossMapPoint.coord.row) return SecondBossMapPoint;
        if (col == StartingMapPoint.coord.col && row == StartingMapPoint.coord.row) return StartingMapPoint;
        return Grid[col, row];
    }

    /// <summary>判断是否存在一条从开局到 Boss 且不经过指定房间类型的路线。</summary>
    public bool HasPathToBossAvoiding(MapPointType forbidden)
    {
        var seen = new HashSet<MapPoint>();
        var pending = new Stack<MapPoint>();
        pending.Push(StartingMapPoint);
        while (pending.Count > 0)
        {
            var point = pending.Pop();
            if (!seen.Add(point) || point.PointType == forbidden) continue;
            if (ReferenceEquals(point, BossMapPoint)) return true;
            foreach (var child in point.Children) pending.Push(child);
        }
        return false;
    }

    void GenerateMap()
    {
        for (int i = 0; i < 7; i++)
        {
            var p = GetOrCreatePoint(_rng.NextInt(0, 7), 1);
            if (i == 1)
                while (startMapPoints.Contains(p))
                    p = GetOrCreatePoint(_rng.NextInt(0, 7), 1);
            startMapPoints.Add(p);
            PathGenerate(p);
        }
        foreach (var x in RowPoints(GetRowCount() - 1)) x.AddChild(BossMapPoint);
        if (SecondBossMapPoint != null) BossMapPoint.AddChild(SecondBossMapPoint);
        foreach (var x in RowPoints(1)) StartingMapPoint.AddChild(x);
    }

    IEnumerable<MapPoint> RowPoints(int row)
    {
        for (int c = 0; c < 7; c++)
            if (Grid[c, row] != null) yield return Grid[c, row];
    }

    MapPoint GetOrCreatePoint(int col, int row)
    {
        var p = Grid[col, row];
        if (p != null) return p;
        p = new MapPoint(col, row);
        Grid[col, row] = p;
        return p;
    }

    void PathGenerate(MapPoint start)
    {
        var cur = start;
        while (cur.coord.row < _len - 1)
        {
            var coord = NextCoord(cur);
            var next = GetOrCreatePoint(coord.col, coord.row);
            cur.AddChild(next);
            cur = next;
        }
    }

    (int col, int row) NextCoord(MapPoint cur)
    {
        int col = cur.coord.col;
        var candidates = new List<int> { -1, 0, 1 };
        candidates.StableShuffle(_rng);
        foreach (var dx in candidates)
        {
            int target = dx switch { -1 => Math.Max(0, col - 1), 1 => Math.Min(col + 1, 6), _ => col };
            if (!HasInvalidCrossover(cur, target))
                return (target, cur.coord.row + 1);
        }
        throw new InvalidOperationException("Cannot find next node");
    }

    bool HasInvalidCrossover(MapPoint cur, int targetX)
    {
        int num = targetX - cur.coord.col;
        if (num == 0) return false;
        var node = Grid[targetX, cur.coord.row];
        if (node == null) return false;
        foreach (var child in node.Children)
            if (child.coord.col - node.coord.col == -num) return true;
        return false;
    }

    void AssignPointTypes()
    {
        foreach (var p in RowPoints(GetRowCount() - 1)) { p.PointType = MapPointType.RestSite; p.CanBeModified = false; }
        var treasureType = _replaceTreasure ? MapPointType.Elite : MapPointType.Treasure;
        foreach (var p in RowPoints(GetRowCount() - 7)) { p.PointType = treasureType; p.CanBeModified = false; }
        foreach (var p in RowPoints(1)) { p.PointType = MapPointType.Monster; p.CanBeModified = false; }

        var queue = new Queue<MapPointType>();
        for (int i = 0; i < _counts.NumOfRests; i++) queue.Enqueue(MapPointType.RestSite);
        for (int i = 0; i < _counts.NumOfShops; i++) queue.Enqueue(MapPointType.Shop);
        for (int i = 0; i < _counts.NumOfElites; i++) queue.Enqueue(MapPointType.Elite);
        for (int i = 0; i < _counts.NumOfUnknowns; i++) queue.Enqueue(MapPointType.Unknown);

        AssignRemaining(queue);
        foreach (var p in GetAllMapPoints().Where(p => p.PointType == MapPointType.Unassigned).ToList())
            p.PointType = MapPointType.Monster;
        BossMapPoint.PointType = MapPointType.Boss;
        StartingMapPoint.PointType = MapPointType.Ancient;
        if (SecondBossMapPoint != null) SecondBossMapPoint.PointType = MapPointType.Boss;
    }

    void AssignRemaining(Queue<MapPointType> queue)
    {
        for (int round = 0; round < 3; round++)
        {
            if (queue.Count <= 0) break;
            var list = GetAllMapPoints().Where(p => p.PointType == MapPointType.Unassigned).ToList();
            list.StableShuffle(_rng);
            foreach (var p in list)
            {
                if (queue.Count == 0) break;
                p.PointType = NextValidType(queue, p);
            }
        }
    }

    MapPointType NextValidType(Queue<MapPointType> queue, MapPoint p)
    {
        for (int i = 0; i < queue.Count; i++)
        {
            var t = queue.Dequeue();
            if (IsValidType(t, p)) return t;
            queue.Enqueue(t);
        }
        return MapPointType.Unassigned;
    }

    public bool IsValidType(MapPointType t, MapPoint p)
    {
        if (p.coord.row < 6 && LowerRestrictions.Contains(t)) return false;
        if (p.coord.row >= _len - 3 && UpperRestrictions.Contains(t)) return false;
        if (ParentRestrictions.Contains(t) && p.parents.Concat(p.Children).Any(x => x.PointType == t)) return false;
        if (SiblingRestrictions.Contains(t) && Siblings(p).Any(x => x.PointType == t)) return false;
        return true;
    }

    static IEnumerable<MapPoint> Siblings(MapPoint p) =>
        p.parents.SelectMany(x => x.Children).Where(x => !ReferenceEquals(x, p));

    // ---------- 剪枝 ----------
    void PruneAndRepair()
    {
        for (int i = 0; i < 3; i++)
        {
            PruneDuplicateSegments();
            if (!RepairPrunedTypes()) break;
        }
    }

    bool RepairPrunedTypes()
    {
        bool flag = RepairType(MapPointType.Shop, _counts.NumOfShops);
        flag |= RepairType(MapPointType.Elite, _counts.NumOfElites);
        flag |= RepairType(MapPointType.RestSite, _counts.NumOfRests);
        return flag | RepairType(MapPointType.Unknown, _counts.NumOfUnknowns);
    }

    bool RepairType(MapPointType type, int target)
    {
        int deficit = target - GetAllMapPoints().Count(p => p.PointType == type);
        if (deficit <= 0) return false;
        bool result = false;
        var candidates = GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster && p.CanBeModified).ToList();
        candidates.StableShuffle(_rng);
        foreach (var p in candidates)
        {
            if (deficit == 0) break;
            if (IsValidType(type, p)) { p.PointType = type; deficit--; result = true; }
        }
        return result;
    }

    void PruneDuplicateSegments()
    {
        int n = 0;
        var matching = FindMatchingSegments();
        while (PrunePaths(matching))
        {
            if (++n > 50) throw new InvalidOperationException("Unable to prune");
            matching = FindMatchingSegments();
        }
    }

    public static long TFind, TSort; public static long NPaths, NSegs, NRounds;

    List<List<MapPoint[]>> FindMatchingSegments()
    {
        var sw = Stopwatch.StartNew();
        var buckets = new Dictionary<SegmentSignature, List<MapPoint[]>>();
        var path = new List<MapPoint>(_len + 1);
        // 每个起点直接遍历到合流点，不展开起点之前和终点之后的完整路线。
        // 同一 key 内仍按 Children 的 DFS 顺序接受首个不重叠的路段；组间随后按引擎 key 排序。
        CollectFrom(StartingMapPoint);
        foreach (var point in GetAllMapPoints())
            if (IsValidSegmentStart(point)) CollectFrom(point);
        Interlocked.Add(ref TFind, sw.ElapsedTicks);
        sw.Restart();

        var withKeys = new List<(string key, List<MapPoint[]> group)>(buckets.Count);
        foreach (var (signature, segments) in buckets)
            if (segments.Count > 1) withKeys.Add((BuildKey(signature), segments));
        withKeys.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        var result = new List<List<MapPoint[]>>(withKeys.Count);
        foreach (var kv in withKeys) result.Add(kv.group);
        Interlocked.Add(ref TSort, sw.ElapsedTicks);
        Interlocked.Add(ref NSegs, buckets.Count);
        return result;

        void CollectFrom(MapPoint source)
        {
            path.Clear();
            Visit(source, 0);
        }

        void Visit(MapPoint current, UInt128 types)
        {
            path.Add(current);
            types = (types << 4) | (uint)current.PointType;
            if (path.Count >= 3 && current.parents.Count >= 2) AddSegment(path, types, buckets);
            if (current.PointType != MapPointType.Boss)
                foreach (var child in current.Children) Visit(child, types);
            path.RemoveAt(path.Count - 1);
        }
    }

    // 现有地图最长17个节点；每种房间占4位，128位精确编码避免类型数组、哈希桶和重复逐路径扫描。
    readonly record struct SegmentSignature(uint Coordinates, UInt128 Types);

    static string BuildKey(SegmentSignature signature)
    {
        int sc = (int)(signature.Coordinates >> 24), sr = (int)(signature.Coordinates >> 16 & 255),
            ec = (int)(signature.Coordinates >> 8 & 255), er = (int)(signature.Coordinates & 255);
        var sb = new System.Text.StringBuilder(64);
        if (sr == 0)
            sb.Append(sr).Append('-').Append(ec).Append(',').Append(er).Append('-');
        else
            sb.Append(sc).Append(',').Append(sr).Append('-').Append(ec).Append(',').Append(er).Append('-');
        int length = er - sr + 1;
        for (int i = 0; i < length; i++)
        { if (i > 0) sb.Append(','); sb.Append((uint)(signature.Types >> ((length - i - 1) * 4) & 15)); }
        return sb.ToString();
    }

    static bool IsValidSegmentStart(MapPoint p) => p.Children.Count > 1 || p.coord.row == 0;

    static void AddSegment(List<MapPoint> path, UInt128 types, Dictionary<SegmentSignature, List<MapPoint[]>> buckets)
    {
        int sc = path[0].coord.col, sr = path[0].coord.row;
        int ec = path[^1].coord.col, er = path[^1].coord.row;
        if (sr == 0) sc = 0;
        var key = new SegmentSignature((uint)(sc << 24 | sr << 16 | ec << 8 | er), types);
        if (!buckets.TryGetValue(key, out var segments)) { segments = new(); buckets[key] = segments; }
        foreach (var existing in segments)
            for (int i = 1; i < existing.Length - 1; i++)
                if (ReferenceEquals(existing[i], path[i])) return;
        segments.Add(path.ToArray());
    }

    bool PrunePaths(IEnumerable<List<MapPoint[]>> groups)
    {
        foreach (var group in groups)
        {
            group.UnstableShuffle(_rng);
            if (PruneAllButLast(group) != 0) return true;
            if (BreakParentChildInAny(group)) return true;
        }
        return false;
    }

    int PruneAllButLast(List<MapPoint[]> matches)
    {
        int num = 0;
        foreach (var match in matches)
        {
            if (num == matches.Count - 1) return num;
            if (PruneSegment(match)) num++;
        }
        return num;
    }

    bool PruneSegment(MapPoint[] segment)
    {
        bool result = false;
        for (int i = 0; i < segment.Length - 1; i++)
        {
            var p = segment[i];
            if (!IsInMap(p)) return true;
            if (p.Children.Count > 1 || p.parents.Count > 1 ||
                p.parents.Any(n => n.Children.Count == 1 && !IsRemoved(n)))
                continue;
            var source = segment.Skip(i);
            if (!source.Any(n => n.Children.Count > 1 && n.parents.Count == 1))
            {
                if (segment[^1].parents.Count == 1) return false;
                if (!p.Children.Where(c => !segment.Contains(c))  // 引用相等（MapPoint 未重写 Equals）
                        .Any(c => c.parents.Count == 1))
                {
                    RemovePoint(p);
                    result = true;
                }
            }
        }
        return result;
    }

    void RemovePoint(MapPoint p)
    {
        Grid[p.coord.col, p.coord.row] = null;
        startMapPoints.Remove(p);
        foreach (var c in p.Children.ToList()) p.RemoveChild(c);
        foreach (var parent in p.parents.ToList()) parent.RemoveChild(p);
    }

    bool IsInMap(MapPoint p) =>
        Grid[p.coord.col, p.coord.row] != null || p.PointType == MapPointType.Ancient || p.PointType == MapPointType.Boss;

    bool IsRemoved(MapPoint p) => Grid[p.coord.col, p.coord.row] == null;

    static bool BreakParentChildInAny(List<MapPoint[]> groups)
    {
        foreach (var seg in groups)
            if (BreakParentChildInSegment(seg)) return true;
        return false;
    }

    static bool BreakParentChildInSegment(MapPoint[] segment)
    {
        bool result = false;
        for (int i = 0; i < segment.Length - 1; i++)
        {
            var p = segment[i];
            if (p.Children.Count >= 2)
            {
                var c = segment[i + 1];
                if (c.parents.Count != 1) { p.RemoveChild(c); result = true; }
            }
        }
        return result;
    }
}

/// <summary>CenterGrid / SpreadAdjacentMapPoints / StraightenPaths，无 RNG 但改坐标。</summary>
public static class MapPostProcessing
{
    public static void CenterGrid(MapPoint[,] grid)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        bool leftEmpty = IsColEmpty(grid, 0) && IsColEmpty(grid, 1);
        bool rightEmpty = IsColEmpty(grid, w - 1) && IsColEmpty(grid, w - 2);
        int shift = 0;
        if (leftEmpty && !rightEmpty) shift = -1;
        else if (!leftEmpty && rightEmpty) shift = 1;
        if (shift == 0) return;
        if (shift > 0)
        {
            for (int r = 0; r < h; r++)
                for (int c = w - 1; c >= 0; c--)
                {
                    var p = grid[c, r];
                    grid[c, r] = null;
                    int nc = c + shift;
                    if (nc < w) { grid[nc, r] = p; if (p != null) p.coord.col = nc; }
                }
        }
        else
        {
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    var p = grid[c, r];
                    grid[c, r] = null;
                    int nc = c + shift;
                    if (nc >= 0) { grid[nc, r] = p; if (p != null) p.coord.col = nc; }
                }
        }
    }

    static bool IsColEmpty(MapPoint[,] grid, int col)
    {
        for (int r = 0; r < grid.GetLength(1); r++)
            if (grid[col, r] != null) return false;
        return true;
    }

    public static void StraightenPaths(MapPoint[,] grid)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                var p = grid[c, r];
                if (p == null || p.parents.Count != 1 || p.Children.Count != 1) continue;
                var parent = p.parents.First();
                var child = p.Children.First();
                bool goRight = p.coord.col < child.coord.col && p.coord.col < parent.coord.col;
                bool goLeft = p.coord.col > child.coord.col && p.coord.col > parent.coord.col;
                if (goRight && c < w - 1)
                {
                    if (grid[c + 1, r] != null) continue;
                    p.coord.col = c + 1;
                    grid[c, r] = null;
                    grid[c + 1, r] = p;
                }
                if (goLeft && c > 0)
                {
                    int nc = c - 1;
                    if (grid[nc, r] == null)
                    {
                        p.coord.col = nc;
                        grid[c, r] = null;
                        grid[nc, r] = p;
                    }
                }
            }
    }

    public static void SpreadAdjacentMapPoints(MapPoint[,] grid)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        for (int r = 0; r < h; r++)
        {
            var rowNodes = new List<MapPoint>();
            for (int c = 0; c < w; c++)
                if (grid[c, r] != null) rowNodes.Add(grid[c, r]);
            bool moved;
            do
            {
                moved = false;
                foreach (var p in rowNodes)
                {
                    int col = p.coord.col;
                    var allowed = AllowedPositions(p, w);
                    int bestGap = Gap(col, rowNodes, p);
                    int bestCol = col;
                    foreach (var cand in allowed)
                    {
                        if (cand != col && (grid[cand, r] == null || grid[cand, r] == p))
                        {
                            int gap = Gap(cand, rowNodes, p);
                            if (gap > bestGap) { bestCol = cand; bestGap = gap; }
                        }
                    }
                    if (bestCol != col)
                    {
                        grid[col, r] = null;
                        grid[bestCol, r] = p;
                        p.coord.col = bestCol;
                        moved = true;
                    }
                }
            } while (moved);
        }
    }

    static HashSet<int> NeighborPositions(int col, int total)
    {
        var set = new HashSet<int>();
        for (int i = -1; i <= 1; i++)
        {
            int n = col + i;
            if (n >= 0 && n < total) set.Add(n);
        }
        return set;
    }

    static HashSet<int> AllowedPositions(MapPoint p, int total)
    {
        var set = new HashSet<int>(Enumerable.Range(0, total));
        foreach (var parent in p.parents) set.IntersectWith(NeighborPositions(parent.coord.col, total));
        foreach (var child in p.Children) set.IntersectWith(NeighborPositions(child.coord.col, total));
        return set;
    }

    static int Gap(int candidateCol, List<MapPoint> rowNodes, MapPoint current)
    {
        int min = int.MaxValue;
        foreach (var n in rowNodes)
            if (!ReferenceEquals(n, current))
                min = Math.Min(min, Math.Abs(candidateCol - n.coord.col));
        return min;
    }
}
