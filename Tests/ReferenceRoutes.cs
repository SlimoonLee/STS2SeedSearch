using SeedSearch;

/// <summary>独立的逐完整路线参考实现，只用于校验优化后的前缀搜索。</summary>
static class ReferenceRoutes
{
    public static bool Evaluate(string seed, SearchConfig config)
    {
        var cfg = config.ValidatedCopy();
        var act = Game.Act(ActSelection.Pick(seed, cfg.underdocksUnlocked, cfg.underdocksDiscovered));
        var map = new ActMap(act, Rng.HashString(seed), false, false, false, cfg.ascension);
        var allowed = cfg.allowedCards.ToHashSet();
        var (a, b, c) = NeowGen.Generate(seed);
        var (rooms, bag) = QueueGen.GenerateAct1(seed, act, cfg.character);
        if (!EncounterFilters.QueueMatches(act, rooms, cfg)) return false;
        if (cfg.eliteBlock && map.HasPathToBossAvoiding(MapPointType.Elite)) return false;
        return cfg.routeMode == "all" ? Paths(map.StartingMapPoint).All(Pass) : Paths(map.StartingMapPoint).Any(Pass);

        bool Pass(List<MapPoint> path)
        {
            var rewards = new RewardsReplay(seed, cfg.character, cfg.ascension);
            if (cfg.neowChoice >= 0)
            {
                var neow = rewards.ApplyNeowEffect(cfg.neowChoice switch { 0 => a, 1 => b, _ => c }, bag.DeepClone());
                if (allowed.Count > 0 && (!neow.exact || neow.FilteredCards.Any(card => !allowed.Contains(card)))) return false;
            }
            var odds = new UnknownOdds(seed);
            int seen = 0, checkedFights = 0, depth = 0;
            bool eliteSeen = false;
            bool checking = cfg.routeMode == "all" || !cfg.checkFromFirstUnknown, truncated = false;
            foreach (var point in path.Skip(1))
            {
                depth++;
                if (depth > cfg.maxSearchFloor)
                {
                    if (!cfg.noRestBeforeFirstElite || eliteSeen
                        || checkedFights < cfg.minFightsChecked || seen < cfg.unknownMonsterStreak)
                        return false;
                    if (point.PointType == MapPointType.RestSite) return false;
                    if (point.PointType == MapPointType.Elite) eliteSeen = true;
                    if (eliteSeen || point.PointType == MapPointType.Boss) return true;
                    continue;
                }
                string type = point.PointType.ToString();
                if (type == "Unknown")
                {
                    if (cfg.checkFromFirstUnknown) checking = true;
                    type = odds.Roll(); seen++;
                    if (seen <= cfg.unknownMonsterStreak && type != "Monster") return false;
                }
                if (cfg.noRestBeforeFirstElite && point.PointType == MapPointType.RestSite && !eliteSeen) return false;
                if (point.PointType == MapPointType.Elite) eliteSeen = true;
                bool fight = type is "Monster" or "Elite";
                if (cfg.routeMode == "all" && depth <= cfg.minFightsChecked && !fight) return false;
                if (fight && checkedFights < cfg.minFightsChecked)
                {
                    if (truncated) return false;
                    var reward = rewards.Fight(type == "Elite", 0);
                    if (checking)
                    {
                        if (allowed.Count > 0 && reward.Cards.Any(card => !allowed.Contains(card.Name))) return false;
                        checkedFights++;
                    }
                }
                else if (type == "Treasure" && !truncated && checkedFights < cfg.minFightsChecked) rewards.Treasure();
                else if (type is "Shop" or "Event") truncated = true;
                if (checkedFights >= cfg.minFightsChecked && seen >= cfg.unknownMonsterStreak
                    && (!cfg.noRestBeforeFirstElite || eliteSeen || point.PointType == MapPointType.Boss)) return true;
            }
            return false;
        }
    }
    static IEnumerable<List<MapPoint>> Paths(MapPoint node)
    {
        if (node.PointType == MapPointType.Boss) { yield return new List<MapPoint> { node }; yield break; }
        foreach (var child in node.Children)
            foreach (var tail in Paths(child))
            { tail.Insert(0, node); yield return tail; }
    }
}
