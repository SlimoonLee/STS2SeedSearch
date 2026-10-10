# 回归样本来源

`replay-v0.111.0.json` 保留优化前的 51 组 SHA-256 指纹。键中的进阶参数用于地图，历史奖励指纹使用原来的 A0 参数；当前 A7+ 奖励由下述独立样本验证，不覆盖历史指纹。

`neow-engine-v0.111.0.json` 由 `Tests/EngineProbe` 直接调用本地 `sts2-cli/lib/sts2.dll` 的奖励生成 API 得到，使用现有 `Sts2Headless` 初始化全解锁单人局。没有用搜索器的 `RewardsReplay` 生成预期卡牌、药水、金币或随机流计数。

- 游戏 DLL SHA-256：`14B57DBAEE1B58CB919625A662831054FD714B3B75B13B7449EAC0E95B98A3E4`。
- 五个角色 × A0/A10 × 八项遗物，另有机械人卷轴箱三种三爪分支，共 83 组。
- 每组按对应遗物源码调用真实卡牌/药水工厂；变形选择起始基础牌。记录效果执行前后 Rewards、Transformations、Niche 计数，以及之后 12 场普通/普通/精英奖励，共 996 场。
- 不模拟战斗、商店、事件选择、遗物递归获得或后续玩家决策；常规奖励对照不应用其他遗物钩子。
- 验证器还检查深拷贝继续推进不共享可变状态。实际筛选是否执行一次、全部候选是否拒绝越界、固定诅咒是否豁免，由 `NeowFilterTests` 单独检查。

从仓库根目录重新生成（更换游戏 DLL 后须先重新提取数据并审查规则）：

```powershell
dotnet run --project seedsearch/Tests/EngineProbe/EngineProbe.csproj -c Release -- ..\sts2-cli\lib seedsearch\Tests\Fixtures\neow-engine-v0.111.0.json
dotnet run --project seedsearch/Tests/SeedSearch.Tests.csproj -c Release -- seedsearch/Tests/Fixtures/replay-v0.111.0.json
```
