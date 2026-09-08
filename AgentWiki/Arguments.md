# 参数集合与 C++ Driver

实现集中在 `Incant.CX.Arguments`：固定属性集合、来源及合并、C 系列参数解释。`Incant.CX.Arguments.Generator` 只为该集合生成属性实现与修改方法。配置来源是 ArgumentSet；调用方负责依赖传播、场景组合、文件系统和执行。

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
| RC Source、Output、Defines、IncludeDirs | 同一集合中的资源属性 | Resource | 不注入 shell 转义 |
| AR/Emar -cr、输入与输出 | ArchiveMode、Inputs、Output | Archive；Xmake ar/llvm_ar | 创建由调用方清理旧产物，追加保留旧成员 |
| 构造函数 -c、/nologo、/bigobj、/FC、/Zc:preprocessor、/cgthreads1 | 操作参数、显式编译选项及 Raw | Compile | /bigobj 等可选偏好不是隐藏全局默认 |
| 构造函数 _WIN32/_WIN64、_HAS_EXCEPTIONS、POSIX/GNU 宏 | 显式 Defines | 调用方 | 不复制引擎隐式宏 |
| 构造函数 -pthread、栈、内存增长、优化隐式 LTO | Threads、Wasm 设置、Lto | Emscripten Compile/Link | 每一项独立设置 |
| Target Visibility、FinalArguments、Override、PathBehavior | ArgumentSet 合并、覆盖、ArgumentPath | 集合合并 | 调用方组织集合，无 Target 代理 |
| CppSL 的 Defines/Includes | Defines、Includes | C 系列编译配置 | 不提供自定义 Driver 接口，不迁移引擎 Shader 管线 |
| SB 未完善：sysroot、triple、multilib、ABI/API、运行库 | 平台参数属性 | GNU/LLVM/Apple/Android/WASI | 保留默认驱动上下文 |
| SB 未完善：LTO、Sanitizer、分组、响应文件 | 跨操作配置、链接树、ResponseFileEncoder | CMake/Xmake/UBT/官方文档 | 不读取安装，不猜测未知版本能力 |

## 本地参考

- SB：`D:\workspace\project\ExtremeEngine\engine\tools\SB`
- CMake：`D:\workspace\project_git\CMake`，`Modules/Compiler/{GNU,Clang}.cmake`、`Modules/Platform/Windows-MSVC.cmake`、`Darwin.cmake`
- Xmake：`D:\workspace\project_git\xmake`，`xmake/modules/core/tools/{gcc,clang,cl,ar,llvm_ar,emcc}.lua`
- UBT：`C:\Program Files\Epic Games\UE_5.8\Engine\Source\Programs\UnrealBuildTool`，Clang、Windows、Mac、IOS、Android ToolChain 参数函数
- [Emscripten settings](https://emscripten.org/docs/tools_reference/settings_reference.html)
- [MSVC response files](https://learn.microsoft.com/en-us/cpp/build/reference/at-specify-a-compiler-response-file?view=msvc-170)

## 验收边界

固定字段合并、来源、快照、生成 API 和传输编码由 CX 黑盒单元测试验证。具体平台参数按上表 review。现有 AutoTest 场景调用生产 Driver；本地不运行 Setup/AutoTest，不新增 CI 场景。

## 文件组织与扩展

集合实现、固定属性声明、来源和生成结果各自集中维护；编译、链接及归档领域的枚举与短记录合并到所属 `*Options.cs`。内部按共同规则、编译（含 RC）、链接、归档和生成上下文分工，不按每个参数或平台建立小文件。

`ArgumentSet.Properties.cs` 是字段的唯一声明来源。内部 `[Argument]` 标记的可空只读 partial 属性生成公开 `ArgumentField`、属性实现、`WithXxx` 和 `WithoutXxx`；列表与映射还生成 `AppendXxx`、`RemoveXxx`。生成器拒绝外部配置类型、不支持的属性类型、重复字段及成员冲突。新增字段还需登记操作适用性并完成 Driver 解释与黑盒测试。

```csharp
using Incant.CX.Arguments;

ArgumentSet common = new ArgumentSet(new Dictionary<string, string> { ["configuration"] = "library" })
    .WithLanguage(Language.Cpp)
    .WithStandard("c++17")
    .WithIncludes([ArgumentPath.Resolve("include", absoluteProjectDirectory)]);
ArgumentSet compilation = common.WithInputs([sourcePath]).WithOutput(objectPath);
ArgumentGenerationResult result = new ArgumentDriver(Dialect.Clang, Operation.Compile)
    .Generate(compilation);
```

通过 `compilation.Standard` 等固定属性读取配置。未设置时返回 `null`；`WithXxx` 拒绝字段级 `null`，`WithoutXxx` 明确移除。宏映射中的 `null` 仍表示无值宏。`Merge` 要求标量相等，列表保序追加，宏和警告映射按名称合并并拒绝冲突；`Override` 只替换传入字段。序列保留顺序和重复；同一原始贡献的菱形导入只合并一次，主动追加则形成新贡献。移除标记、空集合、显式 false 和从未设置相互独立。新快照有新身份，未改字段仍追溯到原始集合；元数据不参与参数生成。

新增 C 系列配置需同时登记操作适用性及解释函数，明确“其他操作无关”和“当前操作不支持”。不能只加入属性而静默丢弃请求。原始参数按语言、操作、方言和位置筛选，每项始终是一个 token；链接树保留嵌套分组和重复库。GNU Windows 的定向导出通过显式 `.def` 文件输入表达，不能套用 ELF 的导出选项。

## 具体 review 结论

- MSVC 的 PCH 创建必须声明对象产物；GCC 使用 `<header>.gch`，Clang 支持只指定 PCH artifact 的消费方式。调度和文件存在性由调用方处理。
- 优化与 LTO 独立；GNU/LLVM LTO 归档需要调用方确认工具能力，MSVC `/GL` 与 MSVC linker 配对，Clang LTO 与 lld-link 配对。Apple LTO 调试明确保留中间对象路径。
- Windows CRT、编译器支持库、C++ 标准库和静态/动态链接分开设置；独立 static libc++ 需要显式提供 libc++/ABI 等运行库输入，排在用户输入之后。Android 保留驱动的 `-static-libstdc++` 选择方式。
- MSVC Dynamic Debug 按编译器版本、x64、调试信息、LTO 和 ASan 组合检查，同时参与编译、归档与链接。其约束依据 [MSVC Dynamic Debug 文档](https://learn.microsoft.com/en-us/cpp/build/reference/dynamic-deopt?view=msvc-170)。
- clang-cl 输入使用 `/Tc`、`/Tp` 操作数，保留 Unix 绝对路径和输入后的选项；依据 [Clang command syntax](https://clang.llvm.org/docs/UsersManual.html#clang-cl)。
- Emscripten main/side module 在编译和链接同时配置；线程、异常、SIMD、内存、栈和模块导出相互独立。WASI 默认与 EH 模式明确隔离，WASI threads 必须声明匹配 triple；不提供不稳定的 WASI 共享库必需能力。
- 响应文件编码与文件创建分离。GNU、Microsoft、LLVM Windows 各自处理引号、反斜杠、空参数和编码；拒绝换行、NUL 和嵌套响应引用。AutoTest 保存逻辑参数和实际传输参数，Python launcher 前缀留在响应文件之外。
- AutoTest 只转换已经解析的驱动上下文与资源组。原生 Linux 默认 sysroot 不被平台资源根覆盖；Windows/Apple/Bundle 的组合和资源顺序仍由既有 Resolve 决定。归档重建只清理当前动作声明、工作目录内的产物。

本轮未迁入 SB 的 Target 代理、引擎宏及隐藏优化/内存默认值；对应能力通过集合合并、显式定义和固定属性表达。未新增 PCH/LTO CI 场景、C++ Modules/BMI 调度或 PGO 训练设施。

`ArgumentField` 仅用于存在性、移除、筛选和来源定位，不能注册新字段。集合 ID 与只读元数据只用于追溯，Driver 不依赖 Finder、SDK、环境或进程。原有公开通用键、策略工厂和自定义归约接口已删除。
