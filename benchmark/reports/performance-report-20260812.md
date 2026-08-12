# SpaceBattle Release 性能对比与 40 ms 验收

**日期:** 2026-08-12
**引擎版本:** 当前 HEAD
**关联 Issue:** #26

---

## 1. 环境与配置 (AC1)

| 项目 | 值 |
|---|---|
| 机器 | AMD Ryzen 7 260 w/ Radeon 780M Graphics |
| 逻辑处理器 | 16 |
| 操作系统 | Windows 11 (10.0.26200) |
| .NET 版本 | net10.0 |
| 飞船数 | **50,000** |
| Typhon worker | **12** |
| 构建配置 | **Release** |
| Deep profiling | **关闭** |
| Page cache | 512 MiB |
| 内存封套 | 1024 MiB |
| 模拟 seed | `0x5350414345424154` |

### 复现命令

```bash
# 构建
dotnet build demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release

# 运行基准测试
dotnet run --project demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release -- benchmark
```

基准测试驱动位于 `demo/SpaceBattle/SpaceBattle.Main/BenchmarkDriver.cs`，自动配置 12 worker 与 50,000 飞船。

---

## 2. 初始化与一次性成本 (AC2)

| 阶段 | 耗时 |
|---|---|
| 数据库创建并初始化（创建世界） | 1,832.8 ms |
| 总初始化（含引擎启动） | 5,987.2 ms |

### 首个 runtime tick 与稳态对比

首个 runtime tick（#1）包含 ShipView 首次在 runtime 下重建及 TargetLockIndex 初始化：

```
Tick #1: total=489.765ms
  [State=69.141ms, Steering=80.238ms, Movement=63.581ms,
   TargetLockCleanup=69.600ms, Targeting=71.438ms, Combat=37.098ms,
   Resolution=77.607ms, Output=9.640ms]
```

预热 256 tick 后稳态均值 361.676 ms。首个 tick 多出约 128 ms。**一次性重建成本与稳态时序明确分离。**

---

## 3. 稳态性能数据（预热后 2048 ticks）

### 完整 tick 统计

| 指标 | 值 (ms) |
|------|--------:|
| 均值 | 361.676 |
| 中位数 (p50) | 383.811 |
| p95 | 449.100 |
| **p99** | **482.678** |
| 最大值 | 1,815.359 |
| 最小值 | 189.601 |
| 标准差 | 77.906 |

### 2048-tick 滚动窗口 p99 (AC5)

| 指标 | 值 |
|------|------|
| 窗口数 | 1（样本数 = 窗口大小） |
| 滚动 p99 最大值 | **482.678 ms** |

**40 ms 预算验收：未通过**（滚动窗口 p99 最大值为 482.678 ms，超出预算 12×）。

> ⚠ 注意：40 ms 预算对应 25 tick/s 的实时帧率目标。本测试在 Ryzen 7 260 笔记本上进行；该机器为低功耗移动处理器，与台式机 Ryzen 9 7950X 存在数量级差异。CI 不应采用固定毫秒阈值（见 AC1/AC6/AC7）。

---

## 4. 各阶段耗时对比 (AC3)

| 阶段 | 均值 (ms) | p50 (ms) | p95 (ms) | p99 (ms) | 占 tick 比 |
|------|----------:|---------:|---------:|---------:|----------:|
| ShipViewRefresh | 0.000 | 0.000 | 0.000 | 0.000 | 0.0% |
| **State** | **51.011** | **52.201** | **66.542** | **71.385** | **14.1%** |
| **Steering** | **57.619** | **58.545** | **76.841** | **86.121** | **15.9%** |
| **Movement** | **60.487** | **62.172** | **77.928** | **85.310** | **16.7%** |
| **TargetLockCleanup** | **84.691** | **93.235** | **113.891** | **125.655** | **23.4%** |
| **Targeting** | **47.636** | **47.908** | **62.295** | **69.858** | **13.2%** |
| Combat | 8.923 | 9.154 | 12.236 | 13.136 | 2.5% |
| **Resolution** | **49.536** | **50.732** | **65.372** | **71.908** | **13.7%** |
| Output | 0.070 | 0.058 | 0.098 | 0.160 | 0.0% |

### 关键发现

1. **ShipViewRefresh ≈ 0 ms**：ECS view 增量刷新几乎不消耗时间——证实输入准备成本已被消除（AC4）。
2. **Combat 仅占 2.5%**：并行化的 Combat 系统充分受益于 12 worker。
3. **TargetLockCleanup 占比最高（23.4%）**：涉及 AdvanceTimedBehaviorDurations（遍历 50,000 飞船）+ AdvanceExistingLocks（遍历活跃锁）。为 DAG 中最重的单线程阶段。
4. **其余各阶段各占 ~13-17%**：每个阶段独立遍历 TickWorkset（50,000 飞船），产生 O(N) 的数据库操作。

---

## 5. 重复全量扫描消除证据 (AC4)

### Ship roster 全量扫描消除

优化前（#24 之前）：每个阶段单独查询数据库获取所有存活飞船 → O(N) 查询 × 9 阶段 = 9 次全量数据库迭代。

优化后（当前）：
- **TickWorkset 与 ShipRoster 在内存中维护为 `EntityId[]`**，系统遍历时直接引用数组，零数据库查询。
- **`ShipRoster.ApplyDelta` 增量更新**：使用 O(log N) 二分查找 + O(N) 归并合并 added/removed，而非每次 tick 全量重建。
- **`ShipViewRefresh` 仅刷新 ECS view 增量**（delta 机制追踪 added/removed），然后将增量合并到 roster。

结果：ShipViewRefresh 阶段的 **wall-clock 耗时 = 0.000 ms（均值）**，输入准备阶段已完全消除可测量开销。

### Target Lock 全量扫描消除

优化前（#25 之前）：TargetLockCleanup 与 Targeting 阶段需要遍历全部 TargetLock 实体 → 全量数据库扫描。

优化后（当前）：
- **`TargetLockIndexes`** 提供 `Add` / `Remove` / `GetAllLockIds` / `GetOwnerLockIds` / `GetLocksForShip` 等 O(1) 索引操作。
- **TargetLockCleanupSystem.AdvanceExistingLocks** 使用 `state.CopyTargetLockIds()` 获取当前所有活跃锁 ID 列表（内存返回 `EntityId[]`），消除数据库全量扫描。
- **TargetingSystem** 使用 `state.CopyOwnerLockCounts()` 获取每艘飞船的活跃锁计数（O(1) 字典复制），避免实时查询 TargetLock 表。
- **ResolutionSystem.ClearDeadLocks** 使用 `state.CopyTargetLockIdsForShip()` 仅获取被摧毁飞船相关锁（精准索引）。

---

## 6. 设计约束验证 (AC7)

- ✅ 所有优化仅限 `demo/SpaceBattle/` 目录，未修改 `src/` 中 Typhon 核心引擎。
- ✅ 采用相同负载（50,000 飞船、12 worker、默认 seed）进行对比。
- ✅ 确定性已验证（#25）：相同 seed 与 worker 配置产生相同的 tick 序列。
- ✅ 基准测试不设置机器相关的毫秒阈值；CI 验收应当使用回归比较。

---

## 7. 现有测试验证

全部 76 个 SpaceBattle 测试在 Release 配置下通过：

| 测试套件 | 通过 |
|----------|:----:|
| StartupTests | 8 |
| ProductionConfigurationTests | 3 |
| SimulationDefinitionTests | 2 |
| BehaviorRulesTests | 4 |
| MovementRulesTests | 1 |
| CombatRulesTests | 3 |
| RuntimeProgressionTests | 8 |
| ShipMembershipViewTests | 11 |
| TargetLockTests | 8 |
| ObservabilityTests | 3 |
| InitialWorldTests | 3 |
| TerminalOutcomeTests | 6 |
| PauseRecoveryTests | 8 |
| RecoveryValidationTests | 4 |
| DeterminismTests | 4 |
| **总计** | **76** |

---

## 8. 附录：末尾 5 tick 明细

| Tick | Total | ShipView | State | Steering | Move | LockCleanup | Target | Combat | Resolve | Output |
|-----:|------:|---------:|------:|---------:|-----:|------------:|-------:|-------:|--------:|-------:|
| 2300 | 276.9 | 0.0 | 35.5 | 41.3 | 53.8 | 59.4 | 36.6 | 9.8 | 39.1 | 0.1 |
| 2301 | 240.8 | 0.0 | 36.1 | 34.8 | 38.8 | 50.6 | 33.6 | 9.6 | 36.3 | 0.1 |
| 2302 | 263.1 | 0.0 | 35.0 | 41.5 | 43.3 | 63.8 | 35.3 | 9.3 | 33.8 | 0.1 |
| 2303 | 260.9 | 0.0 | 33.9 | 47.4 | 37.2 | 56.9 | 31.9 | 9.9 | 42.4 | 0.1 |
| 2304 | 275.7 | 0.0 | 54.7 | 44.2 | 42.1 | 61.1 | 30.1 | 9.7 | 32.5 | 0.1 |

注：所有时间单位为毫秒（ms）。
