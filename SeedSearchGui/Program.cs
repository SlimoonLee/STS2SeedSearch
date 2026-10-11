using System.Drawing.Imaging;
using System.Text.Json;
using SeedSearch;

namespace SeedSearchGui;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "selftest")
        {
            try
            {
                using var options = new SelectionPanel(true);
                options.SetItems(new[] { new SelectionOption("Backflip", "后空翻", "普通"), new SelectionOption("Footwork", "步法", "罕见"), new SelectionOption("Burst", "爆发", "稀有") });
                options.SelectIds(new[] { "Footwork", "Burst" }); options.Filter = "后空翻"; options.SetVisible(true);
                if (options.VisibleCount != 1 || options.CheckedIds().Length != 3) throw new Exception("过滤改变了隐藏勾选项。");
                options.Filter = ""; options.SetItems(new[] { new SelectionOption("Backflip", "后空翻") });
                if (options.OptionCount != 1 || options.CheckedIds().Length != 1) throw new Exception("替换列表遗留旧选项。");
                options.SetItems(new[] { new SelectionOption("Backflip", "后空翻", "普通", "当前职业"),
                    new SelectionOption("Footwork", "步法", "罕见", "当前职业"), new SelectionOption("Anger", "愤怒", "普通", "铁甲战士", false) });
                options.SelectOrdinary();
                if (!options.CheckedIds().ToHashSet().SetEquals(new[] { "Backflip", "Footwork" })) throw new Exception("本职业快捷选择误选异色卡牌。");
                options.Filter = "铁甲战士";
                if (options.VisibleCount != 1 || options.CheckedIds().Length != 2) throw new Exception("卡牌来源搜索或隐藏勾选项错误。");
                if (!Loc.Loaded || Loc.Encounter("THE_KIN_BOSS") == "THE_KIN_BOSS" || Loc.Card("Backflip") == "Backflip")
                    throw new Exception("中文本地化未加载。");
                using var form = new MainForm(false);
                if (form.ReadConfig().maxSearchFloor != 10 || form.ReadConfig().mapMode != "search") throw new Exception("新建界面未默认使用前10层简图。");
                form.VerifyCharacterSelectionMemory();
                var cfg = new SearchConfig { character = "Defect", act1 = "Underdocks", routeMode = "exists", checkFromFirstUnknown = false,
                    minFightsChecked = 3, unknownMonsterStreak = 1, mapMode = "full", maxSearchFloor = 10, allowedCards = new[] { "Claw", "Coolheaded", "Anger", GameData.CardPools["ColorlessCardPool"].First(c => !GameData.CardMultiplayerOnly.Contains(c)) },
                    neowPositives = new[] { "LavaRock", "WingedBoots" }, neowCurses = new[] { "DowsingRod" }, neowChoice = 1,
                    normalStrongPrefixLength = 2, normalStrongAllow = new[] { "CorpseSlugsNormal", "CultistsNormal", "FossilStalkerNormal", "GremlinMercNormal", "HauntedShipNormal" },
                    firstEliteAllow = new[] { "PhantasmalGardenersElite" }, eliteBlock = true, noRestBeforeFirstElite = true,
                    findUntilHit = true, count = 300, threads = 4, rngSeed = ulong.MaxValue, startIndex = 12345, outFile = Path.Combine(Path.GetTempPath(), "gui-test.jsonl") };
                form.ApplyConfig(cfg);
                form.VerifyEncounterControls();
                form.VerifyRelicControls();
                if (JsonSerializer.Serialize(cfg.ValidatedCopy()) != JsonSerializer.Serialize(form.ReadConfig())) throw new Exception("配置往返丢失条件。");
                string deliveryPath = Path.Combine(Path.GetTempPath(), "gui-delivery-" + Guid.NewGuid().ToString("N") + ".jsonl");
                string firstPath = Path.Combine(Path.GetTempPath(), "gui-first-" + Guid.NewGuid().ToString("N") + ".jsonl");
                try
                {
                    Complete(form.VerifyResultDelivery(deliveryPath));
                    Complete(form.VerifyFindUntilHit(firstPath));
                }
                finally
                {
                    if (File.Exists(deliveryPath)) File.Delete(deliveryPath);
                    if (File.Exists(firstPath)) File.Delete(firstPath);
                }
                Hit preview = null;
                SearchConfig previewConfig = null;
                foreach (string relic in new[] { "ArcaneScroll", "HeftyTablet", "ScrollBoxes", "LostCoffer", "LeafyPoultice", "NewLeaf" })
                {
                    Hit found = null;
                    for (ulong i = 0; i < 4096 && found == null; i++)
                    {
                        string seed = SeedUtil.At(i, 20261008);
                        var (a, b, c) = NeowGen.Generate(seed);
                        int choice = a == relic ? 0 : b == relic ? 1 : c == relic ? 2 : -1;
                        if (choice < 0) continue;
                        var detailConfig = new SearchConfig { character = "Silent", neowChoice = choice, minFightsChecked = 1,
                            checkFromFirstUnknown = false, maxSearchFloor = 10, findUntilHit = true, allowedCards = Game.FilterableCards("Silent") };
                        found = RunPlan.Evaluate(seed, detailConfig);
                        if (found != null)
                        {
                            form.ApplyConfig(detailConfig); form.VerifyNeowDetails(found);
                            if (relic == "HeftyTablet") { preview = found; previewConfig = detailConfig; }
                        }
                    }
                    if (found == null) throw new Exception("缺少捏奥详情自检样本：" + relic);
                }
                if (args.Length > 1)
                {
                    previewConfig.allowedCards = preview.neow.FilteredCards.Concat(preview.fights.SelectMany(f => f.cards)).Distinct().ToArray();
                    previewConfig.normalStrongPrefixLength = 2;
                    previewConfig.normalStrongAllow = new[] { "CorpseSlugsNormal", "CultistsNormal", "FossilStalkerNormal", "GremlinMercNormal", "HauntedShipNormal" };
                    previewConfig.firstEliteAllow = new[] { "PhantasmalGardenersElite" };
                    previewConfig.eliteBlock = true; previewConfig.noRestBeforeFirstElite = true;
                    form.ApplyConfig(previewConfig); form.VerifyNeowDetails(preview);
                    form.PreviewEncounters(); form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.GetFullPath(args[1]), ImageFormat.Png);
                    form.Size = form.MinimumSize; form.PreviewEncounters(); Application.DoEvents();
                    using var small = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(small, new Rectangle(Point.Empty, form.Size));
                    string smallPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), Path.GetFileNameWithoutExtension(args[1]) + "-small.png");
                    small.Save(smallPath, ImageFormat.Png);
                }
                Console.WriteLine("PASS: GUI filtering, source groups, current-character shortcut, foreign/colorless selections, character memory, encounter controls/config roundtrip, six Neow details/fixed curse/transforms/following cards, 2000-hit delivery/1000 display cap, first-hit auto-stop and controls.");
                return 0;

                static void Complete(Task task)
                {
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10)) Application.DoEvents();
                    if (!task.IsCompleted) throw new Exception("GUI 搜索回调超时。");
                    task.GetAwaiter().GetResult();
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Application.Run(new MainForm());
        return 0;
    }
}
