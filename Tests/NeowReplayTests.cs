using System.Text.Json;
using SeedSearch;

static class NeowReplayTests
{
    public static void Run(string fixturePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        int cases = 0, fights = 0;
        var clawCounts = new HashSet<long>();
        foreach (var fixture in document.RootElement.EnumerateArray())
        {
            string seed = fixture.GetProperty("seed").GetString();
            string character = fixture.GetProperty("character").GetString();
            string relic = fixture.GetProperty("relic").GetString();
            int ascension = fixture.GetProperty("ascension").GetInt32();
            string label = $"{seed}/{character}/A{ascension}/{relic}";
            var replay = new RewardsReplay(seed, character, ascension);
            var (_, bag) = QueueGen.GenerateAct1(seed, Game.Act("Overgrowth"), character);
            Counters(replay, fixture.GetProperty("before"), label + " before");
            var expected = fixture.GetProperty("effect").Deserialize<NeowEffectReport>(SearchConfig.JsonOptions);
            var effect = replay.ApplyNeowEffect(relic, bag);
            Check(effect.cards.SequenceEqual(expected.cards), label + " opening cards", JsonSerializer.Serialize(effect.cards));
            Check(effect.potions.SequenceEqual(expected.potions), label + " opening potions");
            Check(effect.exact, label + " exact replay");
            Counters(replay, fixture.GetProperty("after"), label + " after");
            if (character == "Defect" && relic == "ScrollBoxes") clawCounts.Add(replay.Rew.Counter);
            Check(!effect.FilteredCards.Contains("Injury"), label + " fixed curse must not be filtered");
            if (relic == "HeftyTablet") Check(effect.FilteredCards.Count() == 3 && effect.cards.Length == 4, label + " all rare choices");
            if (relic == "ScrollBoxes") Check(effect.FilteredCards.Count() == 6, label + " both bundles");
            var clone = replay.DeepClone();
            foreach (var fight in fixture.GetProperty("fights").EnumerateArray())
            {
                bool elite = fight.GetProperty("elite").GetBoolean();
                var result = replay.Fight(elite, 0);
                var cloneResult = clone.Fight(elite, 0);
                string[] expectedCards = fight.GetProperty("cards").EnumerateArray().Select(c => c.GetString()).ToArray();
                Check(result.Cards.Select(c => c.Name).SequenceEqual(expectedCards), label + $" fight {fights % 12 + 1} cards", string.Join(",", result.Cards.Select(c => c.Name)));
                Check(result.Gold == fight.GetProperty("gold").GetInt32(), label + " fight gold");
                Check((result.Potion ?? "") == fight.GetProperty("potion").GetString(), label + " fight potion");
                Check((result.EliteRelic ?? "") == fight.GetProperty("eliteRelic").GetString(), label + " elite rarity");
                Counters(replay, fight.GetProperty("counters"), label + " fight counters");
                Check(JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }) ==
                    JsonSerializer.Serialize(cloneResult, new JsonSerializerOptions { IncludeFields = true }), label + " independent clone");
                Counters(clone, fight.GetProperty("counters"), label + " clone counters");
                fights++;
            }
            cases++;
        }
        Check(cases == 83 && fights == 996, "complete real-engine fixture coverage");
        Check(clawCounts.SetEquals(new long[] { 2, 5, 8 }), "Defect normal/one-Claw/two-Claw RNG consumption");
        Console.WriteLine($"PASS: {cases} real-engine Neow fixtures, {fights} following rewards, independent clones, all random choices and RNG counters.");
    }

    static void Counters(RewardsReplay replay, JsonElement expected, string label)
    {
        Check(replay.Rew.Counter == expected.GetProperty("rewards").GetInt64(), label + " rewards", replay.Rew.Counter.ToString());
        Check(replay.Transf.Counter == expected.GetProperty("transformations").GetInt64(), label + " transformations");
        Check(replay.Niche.Counter == expected.GetProperty("niche").GetInt64(), label + " niche");
    }
    static void Check(bool success, string label, string detail = "")
    {
        if (!success) throw new Exception($"Neow replay mismatch: {label}. {detail}");
    }
}
