namespace SeedSearch;

/// <summary>
/// PlayerRng.Rewards 流重放：战斗奖励（药水判定→金币→药水→3卡→精英遗物）、宝箱。
/// pity 以 float32 语义累积。假设：全解锁卡池、无遗物 hook、单人 slot0。
/// </summary>
public sealed partial class RewardsReplay
{
    Rng _rew;
    Rng _trr;
    Rng _niche;
    Rng _transf;
    Rng _potGen;
    readonly string[] _cardPool;          // 声明顺序
    readonly Dictionary<string, string[]> _cardsByRarity;
    readonly Dictionary<string, string[]> _potionsByRarity;
    readonly string _character;
    readonly float _regularRare, _eliteRare, _rarityGrowth;
    static readonly ulong RewardsHash = Rng.HashString("rewards"), TreasureHash = Rng.HashString("treasure_room_relics"),
        NicheHash = Rng.HashString("niche"), TransformHash = Rng.HashString("transformations"), PotionHash = Rng.HashString("combat_potion_generation");
    sealed record Pools(string[] Cards, Dictionary<string, string[]> CardsByRarity, Dictionary<string, string[]> PotionsByRarity);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Pools> PoolCache = new(StringComparer.Ordinal);
    float _cardPity = -0.05f;             // CardRarityOdds 初始
    float _potionPity = 0.4f;             // PotionRewardOdds 初始

    static float F32(float f) => f; // C# float 本来就是 float32

    public RewardsReplay(string seed, string character, int ascension = 0) : this(Rng.HashString(seed), character, ascension) { }

    public RewardsReplay(ulong seedHash, string character, int ascension = 0)
    {
        _character = character;
        _regularRare = ascension >= 7 ? 0.0149f : 0.03f;
        _eliteRare = ascension >= 7 ? 0.05f : 0.10f;
        _rarityGrowth = ascension >= 7 ? 0.005f : 0.01f;
        _rew = new Rng(seedHash + RewardsHash);
        _trr = new Rng(seedHash + TreasureHash);
        _niche = new Rng(seedHash + NicheHash);
        _transf = new Rng(seedHash + TransformHash);
        _potGen = new Rng(seedHash + PotionHash);
        var data = PoolCache.GetOrAdd(character, static character =>
        {
            var cards = GameData.CardPools[character + "CardPool"]
                .Where(c => !GameData.CardMultiplayerOnly.Contains(c)).ToArray();
            var potions = new[] { character + "PotionPool", "SharedPotionPool" }
                .Where(GameData.PotionPools.ContainsKey).SelectMany(p => GameData.PotionPools[p]);
            return new Pools(cards, cards.GroupBy(c => GameData.CardRarity[c]).ToDictionary(g => g.Key, g => g.ToArray()),
                potions.GroupBy(p => GameData.PotionRarity[p]).ToDictionary(g => g.Key, g => g.ToArray()));
        });
        _cardPool = data.Cards;
        _cardsByRarity = data.CardsByRarity;
        _potionsByRarity = data.PotionsByRarity;
    }

    public Rng Rew => _rew;
    public Rng Niche => _niche;
    public Rng Transf => _transf;
    public Rng PotGen => _potGen;

    /// <summary>深拷贝（Rng 状态逐字复制），供"每条路线独立重放"使用。</summary>
    public RewardsReplay DeepClone()
    {
        var copy = (RewardsReplay)MemberwiseClone();
        copy._rew = _rew.Clone();
        copy._trr = _trr.Clone();
        copy._niche = _niche.Clone();
        copy._transf = _transf.Clone();
        copy._potGen = _potGen.Clone();
        return copy;
    }

    public sealed class CardPick
    {
        public string Name; public string Rarity; public bool Upgraded;
        public override string ToString() => Upgraded ? $"{Name}+" : Name;
    }

    public sealed class FightReward
    {
        public int Gold;
        public string Potion;
        public List<CardPick> Cards = new();
        public string EliteRelic;
    }

    public FightReward Fight(bool elite, int actIndex)
    {
        return FightCore(elite, actIndex, null, out _);
    }

    /// <summary>
    /// 重放战斗奖励，并在第一张不在白名单的卡牌出现时立即结束。
    /// 失败分支的 RNG 状态随后会被丢弃；全部通过时与 Fight 完全消耗相同的随机数。
    /// </summary>
    public bool FightCardsAllowed(bool elite, int actIndex, ISet<string> allowed)
    {
        if (allowed == null || allowed.Count == 0) return true;
        _ = FightCore(elite, actIndex, allowed, out bool passed);
        return passed;
    }

    FightReward FightCore(bool elite, int actIndex, ISet<string> allowed, out bool passed)
    {
        passed = true;
        var fr = new FightReward();
        // ① 药水判定（列表构造时）
        float th = elite ? F32(_potionPity + 0.125f) : _potionPity;
        float roll = _rew.NextFloat();
        bool potion = roll < th;
        _potionPity = F32(potion ? _potionPity - 0.1f : _potionPity + 0.1f);
        // ② 金币
        fr.Gold = elite ? _rew.NextInt(35, 46) : _rew.NextInt(10, 21);
        // ③ 药水种类
        if (potion)
        {
            float pr = _rew.NextFloat();
            string rar = pr <= 0.1f ? "Rare" : pr <= 0.35f ? "Uncommon" : "Common";
            var cand = _potionsByRarity[rar];
            fr.Potion = cand[_rew.NextItem(cand.Length)];
        }
        // ④ 卡牌×3（稀有度 pity → 选卡 → 升级掷骰）
        float rareBase = elite ? _eliteRare : _regularRare;
        float uncBase = elite ? 0.40f : 0.37f;
        for (int i = 0; i < 3; i++)
        {
            float num = _rew.NextFloat();
            float rareTh = rareBase + _cardPity;
            string rar = num < rareTh ? "Rare" : num < uncBase + rareTh ? "Uncommon" : "Common";
            _cardPity = rar == "Rare" ? -0.05f : Math.Min(_cardPity + _rarityGrowth, 0.4f);
            var candidates = _cardsByRarity[rar];
            int available = candidates.Length;
            foreach (var prior in fr.Cards) if (prior.Rarity == rar) available--;
            if (available <= 0) throw new InvalidOperationException($"空候选: rar={rar} pool={_cardPool.Length}");
            int index = _rew.NextItem(available);
            string pick = null;
            foreach (var candidate in candidates)
            {
                bool used = false;
                foreach (var prior in fr.Cards) if (prior.Name == candidate) { used = true; break; }
                if (!used && index-- == 0) { pick = candidate; break; }
            }
            _rew.NextFloat(); // act0 升级阈值 0；仍保留这一次 RNG 消耗。
            if (allowed != null && !allowed.Contains(pick))
            {
                passed = false;
                return fr;
            }
            fr.Cards.Add(new CardPick { Name = pick, Rarity = rar, Upgraded = false });
        }
        // ⑤ 精英遗物稀有度（无 pity）
        if (elite)
        {
            float rr = _rew.NextFloat();
            string rar = rr < 0.5f ? "Common" : rr < 0.83f ? "Uncommon" : "Rare";
            fr.EliteRelic = rar; // deque 取件无 RNG，队列内容未建模
        }
        return fr;
    }

    public (int gold, string relicRarity) Treasure()
    {
        int gold = _rew.NextInt(42, 53);
        float num = _trr.NextFloat();
        string rar = num < 0.5f ? "Common" : num < 0.83f ? "Uncommon" : "Rare";
        return (gold, rar);
    }
}
