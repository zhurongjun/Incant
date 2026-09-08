# 参数集合与 C++ Driver

实现分为 `Incant.Core.Arguments` 的独立配置内核、`Incant.Arguments.Generator` 的编译期便捷 API，以及 `Incant.Core.Cpp.Arguments` 的参数解释。配置来源是 ArgumentSet；调用方负责依赖传播、场景组合、文件系统和执行。

## SB 覆盖与 review 清单

下表包含 Attribute 配置、操作字段、构造函数默认值和专用透传。下列业务映射已落实并逐项完成参数语义 review；参数 review 不代表真实工具运行通过。

| SB 配置或行为 | 新配置领域 | Driver / 参考 | review 约束 |
| --- | --- | --- | --- |
| CVersion、CppVersion、Source、isPCH、语言选择 | Language、Standard、Inputs、Pch | Compile；CMake GNU/Clang/Windows-MSVC PCH | 区分 C/ObjC/C++/ObjC++，PCH 不负责调度 |
| Defines、IncludeDirs、路径基目录 | Defines、Includes、ArgumentPath | Compile/Resource；所有 SB Driver | 宏按名称冲突，路径在声明时确定 |
| Exception、RTTI、RuntimeLibrary | Exceptions、Rtti、WindowsRuntime、StandardLibrary、RuntimeLinkage | Compile/Link；Xmake clang runtime | 无隐式引擎宏；编译和链接一致 |
| Optimization、FpModel、SIMD | Optimization、FloatingPoint、InstructionSet | Compile/Link；UBT Clang 分组函数 | 不隐式启用 LTO，不转换 x86 SIMD 为 Wasm |
| WarningLevel、WarningAsError、DebugSymbols、PDB、PDBMode、DynamicDebug | Warnings、Debug、Pdb、DynamicDebug | MSVC/Clang/GNU | 版本敏感功能单独验收 |
| Object、SourceDependencies、UsePCHAST | Output、Dependencies、Pch | Compile；CMake PCH 与依赖输出 | MSVC PCH 对象由调用方声明和链接 |
| TargetType、Arch、Inputs、Output | Operation、OutputKind、Architecture、Inputs、Output | Compile/Archive/Link | 无 Target 实体、无执行职责 |
| LinkDirs、Link、WholeArchive、NoDefaultLibrary | LibraryDirectories、LinkInputs、NoDefaultLibraries、DisableDefaultLibraries | Link；Xmake nf_linkgroup | 保留重复、状态与分组顺序 |
| ManifestInput、PDB、DynamicDebug、LinkerArgs | ManifestInputs、Pdb、DynamicDebug、Raw | Windows Link | 归档与链接是不同操作 |
| AppleFramework、BundleLoader | Frameworks、BundleLoader | Apple Link；CMake Darwin | 路径独立 token，设备和模拟器独立 |
| CFlags、CppFlags、CXFlags、MSVC/CL/ClangCL/Clang/AppleClang/Emscripten 专属 flags | RawArgument（操作、语言、方言、位置、传递方式） | 所有 Driver | 不拆空格，不排序或去重 |
| RC Source、Output、Defines、IncludeDirs | 同一键表中的资源操作 | Resource | 不注入 shell 转义 |
| AR/Emar -cr、输入与输出 | ArchiveMode、Inputs、Output | Archive；Xmake ar/llvm_ar | 创建由调用方清理旧产物，追加保留旧成员 |
| 构造函数 -c、/nologo、/bigobj、/FC、/Zc:preprocessor、/cgthreads1 | 操作参数、显式编译选项及 Raw | Compile | /bigobj 等可选偏好不是隐藏全局默认 |
| 构造函数 _WIN32/_WIN64、_HAS_EXCEPTIONS、POSIX/GNU 宏 | 显式 Defines | 调用方 | 不复制引擎隐式宏 |
| 构造函数 -pthread、栈、内存增长、优化隐式 LTO | Threads、Wasm 设置、Lto | Emscripten Compile/Link | 每一项独立设置 |
| Target Visibility、FinalArguments、Override、PathBehavior | ArgumentSet 合并、覆盖、ArgumentPath | 通用内核 | 调用方组织集合，无 Target 代理 |
| CppSL 的 Defines/Includes | 自定义 IArgumentDriver | 独立扩展示例 | 不迁移引擎 Shader 管线 |
| SB 未完善：sysroot、triple、multilib、ABI/API、运行库 | 平台参数键 | GNU/LLVM/Apple/Android/WASI | 保留默认驱动上下文 |
| SB 未完善：LTO、Sanitizer、分组、响应文件 | 跨操作配置、链接树、ResponseFileEncoder | CMake/Xmake/UBT/官方文档 | 不读取安装，不猜测未知版本能力 |

## 本地参考

- SB：`D:\workspace\project\ExtremeEngine\engine\tools\SB`
- CMake：`D:\workspace\project_git\CMake`，`Modules/Compiler/{GNU,Clang}.cmake`、`Modules/Platform/Windows-MSVC.cmake`、`Darwin.cmake`
- Xmake：`D:\workspace\project_git\xmake`，`xmake/modules/core/tools/{gcc,clang,cl,ar,llvm_ar,emcc}.lua`
- UBT：`C:\Program Files\Epic Games\UE_5.8\Engine\Source\Programs\UnrealBuildTool`，Clang、Windows、Mac、IOS、Android ToolChain 参数函数
- [Emscripten settings](https://emscripten.org/docs/tools_reference/settings_reference.html)
- [MSVC response files](https://learn.microsoft.com/en-us/cpp/build/reference/at-specify-a-compiler-response-file?view=msvc-170)

## 验收边界

通用合并、来源、快照、生成 API 和传输编码由 Core 黑盒单元测试验证。具体平台参数按上表 review。现有 AutoTest 场景调用生产 Driver；本地不运行 Setup/AutoTest，不新增 CI 场景。

## 文件组织与扩展

通用内核按键定义、内置策略、集合、来源和 Driver 结果组织为五个文件。C++ 公共键集中在 `CppArguments.cs`；命令、编译、链接和归档领域的小型枚举与记录分别合并在相应的 `*Options.cs`。内部按共同规则、编译（含 RC）、链接、归档和生成上下文分工；不按每个 flag 或平台建立类文件。生产参数模块共 17 个 C# 文件，生成器单独一个实现文件。

新增通用配置只需声明静态键；标量、序列、映射分别通过快照和合并策略定义语义。`GenerateArgument` 生成 `WithXxx` 和 `WithoutXxx`，序列与字符串键映射还生成 `AppendXxx`、`RemoveXxx`。独立示例及消费者编译测试位于现有 Core 测试的 `Arguments` 目录。

```csharp
using Incant.Core.Arguments;
using Incant.Core.Cpp.Arguments;

ArgumentSet common = new ArgumentSet(new Dictionary<string, string> { ["configuration"] = "library" })
    .WithLanguage(CppLanguage.Cpp)
    .WithStandard("c++17")
    .WithIncludes([ArgumentPath.Resolve("include", absoluteProjectDirectory)]);
ArgumentSet compilation = common.WithInputs([sourcePath]).WithOutput(objectPath);
ArgumentGenerationResult result = new CppArgumentDriver(CppDialect.Clang, CppOperation.Compile)
    .Generate(compilation);
```

手写 `With(key, value)` 与生成 API 等价。`With` 显式替换字段，`Merge` 使用键策略，`Override` 只替换传入字段。序列保留顺序和重复；同一原始贡献的菱形导入只合并一次，主动追加则形成新贡献。移除标记、空集合、显式 false 和从未设置相互独立。新快照有新身份，未改字段仍追溯到原始集合；元数据不参与参数生成。

C++ 新增配置需同时登记操作适用性及解释函数，明确“其他操作无关”和“当前操作不支持”。不能只加入键而静默丢弃请求。原始参数按语言、操作、方言和位置筛选，每项始终是一个 token；链接树保留嵌套分组和重复库。GNU Windows 的定向导出通过显式 `.def` 文件输入表达，不能套用 ELF 的导出选项。

## 具体 review 结论

- MSVC 的 PCH 创建必须声明对象产物；GCC 使用 `<header>.gch`，Clang 支持只指定 PCH artifact 的消费方式。调度和文件存在性由调用方处理。
- 优化与 LTO 独立；GNU/LLVM LTO 归档需要调用方确认工具能力，MSVC `/GL` 与 MSVC linker 配对，Clang LTO 与 lld-link 配对。Apple LTO 调试明确保留中间对象路径。
- Windows CRT、编译器支持库、C++ 标准库和静态/动态链接分开设置；独立 static libc++ 需要显式提供 libc++/ABI 等运行库输入，排在用户输入之后。Android 保留驱动的 `-static-libstdc++` 选择方式。
- MSVC Dynamic Debug 按编译器版本、x64、调试信息、LTO 和 ASan 组合检查，同时参与编译、归档与链接。其约束依据 [MSVC Dynamic Debug 文档](https://learn.microsoft.com/en-us/cpp/build/reference/dynamic-deopt?view=msvc-170)。
- clang-cl 输入使用 `/Tc`、`/Tp` 操作数，保留 Unix 绝对路径和输入后的选项；依据 [Clang command syntax](https://clang.llvm.org/docs/UsersManual.html#clang-cl)。
- Emscripten main/side module 在编译和链接同时配置；线程、异常、SIMD、内存、栈和模块导出相互独立。WASI 默认与 EH 模式明确隔离，WASI threads 必须声明匹配 triple；不提供不稳定的 WASI 共享库必需能力。
- 响应文件编码与文件创建分离。GNU、Microsoft、LLVM Windows 各自处理引号、反斜杠、空参数和编码；拒绝换行、NUL 和嵌套响应引用。AutoTest 保存逻辑参数和实际传输参数，Python launcher 前缀留在响应文件之外。
- AutoTest 只转换已经解析的驱动上下文与资源组。原生 Linux 默认 sysroot 不被平台资源根覆盖；Windows/Apple/Bundle 的组合和资源顺序仍由既有 Resolve 决定。归档重建只清理当前动作声明、工作目录内的产物。

本轮未迁入 SB 的 Target 代理、引擎宏及隐藏优化/内存默认值；对应能力通过集合合并、显式定义和配置键表达。未新增 PCH/LTO CI 场景、C++ Modules/BMI 调度或 PGO 训练设施。
