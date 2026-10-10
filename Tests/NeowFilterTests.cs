using System.Text.Json;
using SeedSearch;

static class NeowFilterTests
{
    public static void Run()
    {
        int comparisons = 0;
        Hit sample = null;
        SearchConfig sampleConfig = null;
        string[] relics = ["ArcaneScroll", "HeftyTablet", "ScrollBoxes", "LostCoffer", "LeafyPoultice", "NewLeaf", "LeadPaperweight", "Kaleidoscope"];
        string[] characters = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"];
        for (int r = 0; r < relics.Length; r++)
        {
            string relic = relics[r], character = characters[r % characters.Length];
            var (seed, choice, opening, firstFight) = Find(relic, character);
            var cards = opening.FilteredCards.Concat(firstFight.Cards.Select(c => c.Name)).Distinct().ToArray();
            var cfg = new SearchConfig { character = character, ascension = 10, underdocksUnlocked = false,
                neowChoice = choice, minFightsChecked = 1, maxSearchFloor = 10, checkFromFirstUnknown = false, allowedCards = cards };
            foreach (string mode in new[] { "all", "exists" })
                foreach (string map in new[] { "full", "search" })
                {
                    cfg.routeMode = mode; cfg.mapMode = map;
                    var hit = RunPlan.Evaluate(seed, cfg);
                    Check(hit != null && hit.checkedFights == 1, relic + " bonus and first fight accepted");
                    Check(JsonSerializer.Serialize(hit.neow) == JsonSerializer.Serialize(opening), relic + " selected effect once");
                    Check(hit.fights[0].cards.SequenceEqual(firstFight.Cards.Select(c => c.Name)), relic + " following fight sequence");
                    Check(hit.fights[0].gold == firstFight.Gold && hit.fights[0].potion == (firstFight.Potion ?? ""), relic + " following fight rewards");
                    Check(ReferenceRoutes.Evaluate(seed, cfg), relic + " exhaustive route oracle");
                    sample = hit; comparisons++;
                }
            string extraOnly = opening.FilteredCards.First(c => !firstFight.Cards.Any(f => f.Name == c));
            cfg.allowedCards = cards.Where(c => c != extraOnly).ToArray();
            Check(RunPlan.Evaluate(seed, cfg) == null && !ReferenceRoutes.Evaluate(seed, cfg), relic + " missing random candidate rejected");
            cfg.minFightsChecked = 0;
            Check(RunPlan.Evaluate(seed, cfg) == null, relic + " X=0 still filters random candidates");
            cfg.allowedCards = opening.FilteredCards.Distinct().ToArray();
            var openingHit = RunPlan.Evaluate(seed, cfg);
            Check(openingHit != null && openingHit.route.Length == 0 && openingHit.fights.Length == 0, relic + " opening-only skips map");
            Check(!cfg.allowedCards.Contains("Injury"), relic + " fixed curse excluded from whitelist");
            if (relic == "HeftyTablet") Check(openingHit.neow.cards.Any(c => c.card == "Injury" && c.kind == "fixedCurse"), "fixed curse still reported");
            cfg.minFightsChecked = 1;
            string laterOnly = firstFight.Cards.First(c => !opening.FilteredCards.Contains(c.Name)).Name;
            cfg.allowedCards = cards.Where(c => c != laterOnly).ToArray();
            Check(RunPlan.Evaluate(seed, cfg) == null, relic + " subsequent card outside whitelist rejected");
            cfg.allowedCards = cards; sampleConfig = cfg.ValidatedCopy();

            cfg.neowChoice = -1;
            var unchanged = new RewardsReplay(seed, character, 10).Fight(false, 0);
            cfg.allowedCards = unchanged.Cards.Select(c => c.Name).ToArray();
            var noEffect = RunPlan.Evaluate(seed, cfg);
            Check(noEffect != null && noEffect.neow == null && noEffect.fights[0].cards.SequenceEqual(cfg.allowedCards), relic + " unapplied effect preserves original sequence");
        }

        // The added report and foreign/colorless whitelist must survive the actual file execution path.
        string directory = Path.Combine(Path.GetTempPath(), "seedsearch-neow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "seeds.txt");
            File.WriteAllText(input, sample.seed + "\n" + sample.seed + "\n");
            sampleConfig.seedListFile = input; sampleConfig.count = 1;
            foreach (int threads in new[] { 1, 4 })
            {
                sampleConfig.threads = threads;
                foreach (bool until in new[] { false, true })
                {
                    sampleConfig.findUntilHit = until; sampleConfig.outFile = Path.Combine(directory, $"hits-{threads}-{until}.jsonl");
                    int callbacks = 0;
                    var result = SearchEngine.Run(sampleConfig, _ => Interlocked.Increment(ref callbacks));
                    var hits = File.ReadLines(sampleConfig.outFile).Select(line => JsonSerializer.Deserialize<Hit>(line)).ToArray();
                    Check(result.Progress.Hits == (until ? 1 : 2) && callbacks == hits.Length && hits.Length == result.Progress.Hits, "bonus search list/threads/until-hit delivery");
                    Check(hits.All(h => h.neow != null && JsonSerializer.Serialize(h.neow) == JsonSerializer.Serialize(sample.neow)), "bonus report JSONL roundtrip");
                }
            }
        }
        finally { Directory.Delete(directory, true); }

        foreach (string relic in new[] { "SmallCapsule", "LargeCapsule", "NeowsBones" })
        {
            var (seed, choice) = FindChoice(relic);
            var cfg = new SearchConfig { neowChoice = choice, minFightsChecked = 0, allowedCards = Game.FilterableCards("Silent") };
            Check(RunPlan.Evaluate(seed, cfg) == null, relic + " unmodeled cascade cannot pass card whitelist");
            cfg.allowedCards = [];
            Check(RunPlan.Evaluate(seed, cfg)?.neow?.exact == false, relic + " report identifies replay boundary");
        }
        try
        {
            new SearchConfig { neowChoice = 2, neowCurses = ["NeowsBones", "LargeCapsule"], allowedCards = ["Backflip"] }.ValidatedCopy();
            throw new Exception("Unmodelled-only curse configuration was accepted");
        }
        catch (ArgumentException) { }
        new SearchConfig { neowChoice = 2, neowCurses = ["NeowsBones", "HeftyTablet"], allowedCards = ["Backflip"] }.ValidatedCopy();
        Console.WriteLine($"PASS: {comparisons} Neow all/exists/full/search integrations; all bonus candidates, fixed curse exemption, X=0, following cards, unapplied effects, list/threads/until-hit/report roundtrip, cascade boundaries.");
    }

    static (string seed, int choice, NeowEffectReport opening, RewardsReplay.FightReward first) Find(string relic, string character)
    {
        for (ulong i = 0; i < 4096; i++)
        {
            string seed = SeedUtil.At(i, 20261008);
            var (a, b, c) = NeowGen.Generate(seed);
            int choice = a == relic ? 0 : b == relic ? 1 : c == relic ? 2 : -1;
            if (choice < 0) continue;
            var replay = new RewardsReplay(seed, character, 10);
            var (_, bag) = QueueGen.GenerateAct1(seed, Game.Act("Overgrowth"), character);
            var opening = replay.ApplyNeowEffect(relic, bag);
            var first = replay.Fight(false, 0);
            if (opening.FilteredCards.Any(card => !first.Cards.Any(f => f.Name == card)) && first.Cards.Any(f => !opening.FilteredCards.Contains(f.Name)))
                return (seed, choice, opening, first);
        }
        throw new Exception("Could not find integration seed for " + relic);
    }
    static (string seed, int choice) FindChoice(string relic)
    {
        for (ulong i = 0; i < 4096; i++)
        {
            string seed = SeedUtil.At(i, 20261008);
            var (a, b, c) = NeowGen.Generate(seed);
            int choice = a == relic ? 0 : b == relic ? 1 : c == relic ? 2 : -1;
            if (choice >= 0) return (seed, choice);
        }
        throw new Exception("Could not find cascade seed for " + relic);
    }
    static void Check(bool condition, string label) { if (!condition) throw new Exception("Neow filter failed: " + label); }
}
