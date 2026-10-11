namespace SeedSearch;

public sealed record NeowCardReport(string card, string kind, int group = 0, string from = "");
public sealed record NeowEffectReport(string relic, NeowCardReport[] cards, string[] potions, bool exact = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[] relics = null)
{
    // 固定诅咒只展示；其余奖励候选、整组卡牌与变形结果均须在白名单内。
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> FilteredCards => cards.Where(c => c.kind != "fixedCurse").Select(c => c.card);
}

public sealed partial class RewardsReplay
{
    /// <summary>实际生成捏奥卡牌，在首战前执行一次；各来源使用游戏指定的独立 RNG。</summary>
    public NeowEffectReport ApplyNeowEffect(string relic, RelicBag bag)
    {
        var cards = new List<NeowCardReport>();
        var potions = new List<string>();
        List<string> relics = null;
        switch (relic)
        {
            case "ArcaneScroll":
                Add(NonCombatCards(_cardPool, 1, rareOnly: true), "added");
                break;
            case "HeftyTablet":
                Add(NonCombatCards(_cardPool, 3, rareOnly: true), "offer", 1);
                cards.Add(new("Injury", "fixedCurse"));
                break;
            case "ScrollBoxes":
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (int bundle = 1; bundle <= 2; bundle++)
                {
                    // 机械人每组先判定1%三爪；该分支不会向 used 添加 Claw。
                    if (_character == "Defect" && _rew.NextInt(100) < 1)
                    {
                        for (int i = 0; i < 3; i++) cards.Add(new("Claw", "bundle", bundle));
                        continue;
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        var pool = _cardsByRarity[i == 2 ? "Uncommon" : "Common"].Where(c => !used.Contains(c)).ToArray();
                        string chosen = pool[_rew.NextItem(pool.Length)];
                        used.Add(chosen);
                        cards.Add(new(chosen, "bundle", bundle));
                    }
                }
                break;
            case "LostCoffer":
                Add(NonCombatCards(_cardPool, 3), "offer", 1);
                potions.Add(NonCombatPotion());
                break;
            case "LeafyPoultice":
                string[] transformationPool = TransformPool();
                cards.Add(new(transformationPool[_transf.NextItem(transformationPool.Length)], "transform", 0, "Strike" + _character));
                cards.Add(new(transformationPool[_transf.NextItem(transformationPool.Length)], "transform", 0, "Defend" + _character));
                break;
            case "NewLeaf":
                // 开局可变的基础牌使用相同候选池；永久的进阶诅咒不能选择。
                string[] leafPool = TransformPool();
                cards.Add(new(leafPool[_niche.NextItem(leafPool.Length)], "transform", 0, "StartingBasic"));
                break;
            case "SmallCapsule":
                PullSequenceRelic();
                break;
            case "LargeCapsule":
                PullSequenceRelic();
                PullSequenceRelic();
                break;
            case "LeadPaperweight":
                Add(NonCombatCards(GameData.CardPools["ColorlessCardPool"].Where(c => !GameData.CardMultiplayerOnly.Contains(c)).ToArray(), 2), "offer", 1);
                break;
            case "Kaleidoscope":
                for (int group = 1; group <= 2; group++)
                {
                    var pools = new[] { "Ironclad", "Silent", "Defect", "Regent", "Necrobinder" }
                        .Where(c => c != _character).ToList();
                    pools.StableShuffle(_niche);
                    foreach (string character in pools.Take(3))
                        Add(NonCombatCards(GameData.CardPools[character + "CardPool"].Where(c => !GameData.CardMultiplayerOnly.Contains(c)).ToArray(), 1), "offer", group);
                }
                break;
            default:
                var consumption = NeowEffects.Of(relic);
                for (int i = 0; i < consumption.Rewards; i++) _rew.NextFloat();
                for (int i = 0; i < consumption.Niche; i++) _niche.NextDouble();
                for (int i = 0; i < consumption.Transformations; i++) _transf.NextDouble();
                for (int i = 0; i < consumption.PotionGen; i++) _potGen.NextDouble();
                for (int i = 0; i < consumption.BagPulls; i++)
                    PullSequenceRelic();
                break;
        }
        return new(relic, cards.ToArray(), potions.ToArray(), exact: !NeowEffects.HasUnmodeledCascade(relic), relics?.ToArray());

        void Add(IEnumerable<string> generated, string kind, int group = 0)
        {
            foreach (string card in generated) cards.Add(new(card, kind, group));
        }

        string PullSequenceRelic()
        {
            float roll = _rew.NextFloat();
            string rarity = roll < 0.5f ? "Common" : roll < 0.83f ? "Uncommon" : "Rare";
            string pulled = bag?.Pull(rarity) ?? "(袋空)";
            (relics ??= new()).Add(pulled);
            return pulled;
        }
    }

    string[] TransformPool() => _cardPool.Where(c => GameData.CardRarity[c] is "Common" or "Uncommon" or "Rare").ToArray();

    // 非战斗奖励使用基础稀有度，不读取或更新战斗稀有度 pity。
    string[] NonCombatCards(string[] pool, int count, bool rareOnly = false)
    {
        var chosen = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string rarity = "Rare";
            if (!rareOnly)
            {
                float roll = _rew.NextFloat();
                rarity = roll < _regularRare ? "Rare" : roll < 0.37f + _regularRare ? "Uncommon" : "Common";
            }
            string[] candidates = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                candidates = pool.Where(c => GameData.CardRarity[c] == rarity && !chosen.Contains(c)).ToArray();
                if (candidates.Length > 0) break;
                rarity = rarity switch { "Common" => "Uncommon", "Uncommon" => "Rare", _ => "Common" };
            }
            if (candidates.Length == 0) throw new InvalidOperationException("捏奥奖励没有可用卡牌。");
            chosen.Add(candidates[_rew.NextItem(candidates.Length)]);
            if (!rareOnly) _rew.NextFloat(); // 第一幕升级概率为0，仍消耗一次；Arcane/Hefty 设置 NoUpgradeRoll。
        }
        return chosen.ToArray();
    }

    string NonCombatPotion()
    {
        float roll = _rew.NextFloat();
        string rarity = roll <= 0.1f ? "Rare" : roll <= 0.35f ? "Uncommon" : "Common";
        string[] pool = _potionsByRarity[rarity];
        return pool[_rew.NextItem(pool.Length)];
    }
}
