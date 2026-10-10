using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedSearch;
using Sts2Headless;

class Program
{
    static void Main(string[] args)
    {
        string library = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(library, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var error = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null); // Headless startup logs are verbose; failures retain returned diagnostics.
            Run(Path.GetFullPath(args[1]));
        }
        catch (Exception exception)
        {
            Console.SetError(error); Console.Error.WriteLine(exception); Environment.ExitCode = 1;
        }
        finally { Console.SetError(error); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string output)
    {
        var cases = new List<(string seed, string character, int ascension, string relic)>();
        foreach (string character in new[] { "Ironclad", "Silent", "Defect", "Regent", "Necrobinder" })
            foreach (int ascension in new[] { 0, 10 })
                foreach (string relic in new[] { "ArcaneScroll", "HeftyTablet", "ScrollBoxes", "LostCoffer", "LeafyPoultice", "NewLeaf", "LeadPaperweight", "Kaleidoscope" })
                    cases.Add(("X6MB8T8NLGQ5", character, ascension, relic));
        // Include both variable Claw branches. Use the real engine RNG to find fixtures.
        var seenBranches = new HashSet<string>();
        for (ulong i = 0; i < 8192 && seenBranches.Count < 3; i++)
        {
            string seed = SeedUtil.At(i, 20261008);
            var rng = new MegaCrit.Sts2.Core.Random.Rng(SeedSearch.Rng.HashString(seed), "rewards");
            bool first = rng.NextInt(100) < 1;
            if (!first) for (int n = 0; n < 3; n++) rng.NextDouble();
            bool second = rng.NextInt(100) < 1;
            if (first || second)
            {
                string branch = $"{first}/{second}";
                if (seenBranches.Add(branch)) cases.Add((seed, "Defect", 10, "ScrollBoxes"));
            }
        }
        if (seenBranches.Count < 2) throw new Exception("Could not locate Claw bundle branches");
        var fixtures = new List<object>();
        foreach (var (seed, character, ascension, relic) in cases)
        {
            var sim = new RunSimulator();
            var started = sim.StartRun(character, ascension, seed);
            if (started.TryGetValue("type", out var type) && Equals(type, "error")) throw new Exception(JsonSerializer.Serialize(started));
            var run = (RunState)typeof(RunSimulator).GetField("_runState", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sim);
            Player player = run.Players[0];
            var before = Counters(player);
            var effect = Effect(player, relic);
            var after = Counters(player);
            var fights = new List<object>();
            for (int fight = 0; fight < 12; fight++)
            {
                bool elite = fight % 3 == 2;
                RoomType roomType = elite ? RoomType.Elite : RoomType.Monster;
                bool potion = player.PlayerOdds.PotionReward.Roll(player, roomType);
                var gold = new GoldReward(elite ? 35 : 10, elite ? 45 : 20, player); gold.Populate();
                var potionReward = potion ? new PotionReward(player) : null; potionReward?.Populate();
                var cards = new CardReward(CardCreationOptions.ForRoom(player, roomType), 3, player); cards.Populate();
                var reward = elite ? new RelicReward(player) : null; reward?.Populate();
                fights.Add(new { elite, gold = gold.Amount, potion = potionReward?.Potion?.GetType().Name ?? "",
                    cards = cards.Cards.Select(c => c.GetType().Name).ToArray(), eliteRelic = reward?.Relic?.Rarity.ToString() ?? "",
                    counters = Counters(player) });
            }
            fixtures.Add(new { seed, character, ascension, relic, before, effect, after, fights });
            sim.CleanUp();
            // RunSimulator discards its selector scope; Reset intentionally skips TestMode.
            // Remove that one selector before the next isolated fixture run.
            MegaCrit.Sts2.Core.Commands.CardSelectCmd.SuspendSelectorForTest();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonSerializer.Serialize(fixtures, SearchConfig.JsonOptions));
        Console.WriteLine($"Saved {fixtures.Count} real-engine fixtures: 5 characters, A0/A10, 8 relics, variable Claw bundles, 12 following fights and RNG counters.");
    }

    static object Counters(Player player) => new {
        rewards = player.PlayerRng.Rewards.ToSerializable().counter,
        transformations = player.PlayerRng.Transformations.ToSerializable().counter,
        niche = player.RunState.Rng.Niche.ToSerializable().counter };

    static NeowEffectReport Effect(Player player, string relic)
    {
        var cards = new List<NeowCardReport>();
        var potions = new List<string>();
        var pool = new[] { player.Character.CardPool };
        switch (relic)
        {
            case "ArcaneScroll":
            case "HeftyTablet":
                var rare = new CardCreationOptions(pool, CardCreationSource.Other, CardRarityOddsType.Uniform, c => c.Rarity == CardRarity.Rare).WithFlags(CardCreationFlags.NoUpgradeRoll);
                Add(CardFactory.CreateForReward(player, relic == "ArcaneScroll" ? 1 : 3, rare).Select(c => c.Card), relic == "ArcaneScroll" ? "added" : "offer", relic == "ArcaneScroll" ? 0 : 1);
                if (relic == "HeftyTablet") cards.Add(new("Injury", "fixedCurse"));
                break;
            case "ScrollBoxes":
                int bundle = 0;
                foreach (var generated in ScrollBoxes.GenerateRandomBundles(player)) Add(generated, "bundle", ++bundle);
                break;
            case "LostCoffer":
                var offer = new CardReward(new CardCreationOptions(pool, CardCreationSource.Other, CardRarityOddsType.RegularEncounter), 3, player); offer.Populate();
                Add(offer.Cards, "offer", 1);
                var potion = new PotionReward(player); potion.Populate(); potions.Add(potion.Potion.GetType().Name);
                break;
            case "LeafyPoultice":
                var basics = player.Deck.Cards.Where(c => c.Rarity == CardRarity.Basic).ToArray();
                foreach (var original in new[] { basics.First(c => c.Tags.Contains(CardTag.Strike)), basics.First(c => c.Tags.Contains(CardTag.Defend)) })
                    cards.Add(new(CardFactory.CreateRandomCardForTransform(original, false, player.PlayerRng.Transformations).GetType().Name, "transform", 0, original.GetType().Name));
                break;
            case "NewLeaf":
                var starter = player.Deck.Cards.First(c => c.IsTransformable);
                cards.Add(new(CardFactory.CreateRandomCardForTransform(starter, false, player.RunState.Rng.Niche).GetType().Name, "transform", 0, "StartingBasic"));
                break;
            case "LeadPaperweight":
                Add(CardFactory.CreateForReward(player, 2, new CardCreationOptions(new[] { ModelDb.CardPool<ColorlessCardPool>() }, CardCreationSource.Other, CardRarityOddsType.RegularEncounter)).Select(c => c.Card), "offer", 1);
                break;
            case "Kaleidoscope":
                for (int group = 1; group <= 2; group++)
                    foreach (var foreign in player.UnlockState.CharacterCardPools.Where(p => p != player.Character.CardPool).ToList().StableShuffle(player.RunState.Rng.Niche).Take(3))
                        Add(CardFactory.CreateForReward(player, 1, new CardCreationOptions(new[] { foreign }, CardCreationSource.Other, CardRarityOddsType.RegularEncounter).WithFlags(CardCreationFlags.NoCardPoolModifications)).Select(c => c.Card), "offer", group);
                break;
        }
        return new(relic, cards.ToArray(), potions.ToArray());
        void Add(IEnumerable<CardModel> generated, string kind, int group)
        {
            foreach (var card in generated) cards.Add(new(card.GetType().Name, kind, group));
        }
    }
}
