# 单元测试指引

## 定位

- 单元测试集中在 `Tests` 目录，项目使用 `*.UnitTest.*` 命名。
- 测试框架统一使用 xUnit v3，测试平台统一使用 Microsoft Testing Platform；框架依赖以相应项目文件为准，测试平台与 SDK 配置见 [global.json](../../global.json)。
- 基础设施冒烟测试只证明测试发现和执行链路有效，不计入功能覆盖。
- 依赖真实机器部署的验证属于集成测试或 AutoTest，不得混入单元测试。

## 编写原则

- 测试保持黑盒，只通过被测代码的公开契约观察行为；不得通过反射、私有实现或仅为测试开放的入口验证内部细节。
- 按设计的使用情况组织测试，完整覆盖正常路径、边界条件、无效输入和错误结果；代码覆盖率不能替代使用情况覆盖。
- 一个测试表达一个完整且可独立理解的行为，不依赖执行顺序，也不共享可变状态。
- 测试类使用 `<Subject>Tests` 命名，测试方法名称应清楚表达行为和预期结果。
- 测试应快速、确定且可重复，不依赖网络、机器全局状态或未受控的时间与随机性。
- 优先使用 xUnit 自带断言；只有出现明确且重复的需求时才引入额外断言、模拟或测试辅助库。

## 受控依赖与夹具

- 文件系统测试使用隔离的临时目录；预期结果来自受控输入和公开契约，不能调用被测实现计算期望值。
- 测试进程使用明确的运行时、输入和局部环境，不依赖本机安装或复制整个宿主环境。
- 并发、取消和超时测试通过受控事件同步，并同时观察被测任务；超时用于防止挂起，不能代替行为同步。
- 保留独立调用的错误与日志证据。清理前取消并等待相关任务结束，清理失败应报告，但不能覆盖原始失败。

## 运行与验收

- 开发和排查时优先运行最相关的测试项目、测试类或测试方法，避免每次执行全量测试。
- 修改范围扩大、准备合并或发布时，再运行受影响范围的完整测试；CI 负责执行仓库规定的测试集合。
- 新增或修改行为时同步更新相应测试，并确认设计中的使用情况没有遗漏。
- 测试结果统一写入仓库的 `build/TestResults`，不得在仓库根目录生成 `TestResults`；配置见 [Directory.Build.props](../../Directory.Build.props)。

## 命令样板

运行范围按“测试方法 → 测试类 → 测试项目”逐步扩大；以下命令以现有 `Writer` 测试为例：

```shell
# 单个测试方法：开发和排查时的默认起点
dotnet test --project Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj -- --filter-method Incant.UnitTest.Base.Cli.WriterTests.NewWriterIsEmpty

# 单个测试类：同一行为涉及多个测试时使用
dotnet test --project Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj -- --filter-class Incant.UnitTest.Base.Cli.WriterTests

# CI 或退出验收：显式还原、Release 构建并运行测试
dotnet restore Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj
dotnet build Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj --configuration Release --no-restore
dotnet test --project Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj --configuration Release --no-build --minimum-expected-tests 1
```

- `--` 之后是 Microsoft Testing Platform 和 xUnit 的参数；类名和方法名使用完全限定名。
- 只有已经以相同配置成功构建时才使用 `--no-build`，只有已经成功还原时才使用 `--no-restore`。
- 筛选结果为零个测试应视为错误，不得通过忽略退出码掩盖错误的筛选条件。
- 存在多个测试项目时，优先逐个运行受影响项目；只有退出验收、合并或发布需要时才扩大到完整测试集合。
