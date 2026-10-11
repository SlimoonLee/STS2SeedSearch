using System.Diagnostics;
using System.Text.Json;

namespace SeedSearch;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: seedsearch validate <seed> [engineMap.jsonl] [save.json]\n" +
                              "      seedsearch search <config.json>");
            return;
        }
        try
        {
        if (args.Length < 2) throw new ArgumentException("请提供种子、名单或配置文件参数。");
        switch (args[0])
        {
            case "validate": Validate(args); break;
            case "dump": Dump(args[1], args.Length > 2 ? args[2] : "Overgrowth", args.Length > 3 ? args[3] : "Silent"); break;
            case "actsel":
            {
                foreach (var seed in File.ReadAllLines(args[1]))
                    if (seed.Trim().Length > 0)
                        Console.WriteLine($"{seed.Trim()} {(ActSelection.Pick(seed.Trim(), true, true) == "Overgrowth" ? "Overgrowth" : "Underdocks")}");
                break;
            }
            case "neowopt":
            {
                foreach (var seed in File.ReadAllLines(args[1]))
                    if (seed.Trim().Length > 0)
                    {
                        var (pa, pb, pc) = NeowGen.Generate(seed.Trim());
                        Console.WriteLine($"{seed.Trim()} {pa} {pb} {pc}");
                    }
                break;
            }
            case "neowstate":
            {
                string seed = args[1], relic = args[2];
                var rr = new RewardsReplay(seed, "Silent");
                var bag = new RelicBag("Silent");
                rr.ApplyNeowEffect(relic, bag);
                Console.WriteLine("rewards " + string.Join(" ", new ulong[] { rr.Rew.S0, rr.Rew.S1, rr.Rew.S2, rr.Rew.S3 }));
                Console.WriteLine("transformations " + string.Join(" ", new ulong[] { rr.Transf.S0, rr.Transf.S1, rr.Transf.S2, rr.Transf.S3 }));
                break;
            }
            case "mapdump":
            {
                // 确定性自检：同一进程内重复构建同一地图，指纹必须每次相同
                string seed = args[1];
                int times = args.Length > 2 ? int.Parse(args[2]) : 5;
                var act = Game.Act(args.Length > 3 ? args[3] : "Overgrowth");
                for (int i = 0; i < times; i++)
                {
                    var map = new ActMap(act, Rng.HashString(seed), false, false, false, 10);
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in map.GetAllMapPoints())
                        sb.Append(p.coord.row).Append(',').Append(p.coord.col).Append(':').Append((int)p.PointType).Append(';');
                    long edges = map.GetAllMapPoints().Sum(p => (long)p.Children.Count);
                    Console.WriteLine($"#{i}: nodes={map.GetAllMapPoints().Count()} edges={edges} fp={Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..22]}");
                }
                break;
            }
            case "search": Search(args[1]); break;
            default: throw new ArgumentException("未知模式: " + args[0]);
        }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0].Message : ex.Message);
            Environment.ExitCode = 1;
        }
    }

    static void Dump(string seed, string actName, string character)
    {
        var act = Game.Act(actName);
        var (rooms, _) = QueueGen.GenerateAct1(seed, act, character);
        Console.WriteLine($"ACT1:{act.Name}");
        Console.WriteLine("  normal=" + string.Join(",", rooms.Normal.Select(Game.Snake)));
        Console.WriteLine("  elite=" + string.Join(",", rooms.Elite.Select(Game.Snake)));
        Console.WriteLine("  events=" + string.Join(",", rooms.Events.Select(Game.Snake)));
        Console.WriteLine("  boss=" + Game.Snake(rooms.Boss) + " ancient=" + rooms.Ancient.ToUpperInvariant());
    }

    // ---------- validate ----------
    static void Validate(string[] args)
    {
        string seed = SeedUtil.Canonicalize(args[1]);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent != null && !Directory.Exists(Path.Combine(directory.FullName, "docs", "validation"))) directory = directory.Parent;
        string mapJson = args.Length > 2 ? args[2] : Path.Combine(directory.FullName, "docs", "validation", $"seed-{seed.ToLowerInvariant()}-engine-a10.jsonl");
        string saveJson = args.Length > 3 ? args[3] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", $"sts2_{seed}.json");

        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} {detail}");
            if (!ok) fails++;
        }

        var act = Game.Act("Overgrowth");
        var (rooms, _) = QueueGen.GenerateAct1(seed, act, "Silent");
        Console.WriteLine($"== 队列重放 ({seed}) ==");
        string saveBoss = null;
        if (File.Exists(saveJson))
            saveBoss = JsonDocument.Parse(File.ReadAllText(saveJson)).RootElement
                .GetProperty("acts")[0].GetProperty("rooms").GetProperty("boss_id").GetString()?.Replace("ENCOUNTER.", "");
        Check("boss" + (saveBoss != null ? " (vs 存档)" : ""), saveBoss == null || Game.Snake(rooms.Boss) == saveBoss, rooms.Boss);
        Check("ancient", rooms.Ancient == "Neow", rooms.Ancient);
        Check("normal 队列 15 个", rooms.Normal.Count == 15, string.Join(",", rooms.Normal));
        Check("weak 前缀", rooms.Normal.Take(act.WeakCount).All(name => act.Weak.Any(e => e.Name == name)));
        Console.WriteLine($"  elite 首个: {Game.Snake(rooms.Elite[0])}");
        Check("elite 队列 15 个", rooms.Elite.Count == 15);
        Check("事件队列数量", rooms.Events.Count == act.Events.Length + GameData.SharedEvents.Length, rooms.Events.Count.ToString());

        Console.WriteLine("== 捏奥 ==");
        var (a, b, c) = NeowGen.Generate(seed);
        Console.WriteLine($"  捏奥三选项: {a} / {b} / {c} (诅咒位)");

        Console.WriteLine("== 地图 ==");
        var map = new ActMap(act, Rng.HashString(seed), false, false, false, 10);
        int nodes = map.GetAllMapPoints().Count();
        Console.WriteLine($"  网格节点数: {nodes}");
        Check("boss点", map.BossMapPoint.coord == (3, 16));

        if (File.Exists(mapJson))
        {
            // 引擎真值对比（get_map 输出: rows -> [{col,row,type,children:[{col,row}]}]）
            var engine = ReadEngineMap(mapJson);
            int mismatch = 0, edges = 0;
            foreach (var (coord, type, kids) in engine)
            {
                var p = map.GetPoint(coord.col, coord.row);
                if (p == null) { mismatch++; if (mismatch <= 20) Console.WriteLine($"  引擎有而我无: r{coord.row}c{coord.col} {type}"); continue; }
                if (p.PointType.ToString() != type) { mismatch++; if (mismatch <= 20) Console.WriteLine($"  类型不同 r{coord.row}c{coord.col}: 引擎={type} 我的={p.PointType}"); }
                else
                {
                    var mine = p.Children.Select(ch => ch.coord).OrderBy(x => x.col).ThenBy(x => x.row).ToList();
                    var theirs = kids.Select(k => (k.col, k.row)).OrderBy(x => x.col).ThenBy(x => x.row).ToList();
                    if (!mine.SequenceEqual(theirs)) { mismatch++; Console.WriteLine($"  连边不同 r{coord.row}c{coord.col}: 引擎=[{string.Join(",", theirs)}] 我的=[{string.Join(",", mine)}]"); }
                    edges += theirs.Count;
                }
            }
            Check($"引擎地图 {engine.Count} 节点/类型/连边全等", engine.Count > 0 && engine.Count == nodes && mismatch == 0, $"不匹配 {mismatch}, 边 {edges}");
        }
        else Check("引擎地图真值存在", false, mapJson);

        if (File.Exists(saveJson))
        {
            using var save = JsonDocument.Parse(File.ReadAllText(saveJson));
            var act0 = save.RootElement.GetProperty("acts")[0].GetProperty("rooms");
            var nq = act0.GetProperty("normal_encounter_ids").EnumerateArray().Select(e => e.GetString()).ToList();
            var eq = act0.GetProperty("elite_encounter_ids").EnumerateArray().Select(e => e.GetString()).ToList();
            var evq = act0.GetProperty("event_ids").EnumerateArray().Select(e => e.GetString()).ToList();
            bool nqOk = nq.Select(x => x.Replace("ENCOUNTER.", "")).SequenceEqual(rooms.Normal.Select(Game.Snake));
            bool eqOk = eq.Select(x => x.Replace("ENCOUNTER.", "")).SequenceEqual(rooms.Elite.Select(Game.Snake));
            bool evOk = evq.Select(x => x.Replace("EVENT.", "")).SequenceEqual(rooms.Events.Select(Game.Snake));
            Check("存档 normal 队列逐位一致", nqOk);
            Check("存档 elite 队列逐位一致", eqOk);
            Check("存档 event 队列逐位一致", evOk);
        }

        Console.WriteLine(fails == 0 ? "== 全部通过 ==" : $"== {fails} 项失败 ==");
        Environment.ExitCode = fails == 0 ? 0 : 1;
    }

    static List<((int col, int row), string type, List<(int col, int row)> kids)> ReadEngineMap(string path)
    {
        var result = new List<((int, int), string, List<(int, int)>)>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.TrimStart().StartsWith("{\"type\":\"map\"")) continue;
            using var doc = JsonDocument.Parse(line);
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                foreach (var n in row.EnumerateArray())
                {
                    var kids = n.GetProperty("children").EnumerateArray()
                        .Select(k => (k.GetProperty("col").GetInt32(), k.GetProperty("row").GetInt32())).ToList();
                    result.Add(((n.GetProperty("col").GetInt32(), n.GetProperty("row").GetInt32()),
                                n.GetProperty("type").GetString(), kids));
                }
        }
        return result;
    }

    // ---------- search ----------
    static void Search(string configPath)
    {
        var cfg = SearchConfig.Load(configPath);
        Console.WriteLine($"搜种器: {cfg.character} A{cfg.ascension} 幕={cfg.act1} 模式={cfg.routeMode} 前{cfg.minFightsChecked}场 地图={cfg.mapMode} 截止第{cfg.maxSearchFloor}层 线程={(cfg.threads == 0 ? Environment.ProcessorCount : cfg.threads)}");
        if (cfg.minFightsChecked == 0 && cfg.unknownMonsterStreak == 0) Console.WriteLine("仅筛开局：Boss / 捏奥 / 第一幕及已应用的捏奥卡牌，不生成地图。");
        Console.WriteLine(cfg.seedListFile.Length > 0 ? $"名单模式: {cfg.seedListFile}"
            : cfg.findUntilHit ? $"找到为止: 从索引 {cfg.startIndex} 持续搜索，不限制数量，随机键={cfg.rngSeed}"
            : $"范围模式: [{cfg.startIndex}, {cfg.startIndex + (ulong)cfg.count}) 随机键={cfg.rngSeed}");
        if (cfg.idleSearch) Console.WriteLine("闲时搜索：低优先级运行，按其他应用 CPU 负载自动让步；负载下降后恢复。");
        if (cfg.seedListFile.Length == 0)
            Console.WriteLine(cfg.resumeFromCheckpoint ? $"自动续跑: 检查 {Path.GetFullPath(cfg.outFile)}.checkpoint.json" : "自动续跑: 关闭（忽略旧检查点）");
        if (cfg.findUntilHit) Console.WriteLine("找到一个符合全部条件的种子后保存并自动停止；Ctrl+C 可手动停止。");
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = SearchEngine.Run(cfg, null, p =>
                Console.WriteLine($"评估 {p.Evaluated:N0}{(p.Total.HasValue ? $"/{p.Total:N0}" : "")} · 命中 {p.Hits:N0} · {p.SeedsPerSec:N0} seeds/s · {p.Elapsed.TotalSeconds:F1}s{(p.Idle == null ? "" : " · " + p.Idle.Description)}"), cts.Token);
            string outcome = cfg.findUntilHit && result.Progress.Hits > 0 ? "已找到，自动停止" : result.Canceled ? "已停止" : "完成";
            Console.WriteLine($"{outcome}{(result.Resumed ? "（从检查点续跑" + (result.RecoveredCheckpoint ? "备份" : "") + (result.RecoveredOutput ? "，已回退尾部" : "") + "）" : "")}: 评估 {result.Progress.Evaluated:N0} · 保存 {result.Progress.Hits:N0} 个结果 · {result.Progress.Stages}");
            Console.WriteLine($"排错记录: {result.DiagnosticsPath}");
            if (cfg.findUntilHit && !result.Canceled && result.Progress.Hits == 0)
                Console.WriteLine(cfg.seedListFile.Length > 0 ? "输入名单已处理完，未找到符合条件的种子。" : "当前起始索引之后的种子空间已搜索完，未找到符合条件的种子。");
            Console.WriteLine($"结果: {Path.GetFullPath(cfg.outFile)}");
            if (result.Canceled) Environment.ExitCode = 130;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.WriteLine("已停止"); Environment.ExitCode = 130;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
