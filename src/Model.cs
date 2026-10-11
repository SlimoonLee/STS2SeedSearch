namespace SeedSearch;

public readonly record struct Int2(int A, int B);
public readonly record struct Int4(int A, int B, int C, int D);

// Monsters is the complete member set declared by the game's AllPossibleMonsters
// for this encounter.  Older generated tables may omit it, so keep the
// parameter optional for backwards-compatible configs and fixtures.
public sealed record EncDef(string Name, string RoomType, bool Weak, string[] Tags, string[] Monsters = null);
public sealed record ActDef(
    string Name, int Index, int Rooms, int WeakCount,
    bool HasRestGauss, Int4 RestGauss, Int2 RestUniform, bool UnknownMinus,
    string[] Events, string[] Ancients, EncDef[] Encounters)
{
    public IEnumerable<EncDef> Weak => Encounters.Where(e => e.RoomType == "Monster" && e.Weak);
    public IEnumerable<EncDef> Regular => Encounters.Where(e => e.RoomType == "Monster" && !e.Weak);
    public IEnumerable<EncDef> Elites => Encounters.Where(e => e.RoomType == "Elite");
    public IEnumerable<EncDef> Bosses => Encounters.Where(e => e.RoomType == "Boss");
}

public enum MapPointType { Unassigned, Unknown, Shop, Treasure, RestSite, Monster, Elite, Boss, Ancient }

/// <summary>地图节点：parents/Children 保持引用相等 + 插入序（与引擎 HashSet 语义一致）。</summary>
public sealed class MapPoint : IComparable<MapPoint>
{
    public (int col, int row) coord;
    // 必须保持 HashSet<MapPoint>：引擎地图的遍历序依赖 .NET 对象身份哈希的桶序，
    // 引擎（Godot/.NET）与本项目在相同分配序列下迭代序一致——换成 List（纯插入序）会与引擎发散。
    // 运行时升级后必须重跑 validate 全套对拍。
    public HashSet<MapPoint> parents = new();
    public HashSet<MapPoint> Children = new();
    public bool CanBeModified = true;
    public MapPointType PointType;

    public MapPoint(int col, int row) { coord = (col, row); }

    public int CompareTo(MapPoint other) =>
        coord.col != other.coord.col ? coord.col.CompareTo(other.coord.col) : coord.row.CompareTo(other.coord.row);

    public void AddChild(MapPoint child) { Children.Add(child); child.parents.Add(this); }
    public void RemoveChild(MapPoint child) { Children.Remove(child); child.parents.Remove(this); }
    public override string ToString() => $"Point[{coord.col},{coord.row}]";
}

public static class Game
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SnakeNames = new(StringComparer.Ordinal);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> CardFilterPools = new(StringComparer.Ordinal);
    public static string[] FilterableCards(string character) => CardFilterPools.GetOrAdd(character, static character =>
        GameData.CardPools[character + "CardPool"].Concat(GameData.CardPools["ColorlessCardPool"])
            .Concat(new[] { "Ironclad", "Silent", "Defect", "Regent", "Necrobinder" }.Where(c => c != character)
                .SelectMany(c => GameData.CardPools[c + "CardPool"]))
            .Where(c => !GameData.CardMultiplayerOnly.Contains(c)).Distinct(StringComparer.Ordinal).ToArray());
    public static ActDef Act(string name) => GameData.Acts.First(a => a.Name == name);
    public static string Snake(string cls) => SnakeNames.GetOrAdd(cls, static name =>
    {
        if (!name.Any(char.IsLower)) return name.ToUpperInvariant();
        var text = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) text.Append('_');
            text.Append(char.ToUpperInvariant(name[i]));
        }
        return text.ToString();
    });
    public static string RoomTypeOf(string actName) => Act(actName).Name;
}
