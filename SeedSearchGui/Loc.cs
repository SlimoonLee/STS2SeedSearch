using System.Text.Json;

namespace SeedSearchGui;

/// <summary>
/// 中文本地化：运行时加载 sts2-cli/localization_zhs/*.json（从程序目录向上查找仓库根）。
/// 找不到时回退为原始 id，不影响功能。
/// </summary>
public static class Loc
{
    static readonly Dictionary<string, string> _cards, _relics, _encounters, _events;
    public static bool Loaded { get; }

    static Loc()
    {
        (_cards, _relics, _encounters, _events) = (new(), new(), new(), new());
        string dir = FindLocDir();
        if (dir == null) return;
        try
        {
            Load(_cards, Path.Combine(dir, "cards.json"));
            Load(_relics, Path.Combine(dir, "relics.json"));
            Load(_encounters, Path.Combine(dir, "encounters.json"));
            Load(_events, Path.Combine(dir, "events.json"));
            Loaded = true;
        }
        catch { }
    }

    static void Load(Dictionary<string, string> into, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Name.EndsWith(".title") || p.Name.EndsWith(".name"))
                into[p.Name[..p.Name.LastIndexOf('.')]] = Strip(p.Value.GetString());
    }

    static string Strip(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\[/?\w+\]", "");

    static string FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "sts2-cli", "localization_zhs");
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    public static string Card(string cls) => _cards.TryGetValue(Snake(cls), out var v) ? v : cls;
    public static string Relic(string cls) => _relics.TryGetValue(Snake(cls), out var v) ? v : cls;
    public static string Encounter(string eid) => _encounters.TryGetValue(eid, out var v) ? v : eid;

    public static string Snake(string name) => SeedSearch.Game.Snake(name);

    public static readonly (string Id, string Cn)[] Characters =
    {
        ("Ironclad", "战士 Ironclad"),
        ("Silent", "猎人 Silent"),
        ("Defect", "机器人 Defect"),
        ("Regent", "摄政王 Regent"),
        ("Necrobinder", "缚灵师 Necrobinder"),
    };

    public static readonly (string Id, string Cn)[] Acts =
    {
        ("", "任意第一幕"),
        ("Overgrowth", "密林 Overgrowth"),
        ("Underdocks", "暗港 Underdocks"),
    };

    public static string RarityCn(string r) => r switch
    {
        "Basic" => "基础", "Common" => "普通", "Uncommon" => "罕见", "Rare" => "稀有",
        "Ancient" => "远古", "Event" => "事件", "Token" => "衍生物", "Status" => "状态",
        "Curse" => "诅咒", _ => r,
    };
}
