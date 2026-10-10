namespace SeedSearch;

/// <summary>
/// 第一幕选择重放（StartRunLobby.BeginRunLocally + ActModel.GetRandomList 逐位转写）。
/// 单人且玩家未碰角色选择界面的 Act 下拉框（默认 "random"，开发遗留组件）时，
/// 第一幕 = act_selection RNG（seed 派生）掷出：
///   - 暗港已解锁但未被发现 → 强制暗港（不消耗抽签）；
///   - 否则 [Overgrowth, Underdocks] 各 50%（NextInt(2)，1 抽）；
///   - Hive/Glory 各自再抽 1 次（单候选列表仍消耗抽签）。
/// act_selection 与 UpFront 互相独立，不影响队列/地图/奖励流。
/// </summary>
public static class ActSelection
{
    static readonly ulong StreamHash = Rng.HashString("act_selection");
    /// <returns>第一幕的 ActDef 名（"Overgrowth"/"Underdocks"）。</returns>
    public static string Pick(string seed, bool underdocksUnlocked, bool underdocksDiscovered, bool multiplayer = false)
        => Pick(Rng.HashString(seed), underdocksUnlocked, underdocksDiscovered, multiplayer);

    public static string Pick(ulong seedHash, bool underdocksUnlocked, bool underdocksDiscovered, bool multiplayer = false)
    {
        var rng = new Rng(seedHash + StreamHash);

        // index 0: [Overgrowth(IsDefault), Underdocks] —— 按 ModelDb.Acts 声明序进桶。
        // 这里候选固定，直接取索引，避免每颗种子创建临时 List。
        string act0;
        if (underdocksUnlocked && !multiplayer && !underdocksDiscovered)
            act0 = "Underdocks";                  // 未发现的非默认幕强制登场，不抽签
        else
            act0 = rng.NextItem(underdocksUnlocked ? 2 : 1) == 0 ? "Overgrowth" : "Underdocks";

        // index 1: [Hive]（单候选仍抽 1 次）
        _ = rng.NextItem(1);
        // index 2: [Glory]
        _ = rng.NextItem(1);
        return act0;
    }
}
