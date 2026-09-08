# 单元测试指引

## 定位

- 单元测试集中在 `Tests` 目录，项目使用 `*.UnitTest.*` 命名。
- 测试框架统一使用 xUnit v3，测试平台统一使用 Microsoft Testing Platform。
- 基础设施冒烟测试只证明测试发现和执行链路有效，不计入功能覆盖。
- `Incant.UnitTest.Base` 覆盖底层基础设施，`Incant.UnitTest.CXLegacy` 覆盖 C 系列旧实现的确定性行为。
- 依赖真实机器部署的工具链发现由 `Incant.AutoTest.CXLegacyToolchain` 验证，不得混入单元测试。

## 编写原则

- 测试保持黑盒，只通过被测代码的公开契约观察行为；不得通过反射、私有实现或仅为测试开放的入口验证内部细节。
- 按设计的使用情况组织测试，完整覆盖正常路径、边界条件、无效输入和错误结果；代码覆盖率不能替代使用情况覆盖。
- 一个测试表达一个完整且可独立理解的行为，不依赖执行顺序，也不共享可变状态。
- 测试类使用 `<Subject>Tests` 命名，测试方法名称应清楚表达行为和预期结果。
- 测试应快速、确定且可重复，不依赖网络、机器全局状态或未受控的时间与随机性。
- 优先使用 xUnit 自带断言；只有出现明确且重复的需求时才引入额外断言、模拟或测试辅助库。

## 运行与验收

- 开发和排查时优先运行最相关的测试项目、测试类或测试方法，避免每次执行全量测试。
- 修改范围扩大、准备合并或发布时，再运行受影响范围的完整测试；CI 负责执行仓库规定的测试集合。
- 新增或修改行为时同步更新相应测试，并确认设计中的使用情况没有遗漏。
- 测试结果统一写入仓库的 `build/TestResults`，不得在仓库根目录生成 `TestResults`。

## 命令样板

运行范围按“测试方法 → 测试类 → 测试项目”逐步扩大；以下命令以现有 `Writer` 测试为例：

```shell
# 单个测试方法：开发和排查时的默认起点
dotnet test Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj -- --filter-method Incant.UnitTest.Base.Cli.WriterTests.NewWriterIsEmpty

# 单个测试类：同一行为涉及多个测试时使用
dotnet test Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj -- --filter-class Incant.UnitTest.Base.Cli.WriterTests

# 单个测试项目：修改影响整个测试项目时使用
dotnet test Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj

# CI 或退出验收：显式还原、Release 构建并运行测试
dotnet restore Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj
dotnet build Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj --configuration Release --no-restore
dotnet test Tests/Incant.UnitTest.Base/Incant.UnitTest.Base.csproj --configuration Release --no-build --minimum-expected-tests 1
```

- `--` 之后是 Microsoft Testing Platform 和 xUnit 的参数；类名和方法名使用完全限定名。
- 只有已经以相同配置成功构建时才使用 `--no-build`，只有已经成功还原时才使用 `--no-restore`。
- 筛选结果为零个测试应视为错误，不得通过忽略退出码掩盖错误的筛选条件。
- 存在多个测试项目时，优先逐个运行受影响项目；只有退出验收、合并或发布需要时才扩大到完整测试集合。

## 工具链 AutoTest

- Toolchain CI 暂时禁用：工作流保留为 `.github/workflows/toolchain-check.yml.disabled`，NuGet 发布暂不依赖该门禁。恢复时须同时还原工作流扩展名及发布调用；Basic Check 仅还原和构建 Base 测试及主程序，执行 Base 单元测试与主程序运行检查，不检查 CXLegacy。

- CXLegacy 单元测试通过公开 Finder/Provider API 构造受控候选或合成安装目录与启动器，验证 CXLegacy.FindTools / CXLegacy.FindSdk 的调度、筛选、选择、目标别名和不可变快照；不得读取本机安装、Registry 或网络。
- 依赖真实安装的验证统一由 `Incant.AutoTest.CXLegacyToolchain` 承担。它不提供自由组合的发现参数，而是公开 `windows-vs2022`、`windows-vs2026`、`ubuntu-24.04` 和 `macos-15-arm64` 四个完整环境 Profile。
- 每个 Profile 分开声明预装家族覆盖、主动部署安装及目标场景，并依次运行 `Preflight`、`Discover`、`Resolve`、`Build` 和 `Execute`。候选之间互不回退；某个候选失败只跳过依赖它的动作。
- CI Setup 写出 schema version 1 的环境清单，记录实际安装根目录、版本、来源、局部环境和运行时。AutoTest 对主动部署路径应用候选边界及声明版本范围；预装安装来自全局 Finder 发现，旧清单的预装记录不恢复固定版本门槛。
- 全局分别执行一次 ToolSet/SDK 自动发现；主动部署安装执行显式查询，目标 SDK 查询按构建需要复用。查询筛选、无效输入、ABI/变体隔离、部分安装、别名、wrapper、入口优先级、取消、快照和独立安装由受控黑盒单元测试承接。
- 固定签入的 C/C++ 资产用于编译多个对象、创建和检查静态库、链接纯 C 程序、构建共享库、链接 C++ 程序及运行可执行产物。Emscripten 另测默认与 `pic`/side-module 布局；WASI 使用 Wasmtime 执行。
- AutoTest 只读取环境清单和安装，不下载依赖或修改全局环境。额外 SDK 由 `Tests/Incant.AutoTest.CXLegacyToolchain.Setup` 安装到 `build/toolchains`，各候选的环境变量仅传给对应进程。
- schema 2 报告总是在 `finally` 中写出，集中保存安装与 SDK 快照，场景通过 ID 引用，并包含覆盖结果、失败/跳过原因、诊断、动作、退出码、耗时、日志和产物。宿主/清单配置错误返回 2，测试失败返回 1，成功返回 0，取消返回 130。
- 本机只能声明实际执行过的 Profile；四个 GitHub runner 的 matrix 结果才构成跨平台功能验收。

### 受控工具链夹具

- 安装夹具通过共用 `TestDirectory` 在物理临时根下创建。测试内部的符号链接仍属于显式输入；资源期望值从已知安装目标构造，不能调用生产路径规范化实现作为判断依据。调用入口与物理资源身份分别断言。
- .NET 编译器辅助程序使用当前测试 runtime 所属安装的 `DOTNET_ROOT` 及架构变量启动。不要依赖全局 .NET 安装或将整个宿主环境复制进 Finder 查询；用例需要的变量必须显式提供。
- 每次辅助程序调用独立发布启动、完成记录。日志通过临时文件原子发布，用调用 ID 区分前后两次查询；不得跨进程追加同一 JSON 文件或在发现过程中清空日志。调用原始记录作为 xUnit 附件保存。
- 并发、取消和超时测试使用受控启动与释放事件；事件等待同时观察发现任务，提前失败应立即呈现诊断。超时用于防止挂起，不能代替行为同步。清理前须取消并等待发现结束，清理异常作为测试警告保留，不覆盖原始失败。
- WASI 黑盒夹具应覆盖官方发行包的目标与异常变体目录，以及旧布局、部分安装和目标别名。默认 `noeh` 布局标识为 `.`，异常布局为 `eh`，未分类旧布局为 `null`；头文件与 C++ 库不能跨变体补齐。LTO、其他 WASI 目标和 threads 目录不能混入默认布局。
- WASI 新旧目标名称是替代资源组，不能直接求并集。头文件和库各自选择一个安装前缀，同时支持两者分开放置。夹具须覆盖两套完整副本共存、按变体独立回退、部分目标别名不互相补齐，以及安装损坏与恢复后重新选择。C++ 标准头目录只能选择一组，防止 `include_next` 再次命中相同包装头。
- WASI AutoTest 明确选择默认布局并执行原有静态库及可执行程序链。EH 布局本轮验证发现和筛选契约，不作为异常执行能力已经通过的证明。

```shell
# CXLegacy 局部单元测试
dotnet test --project Tests/Incant.UnitTest.CXLegacy/Incant.UnitTest.CXLegacy.csproj -- --filter-class Incant.UnitTest.CXLegacy.FindTools.FinderTests

# SDK 资源、查询语义与 WASI 目标别名测试
dotnet test --project Tests/Incant.UnitTest.CXLegacy/Incant.UnitTest.CXLegacy.csproj -- --filter-class Incant.UnitTest.CXLegacy.FindSdk.FinderTests

# CXLegacy 完整单元测试
dotnet test --project Tests/Incant.UnitTest.CXLegacy/Incant.UnitTest.CXLegacy.csproj

# 使用 CI Setup 生成的清单运行一个完整环境 Profile
Incant.AutoTest.CXLegacyToolchain windows-vs2022
Incant.AutoTest.CXLegacyToolchain windows-vs2026
Incant.AutoTest.CXLegacyToolchain ubuntu-24.04
Incant.AutoTest.CXLegacyToolchain macos-15-arm64

# 本地调试时可显式指定清单、报告和工作目录
dotnet run --project Tests/Incant.AutoTest.CXLegacyToolchain/Incant.AutoTest.CXLegacyToolchain.csproj -- \
  ubuntu-24.04 \
  --environment build/toolchain-environments/ubuntu-24.04.json \
  --report build/toolchain-reports/ubuntu-24.04.json \
  --work-root build/toolchain-autotest/ubuntu-24.04 \
  --keep-work
```

## 参数集合与 Driver

- `Incant.UnitTest.CXLegacy/Arguments` 通过公开集合 API 验证固定属性、未设置与空值、标量和映射冲突、快照、来源、菱形合并、主动重复贡献、移除、覆盖及元数据隔离；不再提供自定义键或通用 Driver 示例。
- 生成器通过编译和调用生成的标量、序列、映射 API 验证；非法声明和方法冲突检查编译诊断，不使用反射或生成源码快照作为主要证据。
- `Arguments` 验证同一配置跨操作使用、必需输入、参数顺序与边界、链接分组、显式不支持的功能及版本敏感诊断。响应编码针对真实 token 的空值、Unicode、引号和反斜杠边界；少量必要的命令字面断言不能取代真实工具链运行。
- 生产 Driver 不做发现、文件写入或执行。真实编译、归档、链接和运行沿用现有 AutoTest 场景；本轮本地只运行单元测试，不运行 Setup 或 CXLegacyToolchain AutoTest。
