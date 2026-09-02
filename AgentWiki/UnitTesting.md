# 单元测试指引

## 定位

- 单元测试集中在 `Tests` 目录，项目使用 `*.UnitTest.*` 命名。
- 测试框架统一使用 xUnit v3，测试平台统一使用 Microsoft Testing Platform。
- 基础设施冒烟测试只证明测试发现和执行链路有效，不计入功能覆盖。
- `Incant.UnitTest.Base` 覆盖底层基础设施，`Incant.UnitTest.Core` 覆盖构建系统核心的确定性行为。
- 依赖真实机器部署的工具链发现由 `Incant.AutoTest.Toolchains` 验证，不得混入单元测试。

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

## 工具链测试

- Core 单元测试只能通过公开 Provider 接口构造受控候选，验证发现调度、筛选、配对和选择，不得读取本机工具链状态。
- `Incant.AutoTest.Toolchains` 使用 Base CLI 提供 `discover` 与 `verify` 子命令；选项应归属实际使用它的子命令，不在入口处手工解析或堆叠无关选项。
- `discover` 用于检查实际发现结果；`verify` 对工具链种类、目标、架构、版本和组件设置明确门禁，并使用解析出的 Profile 分别编译、链接 C 与 C++ HelloWorld。
- `verify clang-cl msvc-link` 与 `verify clang-cl llvm-link` 分别验证 clang-cl 配合 MSVC 和 LLVM Windows 链接器的真实构建路径。
- `verify` 对当前宿主可直接运行的原生产物继续执行冒烟；Emscripten 或 WASI 只有在本机存在对应运行时才执行，其他交叉编译产物只验证构建成功。
- AutoTest 不负责下载、安装或修改全局环境；工具链准备由 CI Job 负责，编译使用临时目录并在结束时清理。

```shell
# Core 局部单元测试
dotnet test Tests/Incant.UnitTest.Core/Incant.UnitTest.Core.csproj -- --filter-class Incant.UnitTest.Core.Toolchains.ResolverTests

# Core 完整单元测试
dotnet test Tests/Incant.UnitTest.Core/Incant.UnitTest.Core.csproj

# 查看当前机器的工具链目录
dotnet run --project Tests/Incant.AutoTest.Toolchains/Incant.AutoTest.Toolchains.csproj -- discover

# 验证当前机器至少存在一个 x64 GCC Profile
dotnet run --project Tests/Incant.AutoTest.Toolchains/Incant.AutoTest.Toolchains.csproj -- verify --kind Gnu --target Linux --arch X64 --minimum 1

# 分别验证 clang-cl 的两个 Windows 链接器变种
dotnet run --project Tests/Incant.AutoTest.Toolchains/Incant.AutoTest.Toolchains.csproj -- verify clang-cl msvc-link --arch X64
dotnet run --project Tests/Incant.AutoTest.Toolchains/Incant.AutoTest.Toolchains.csproj -- verify clang-cl llvm-link --arch X64
```
