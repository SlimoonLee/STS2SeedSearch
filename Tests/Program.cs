using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeedSearch;

static string Fingerprint(string seed, string actName, int ascension)
{
    var act = Game.Act(actName);
    var (rooms, bag) = QueueGen.GenerateAct1(seed, act, "Silent");
    var map = new ActMap(act, Rng.HashString(seed), false, false, false, ascension);
    var rewards = new RewardsReplay(seed, "Silent"); // 历史指纹保留原 A0 奖励；地图进阶独立，A7+ 奖励由真实引擎样本验证。
    var fights = new List<object>();
    for (int i = 0; i < 12; i++)
    {
        var f = rewards.Fight(i % 3 == 2, 0);
        fights.Add(new { f.Gold, f.Potion, cards = f.Cards.Select(c => c.Name), f.EliteRelic,
            relic = f.EliteRelic == null ? "" : bag.Pull(f.EliteRelic) });
    }
    var odds = new UnknownOdds(seed);
    var rolls = Enumerable.Range(0, 15).Select(_ => odds.Roll()).ToArray();
    var (a, b, c) = NeowGen.Generate(seed);
    var data = JsonSerializer.Serialize(new { a, b, c, rooms.Boss, rooms.Ancient, rooms.Events,
        rooms.Normal, rooms.Elite, fights, rolls,
        points = map.GetAllMapPoints().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col)
            .Select(p => new { p.coord.row, p.coord.col, type = p.PointType.ToString(),
                children = p.Children.Select(q => $"{q.coord.row},{q.coord.col}").Order(StringComparer.Ordinal) }) });
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
}

static IEnumerable<(string seed, string act, int asc)> Cases()
{
    yield return ("X6MB8T8NLGQ5", "Overgrowth", 10);
    yield return ("BBBBBBBBBBB2", "Overgrowth", 10);
    yield return ("31YXNJNSU5LX", "Underdocks", 10);
    var rnd = new Random(1209);
    for (int i = 0; i < 48; i++)
    {
        string seed = new(Enumerable.Range(0, 12).Select(_ => SeedUtil.Alphabet[rnd.Next(SeedUtil.Alphabet.Length)]).ToArray());
        yield return (seed, i % 2 == 0 ? "Overgrowth" : "Underdocks", i % 3 == 0 ? 0 : 10);
    }
}

if (args.Length > 0 && args[0] == "golden")
{
    var fixtures = Cases().ToDictionary(c => $"{c.seed}/{c.act}/{c.asc}", c => Fingerprint(c.seed, c.act, c.asc));
    File.WriteAllText(args[1], JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Saved {fixtures.Count} generator fixtures.");
    return;
}

if (args.Length > 0 && args[0] == "benchmark")
{
    Benchmarks.Run(args.Length > 1 ? args[1] : null);
    return;
}

if (args.Length > 0 && args[0] == "benchmark-maps")
{
    Benchmarks.MapModes(args.Length > 1 ? args[1] : null);
    return;
}

var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[0]));
foreach (var entry in expected)
{
    var parts = entry.Key.Split('/');
    if (entry.Value != Fingerprint(parts[0], parts[1], int.Parse(parts[2])))
        throw new Exception($"Generator replay changed: {entry.Key}");
}
Console.WriteLine($"PASS: {expected.Count} generator fixtures (maps, edges, queues, Neow, rewards, relics, unknown rolls).");
NeowReplayTests.Run(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "neow-engine-v0.111.0.json"));
NeowFilterTests.Run();
RelicFilterTests.Run();
EncounterFilterTests.Run();
RegressionTests.Run(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "../..")));
MapModeTests.Run();
FindUntilHitTests.Run();
CheckpointTests.Run();
IdleSearchTests.Run();
