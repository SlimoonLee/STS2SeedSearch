namespace SeedSearch;

/// <summary>
/// 不直接生成卡牌的捏奥选项消耗表；卡牌效果由 NeowReplay 实际重放。
/// 选项生成（事件 Rng）与效果执行（这些流）完全独立：选了才消耗。
/// </summary>
public static class NeowEffects
{
    public readonly record struct Consumption(int Rewards, int Niche, int Transformations, int PotionGen, int BagPulls);

    /// <summary>按遗物类名返回消耗。奖励型选项的规则：
    /// Uniform 卡奖=1 抽/张（无稀有度/升级掷骰）；RegularEncounter 非战斗源=3 抽/张（RollWithBaseOdds+选卡+升级掷骰，pity 不动）；
    /// 袋取件=1 次 Rewards 稀有度掷骰+袋首无 RNG 取件；连锁遗物的递归效果尚未展开。</summary>
    public static Consumption Of(string relic) => relic switch
    {
        "ArcaneScroll"    => new(Rewards: 1, 0, 0, 0, 0),          // Uniform+Rare 谓词+NoUpgradeRoll
        "Kaleidoscope"    => new(18, Niche: 6, 0, 0, 0),           // 2 次外职业4池洗牌，各取3池生成1张（实际重放）
        "ScrollBoxes"     => new(6, 0, 0, 0, 0),                   // 非Defect 6；Defect 8/5/2，必须按实际分支生成
        "LeadPaperweight" => new(6, 0, 0, 0, 0),                   // 无色池 RegularEncounter × 2
        "MassiveScroll"   => new(3, 0, 0, 0, 0),                   // RegularEncounter 多人限定 × 3
        "LostCoffer"      => new(11, 0, 0, 0, 0),                  // 卡奖 3×3 + 药水奖 2（均 Rewards）
        "PhialHolster"    => new(0, 0, 0, PotionGen: 4, 0),        // 2 药水 × [稀有度+选卡]（CombatPotionGeneration!）
        "SmallCapsule"    => new(0, 0, 0, 0, BagPulls: 1),         // BagPulls 自身包含稀有度掷骰，不重复计数
        "LargeCapsule"    => new(0, 0, 0, 0, BagPulls: 2),         // 2 × [稀有度+袋首]；递归遗物效果未展开
        "HeftyTablet"     => new(3, 0, 0, 0, 0),                   // 3 稀有 Uniform 选1 + Injury(无RNG)
        "NewLeaf"         => new(0, Niche: 1, 0, 0, 0),            // 玩家选 1 张变形（Niche）
        "LeafyPoultice"   => new(0, 0, Transformations: 2, 0, 0),  // 起始打击+防御变形（Transformations）
        "NeowsBones"      => new(27, Niche: 1, 0, 0, 0),           // 28 项池(30-MassiveScroll-自身)洗牌 27 抽 + 1 诅咒(Niche)；二选一级联其遗物表
        _ => new(0, 0, 0, 0, 0),                                   // 金币/HP/升级/被动/移除/加固定牌类：零消耗
    };

    public static bool HasUnmodeledCascade(string relic) => relic is "NeowsBones" or "SmallCapsule" or "LargeCapsule";

    /// <summary>指定选项是否会在前 X 场前引入"玩家决策"（选中后还需再做选择，影响实际序列）。</summary>
    public static bool HasPlayerDecision(string relic) => relic switch
    {
        "NewLeaf" or "PreciseScissors" or "PrecariousShears" or "Pomander"
        or "Kaleidoscope" or "ScrollBoxes" or "LeadPaperweight" or "MassiveScroll"
        or "HeftyTablet" or "LostCoffer" or "NeowsBones" => true,
        _ => false,
    };
}
