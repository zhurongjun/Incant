# Deps Benchmark

测量 `Database` 的 CSV 单文件读写、压缩及体积。结果写入 `build/benchmark/deps`，不加入 CI。

```shell
dotnet run --project Benchmark/Incant.Bench.Deps/Incant.Bench.Deps.csproj -c Release -- --filter '*'
dotnet run --project Benchmark/Incant.Bench.Deps/Incant.Bench.Deps.csproj -c Release -- --filter '*DatabaseReadBenchmarks*'
dotnet run --project Benchmark/Incant.Bench.Deps/Incant.Bench.Deps.csproj -c Release -- --sizes
```

固定数据集：64／10,000 个 Key，每 Key 12 个输入文件、4 个外部文件、4 个参数；从 256 个实际存在的文件构造确定性依赖。使用时间戳模式，使测量侧重依赖库而非文件内容散列。

- `OpenAndRead` 包含打开、完整索引扫描和全部 Key 查询，使用新的元数据缓存；不驱逐 OS 文件缓存，因此不是冷磁盘测试。
- `WarmRead` 复用已经打开的数据库和预热元数据缓存。
- `Insert` 从空库写入；`Update` 修改每个已有 Key 的一个参数。输入和外部文件仍执行真实快照查询，不用假文件系统。
- 读写场景均提供 1／8 路并发，数据准备、打开可写库和最终 Dispose 的持久化 Flush 不计入写入耗时。每次写入自身的 OS Flush 仍在计时范围内。
- `Compact` 单独测量最新记录重写、持久化 Flush 和原子替换，不把压缩开销隐藏在普通写入中。
- `--sizes` 输出初次写入、更新一次和压缩后的逻辑字节数；不包含文件系统簇、目录和 MFT 等额外占用。

一次 Benchmark invocation 是一个完整批次（Keys 次操作），不是单条记录；吞吐量为 `Keys / Mean 秒数`。MemoryDiagnoser 的 Allocated 也是整批分配，包含并发线程；可除以 Keys 比较单条成本。ShortRun 只用于本机趋势比较，磁盘、杀毒、OS 缓存及线程调度都会影响结果，不能用作硬性延迟阈值。
