# 保持 SimulationRun 的启动状态为 Versioned

`SimulationRun` 仍是单一的运行身份实体。其 seed、规则版本、完成 tick 和人口计数保留在 `SimulationRunComponent`；`ProcessSegment` 与终态/运行态保留在同一实体的 `SimulationRunStateComponent`。

战斗中的高频状态继续使用 `SingleVersion` 并在 tick fence 持久化。启动状态只在创建、恢复和终态转换时写入，必须在启动流程继续前可靠持久化，因此使用默认的 `Versioned` 存储模式和 `Immediate` UnitOfWork。这样恢复、终态保护和身份校验始终读取同一 `SimulationRun` 实体，不引入第二个运行身份。
