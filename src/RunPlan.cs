using System.Text.Json.Serialization;

namespace SeedSearch;

public sealed record Hit(string seed, string act1, string boss, string ancient,
    string neowA, string neowB, string neowC, string[] route, string[] unknownRolls,
    FightReport[] fights, int checkedFights, string[] treasures,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeowEffectReport neow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] normalQueue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] eliteQueue = null);
public sealed record FightReport(string encounter, int gold, string potion, string[] cards, string eliteRelic);

public static class RunPlan
{
    public static string Norm(string card) => GameData.CardRarity.Keys.FirstOrDefault(c =>
        c.Equals(card, StringComparison.OrdinalIgnoreCase) || Game.Snake(c).Equals(card, StringComparison.OrdinalIgnoreCase)) ?? card;
    public static Hit Evaluate(string seed, SearchConfig cfg) => new SeedEvaluator(cfg).Evaluate(seed);
}

/// <summary>一次编译过滤条件，供所有工作线程共享；每颗种子的路线状态独立。</summary>
public sealed class SeedEvaluator
{
    readonly SearchConfig _cfg;
    readonly HashSet<string> _positives, _curses, _bosses, _normalStrong, _firstElite, _allowed;
    public SeedEvaluator(SearchConfig cfg)
    {
        _cfg = cfg.ValidatedCopy();
        _positives = new(_cfg.neowPositives, StringComparer.Ordinal);
        _curses = new(_cfg.neowCurses, StringComparer.Ordinal);
        _bosses = new(_cfg.bossAllow, StringComparer.Ordinal);
        _normalStrong = new(_cfg.normalStrongAllow, StringComparer.Ordinal);
        _firstElite = new(_cfg.firstEliteAllow, StringComparer.Ordinal);
        _allowed = new(_cfg.allowedCards, StringComparer.Ordinal);
    }

    public Hit Evaluate(string seed, SearchCounters stats = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        seed = SeedUtil.Canonicalize(seed);
        if (string.IsNullOrWhiteSpace(seed)) throw new ArgumentException("种子不能为空。");
        ulong hash = Rng.HashString(seed);
        return EvaluateCore(seed, hash, stats, ct);
    }

    /// <summary>搜索全空间时直接传入哈希，避免为每个未命中候选创建种子字符串。</summary>
    public Hit EvaluateHash(ulong hash, SearchCounters stats = null, CancellationToken ct = default)
        => EvaluateCore(null, hash, stats, ct);

    Hit EvaluateCore(string seed, ulong hash, SearchCounters stats, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stats != null) stats.Total++;
        string a = null, b = null, c = null;
        bool needNeow = _positives.Count > 0 || _curses.Count > 0 || _cfg.neowChoice >= 0;
        if (needNeow)
        {
            (a, b, c) = NeowGen.Generate(hash);
            if (_positives.Count > 0 && (!_positives.Contains(a) || !_positives.Contains(b))
                || _curses.Count > 0 && !_curses.Contains(c)) return null;
        }
        if (stats != null) stats.PassNeow++;
        string actName = ActSelection.Pick(hash, _cfg.underdocksUnlocked, _cfg.underdocksDiscovered);
        if (_cfg.act1.Length > 0 && _cfg.act1 != actName) return null;
        if (stats != null) stats.PassAct++;
        var act = Game.Act(actName);
        // 先用与完整队列相同的 RNG 计算摘要。只要条件未通过，就不创建完整遭遇列表，
        // 也不进入奖励/地图重放；完整队列只在奖励预筛通过后为命中候选生成。
        bool queueFiltered = _bosses.Count > 0 || _normalStrong.Count > 0 || _firstElite.Count > 0;
        bool needsMap = _cfg.minFightsChecked > 0 || _cfg.unknownMonsterStreak > 0 || _cfg.eliteBlock || _cfg.noRestBeforeFirstElite;
        int guaranteed = _cfg.routeMode == "all" ? Math.Min(5, _cfg.minFightsChecked)
            : !_cfg.checkFromFirstUnknown && _cfg.minFightsChecked > 0 ? 1 : 0;
        bool deferQueueStages = !queueFiltered && _allowed.Count > 0 && guaranteed > 0;
        QueueGen.QueueSummary summary = null;
        ActRooms rooms = null;
        RelicBag bag = null;
        QueueGen.ActPreparation preparation = null;
        if (queueFiltered || _cfg.neowChoice >= 0)
        {
            preparation = QueueGen.PrepareAct1(hash, _cfg.character, includeBag: _cfg.neowChoice >= 0);
            bag = preparation.Bag;
            if (queueFiltered)
            {
                summary = QueueGen.GenerateSummary(act, preparation.QueueRng.Clone(),
                    _normalStrong.Count > 0 ? _cfg.normalStrongPrefixLength : 0, _firstElite.Count > 0);
                if (_bosses.Count > 0 && !_bosses.Contains(Game.Snake(summary.Boss))) return null;
                if (stats != null) stats.PassBoss++;
                if (!EncounterFilters.QueueMatches(act, summary, _cfg)) return null;
                if (stats != null) stats.PassEncounter++;
            }
        }
        if (!queueFiltered && !deferQueueStages && stats != null)
        {
            // 无队列条件时这些阶段必然通过；保持旧版计数语义，同时延后实际队列对象。
            stats.PassBoss++;
            stats.PassEncounter++;
        }

        // 捏奥只执行一次。额外卡牌独立于战斗数 X，固定诅咒不参与白名单。
        RewardsReplay initialRewards = _cfg.minFightsChecked > 0 || _cfg.neowChoice >= 0
            ? new RewardsReplay(hash, _cfg.character, _cfg.ascension) : null;
        NeowEffectReport neow = _cfg.neowChoice < 0 ? null
            : initialRewards.ApplyNeowEffect(_cfg.neowChoice switch { 0 => a, 1 => b, _ => c }, bag);
        if (_allowed.Count > 0 && neow != null && (!neow.exact || neow.FilteredCards.Any(card => !_allowed.Contains(card)))) return null;

        if (_allowed.Count > 0 && guaranteed > 0)
        {
            var rewards = initialRewards.DeepClone();
            for (int i = 0; i < guaranteed; i++)
                if (!rewards.FightCardsAllowed(false, 0, _allowed)) return null;
        }

        if (stats != null) stats.PassRewards++;

        // 地图或命中详情需要完整队列；此时才支付完整队列/遗物袋的成本。
        bool needsFullRooms = needsMap || _normalStrong.Count > 0 || _firstElite.Count > 0 || _cfg.eliteBlock;
        if (needsFullRooms)
        {
            if (preparation?.Bag != null)
            {
                rooms = QueueGen.GenerateRooms(act, preparation.QueueRng);
                bag = preparation.Bag;
            }
            else
            {
                (rooms, bag) = QueueGen.GenerateAct1(hash, act, _cfg.character);
            }
            if (deferQueueStages && stats != null) { stats.PassBoss++; stats.PassEncounter++; }
        }
        else if (summary == null)
        {
            // 没有路线/队列条件时，Boss/先古只在奖励通过后计算；这一步同样是摘要路径。
            preparation ??= QueueGen.PrepareAct1(hash, _cfg.character, includeBag: false);
            summary = QueueGen.GenerateSummary(act, preparation.QueueRng.Clone());
            if (deferQueueStages && stats != null) { stats.PassBoss++; stats.PassEncounter++; }
        }
        else if (bag == null && preparation?.Bag != null)
        {
            bag = preparation.Bag;
        }
        Hit hit;
        if (!needsMap) hit = MakeHit(null);
        else
        {
            var map = new ActMap(act, hash, false, false, false, _cfg.ascension, applyLayout: _cfg.mapMode == "full");
            if (_cfg.eliteBlock && map.HasPathToBossAvoiding(MapPointType.Elite)) return null;
            var initial = new WalkState
            {
                Rewards = initialRewards ?? new RewardsReplay(hash, _cfg.character, _cfg.ascension), Bag = bag, Odds = new UnknownOdds(hash),
                Checking = _cfg.routeMode == "all" || !_cfg.checkFromFirstUnknown
            };
            WalkState sample = null;
            bool all = _cfg.routeMode == "all";
            foreach (var first in map.StartingMapPoint.Children)
            {
                bool pass = Visit(first, initial.DeepClone(), all, ref sample);
                if (all && !pass) return null;
                if (!all && pass) break;
            }
            if (sample == null) return null;
            map.CompleteLayout();
            hit = MakeHit(sample);

            bool Visit(MapPoint node, WalkState state, bool requireAll, ref WalkState firstPass)
            {
                ct.ThrowIfCancellationRequested();
                state.Depth++;
                state.Route.Add(node);

                // The cutoff applies to rewards, unknown rolls, and the first-X
                // route conditions.  A no-rest-before-first-elite check may
                // continue past it only to inspect static map nodes until the
                // first elite (or Boss); it must not expand the whitelist scope.
                if (state.Depth > _cfg.maxSearchFloor)
                {
                    if (!_cfg.noRestBeforeFirstElite || state.EliteSeen
                        || state.Checked < _cfg.minFightsChecked
                        || state.Unknowns < _cfg.unknownMonsterStreak)
                        return false;
                    if (node.PointType == MapPointType.RestSite) return false;
                    if (node.PointType == MapPointType.Elite) state.EliteSeen = true;
                    if (state.EliteSeen || ReferenceEquals(node, map.BossMapPoint))
                    {
                        firstPass ??= state;
                        return true;
                    }
                    if (node.Children.Count == 0) return false;
                    bool postFloorAny = false;
                    foreach (var child in node.Children)
                    {
                        bool pass = Visit(child, state.DeepClone(), requireAll, ref firstPass);
                        if (requireAll && !pass) return false;
                        if (!requireAll && pass) return true;
                        postFloorAny |= pass;
                    }
                    return postFloorAny;
                }

                string type = node.PointType.ToString();
                if (node.PointType == MapPointType.Unknown)
                {
                    if (_cfg.checkFromFirstUnknown) state.Checking = true;
                    type = state.Odds.Roll();
                    state.Unknowns++;
                    state.Rolls.Add(type);
                    if (state.Unknowns <= _cfg.unknownMonsterStreak && type != "Monster") return false;
                }
                if (_cfg.noRestBeforeFirstElite && node.PointType == MapPointType.RestSite && !state.EliteSeen) return false;
                if (node.PointType == MapPointType.Elite) state.EliteSeen = true;
                bool fight = type is "Monster" or "Elite";
                if (requireAll && state.Depth <= _cfg.minFightsChecked && !fight) return false;
                bool stillChecking = state.Checked < _cfg.minFightsChecked;
                if (fight && stillChecking)
                {
                    if (state.Truncated) return false;
                    bool elite = type == "Elite";
                    var reward = state.Rewards.Fight(elite, 0);
                    if (state.Checking)
                    {
                        if (!CardsAllowed(reward)) return false;
                        state.Checked++;
                    }
                    string encounter = elite ? rooms.Elite[state.Elite++ % rooms.Elite.Count]
                        : rooms.Normal[state.Normal++ % rooms.Normal.Count];
                    state.Fights.Add(new FightReport(Game.Snake(encounter), reward.Gold, reward.Potion ?? "",
                        reward.Cards.Select(p => p.Name).ToArray(), reward.EliteRelic == null ? "" : state.Bag.Pull(reward.EliteRelic) ?? "(袋空)"));
                }
                else if (type == "Treasure" && stillChecking && !state.Truncated)
                {
                    var treasure = state.Rewards.Treasure();
                    state.Treasures.Add($"{treasure.gold}+{state.Bag.Pull(treasure.relicRarity)}");
                }
                else if (type is "Shop" or "Event") state.Truncated = true;
                bool eliteConditionDone = !_cfg.noRestBeforeFirstElite || state.EliteSeen || ReferenceEquals(node, map.BossMapPoint);
                if (state.Checked >= _cfg.minFightsChecked && state.Unknowns >= _cfg.unknownMonsterStreak && eliteConditionDone)
                {
                    firstPass ??= state;
                    return true;
                }
                bool mustContinueToFirstElite = _cfg.noRestBeforeFirstElite && !state.EliteSeen;
                if (state.Truncated && state.Checked < _cfg.minFightsChecked || node.Children.Count == 0 || state.Depth >= _cfg.maxSearchFloor && !mustContinueToFirstElite) return false;
                bool any = false;
                foreach (var child in node.Children)
                {
                    bool pass = Visit(child, state.DeepClone(), requireAll, ref firstPass);
                    if (requireAll && !pass) return false;
                    if (!requireAll && pass) return true;
                    any |= pass;
                }
                return any;
            }
        }
        if (stats != null) stats.PassAll++;
        return hit;

        Hit MakeHit(WalkState state)
        {
            if (!needNeow) (a, b, c) = NeowGen.Generate(hash);
            return new(seed, actName, Game.Snake(rooms?.Boss ?? summary?.Boss ?? ""),
            rooms?.Ancient ?? summary?.Ancient ?? "", a, b, c,
            state?.Route.Select(node => $"{node.coord.row},{node.coord.col}:{node.PointType}").ToArray() ?? [], state?.Rolls.ToArray() ?? [], state?.Fights.ToArray() ?? [],
            state?.Checked ?? 0, state?.Treasures.ToArray() ?? [], neow,
            _normalStrong.Count > 0 || _firstElite.Count > 0 || _cfg.eliteBlock ? rooms?.Normal.ToArray() : null,
            _normalStrong.Count > 0 || _firstElite.Count > 0 || _cfg.eliteBlock ? rooms?.Elite.ToArray() : null);
        }
    }

    bool CardsAllowed(RewardsReplay.FightReward reward) => _allowed.Count == 0 || reward.Cards.All(c => _allowed.Contains(c.Name));

    sealed class WalkState
    {
        public RewardsReplay Rewards;
        public RelicBag Bag;
        public UnknownOdds Odds;
        public List<MapPoint> Route = [];
        public List<string> Rolls = [], Treasures = [];
        public List<FightReport> Fights = [];
        public int Depth, Unknowns, Checked, Normal, Elite;
        public bool Checking, Truncated, EliteSeen;
        public WalkState DeepClone() => new()
        {
            Rewards = Rewards.DeepClone(), Bag = Bag.DeepClone(), Odds = Odds.DeepClone(),
            Route = new(Route), Rolls = new(Rolls), Treasures = new(Treasures), Fights = new(Fights),
            Depth = Depth, Unknowns = Unknowns, Checked = Checked, Normal = Normal, Elite = Elite,
            Checking = Checking, Truncated = Truncated, EliteSeen = EliteSeen
        };
    }
}
