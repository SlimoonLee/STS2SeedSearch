using SeedSearch;

static class RelicFilterTests
{
    public static void Run()
    {
        string seed = null;
        int choice = -1;
        for (ulong i = 0; i < 16384 && seed == null; i++)
        {
            string candidate = SeedUtil.At(i, 20261008);
            var (a, b, c) = NeowGen.Generate(candidate);
            choice = a == "SmallCapsule" ? 0 : b == "SmallCapsule" ? 1 : c == "SmallCapsule" ? 2 : -1;
            if (choice >= 0 && ActSelection.Pick(candidate, false, false) == "Overgrowth") seed = candidate;
        }
        Check(seed != null, "Could not find a SmallCapsule sample");

        var all = RelicBag.SequenceRelicIds("Silent");
        var broad = new SearchConfig { character = "Silent", underdocksUnlocked = false, neowChoice = choice,
            minFightsChecked = 0, maxSearchFloor = 1, routeMode = "exists", checkFromFirstUnknown = false,
            allowedRelics = all };
        var hit = RunPlan.Evaluate(seed, broad);
        Check(hit != null && hit.firstEliteRelic?.Length > 0 && hit.neow?.relics is { Length: 1 }, "SmallCapsule/first elite sequence was not replayed");
        Check(!hit.neow.relics.Contains("SmallCapsule"), "Neow option itself entered the sequence report");

        var exact = hit.neow.relics.Concat(new[] { hit.firstEliteRelic }).Distinct(StringComparer.Ordinal).ToArray();
        broad.allowedRelics = exact;
        var accepted = RunPlan.Evaluate(seed, broad);
        Check(accepted != null && accepted.firstEliteRelic == hit.firstEliteRelic
            && accepted.neow.relics.SequenceEqual(hit.neow.relics), "Combined Neow-effect/first-elite whitelist rejected its own sequence");
        broad.allowedRelics = exact.Where(r => r != hit.neow.relics[0]).ToArray();
        Check(RunPlan.Evaluate(seed, broad) == null, "SmallCapsule relic was not checked");
        broad.allowedRelics = exact.Where(r => r != hit.firstEliteRelic).ToArray();
        Check(RunPlan.Evaluate(seed, broad) == null, "First elite relic was not checked");

        Invalid(new SearchConfig { allowedRelics = new[] { "SmallCapsule" } });
        Invalid(new SearchConfig { allowedRelics = new[] { "NeowsBones" } });

        var bonesBag = new RelicBag("Silent");
        var freshBag = new RelicBag("Silent");
        var bonesReplay = new RewardsReplay(seed, "Silent", 10);
        var bones = bonesReplay.ApplyNeowEffect("NeowsBones", bonesBag);
        Check(bones.relics == null && bonesBag.Pull("Common") == freshBag.Pull("Common"), "NeowsBones incorrectly consumed the relic sequence");
        Console.WriteLine("PASS: relic sequence whitelist checks Neow bag effects and first elite, excludes Neow option relics, and keeps NeowsBones outside the bag sequence.");
    }

    static void Invalid(SearchConfig config)
    {
        try { config.ValidatedCopy(); }
        catch (ArgumentException) { return; }
        throw new Exception("Relic sequence accepted a Neow-only relic");
    }

    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("Relic filter failed: " + message);
    }
}
