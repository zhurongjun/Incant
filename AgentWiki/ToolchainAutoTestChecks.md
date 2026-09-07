# Toolchain AutoTest 验收条件审查

AutoTest 验证真实安装能通过 Finder 找到并完成库链。环境清单仍为 schema 1；AutoTest 报告为 schema 2。查询契约由受控 Core 黑盒测试负责，不能以真实 runner 的偶然布局定义生产契约。

| 原检查 | 归类 | 当前处理 |
| --- | --- | --- |
| 清单存在、JSON/schema/Profile、非空结构、唯一 ID、合法环境变量名、实际宿主 OS/架构 | 实际使用必需 | Preflight，配置错误返回 2 |
| runner 标签、镜像版本、清单声明的宿主字符串 | 仅报告 | 以实际进程宿主为准 |
| 所有安装、Node/Python/Wasmtime 必须在 Preflight 可用 | 无可靠依据而删除 | 每个受控安装在 Discover 检查；运行时在其场景检查，其他候选继续 |
| 来源 URI、摘要、revision、运行时精确 patch | 仅报告 | 安装器保留下载校验；AutoTest 不重复验证部署 provenance |
| 预装 VS、Windows SDK、Xcode、Homebrew 固定版本和路径 | 无可靠依据而删除 | 全局 Finder 自动发现，覆盖独立安装；必需宿主家族不能全部跳过 |
| 主动部署版本范围与显式安装根 | 实际使用必需 | 显式 Finder 查询应用 Profile 的范围，不把清单实际 patch 当新约束 |
| 安装级种类、精确版本、无约束、显式查询的全快照比较 | 已有单元测试承接 | 删除 DiscoveryConsistency；Core 测试验证查询筛选、来源合并及独立安装 |
| 不存在路径抛异常、错误版本/目标返回空 | 已有单元测试承接 | FindTools/FindSdk 的 FinderTests 及 CompilerProviderTests |
| 安装 Sources 必须非空/含 Explicit、Channel 已知且跨组件一致 | 仅报告 | 不参与使用验收 |
| 工具、运行时与被使用的 sysroot 路径存在 | 实际使用必需 | Resolve 的 BuildInputs 与具体解析器；构建期间消失则动作失败 |
| SDK 安装锚点必须覆盖所有资源、IsExternal 必须与嵌套关系一致 | 仅报告 | 归属由 Finder 确认；不能以目录嵌套替代组件所有权 |
| 每项资源存在、完整资源组、Framework/builtin/运行库全集 | 仅报告 | 检查库链使用的头文件、Windows 运行库和选中的 Apple sysroot；其他缺失不阻止场景 |
| SDK/ToolSet 的 Error 或 missing-resource 自动使候选失败 | 仅报告 | 诊断保留；按实际输入决定是否可以构建 |
| 目标/ABI/multilib/API 查询隔离 | 已有单元测试承接 | Core 不放宽查询；AutoTest 使用目标查询返回布局，保留 Android 有效 API 和变体参数 |
| AutoTest 再解析一套通用 triple 并比较 | 无可靠依据而删除 | 只保留生成 Apple 部署目标与平台命令所需的格式转换 |
| MSVC SDK/ToolSet 配对、Apple 开发环境一致、Bundle 同根 | 实际使用必需 | 集中的安装归属与平台解析器，部分最高版本不阻塞其他组合 |
| 宿主架构必须已知 | 无可靠依据而删除 | 优先原生和实测 Rosetta 架构，Unknown 进入实际使用验证 |
| Linux x64 身份直接允许 x86 执行 | 无可靠依据而删除 | 仅当前架构和实测能力允许执行，其余仅构建 |
| 编译器驱动链接仍要求独立 linker | 无可靠依据而删除 | Driver 场景使用编译器；MSVC/lld-link 场景明确执行所选 linker |
| ranlib 必须存在 | 无可靠依据而删除 | 存在时执行索引；否则由 ar rcs 建立索引 |
| 重复资源、路径类型、搜索顺序、ATL/MFC/Windows 目录排列 | 已有单元测试承接 | SDK 快照写入报告，资源快照/搜索顺序等受控测试保留；真实编译仍使用 Finder 的资源顺序 |
| Emscripten 默认/PIC 目录不得有任何交集 | 无可靠依据而删除 | 目标变体隔离仍由 Finder 保证；实际默认与 side/main-module 场景分别构建，不禁止合法共享资源 |
| 编译/归档/成员列表/共享库/可执行程序、非空产物、固定输出与退出码 | 实际使用必需 | 保留固定资产与串行调度；开始使用后失败不得回退安装 |
| 必需安装及宿主家族覆盖、所有完整安装的真实失败 | 实际使用必需 | 覆盖结果独立汇总，必需集合为空不能通过 |
| 取消、快照不可变、每次查询新发现、相同版本不同安装不合并、wrapper 调用入口 | 已有单元测试承接 | Core 黑盒用例与新增独立安装、名称解析用例；AutoTest 同一运行内复用 SDK 查询 |

## 扩展方式

- 预装工具链不在 Setup 清点版本。增加必需家族时更新 Shared 的 RequiredHostFamilies 与相应场景。
- 主动部署版本登记在 Shared 的 Installations；下载 Bundle 同时登记 BundleCatalog 的发布参数及摘要。同家族版本复用现有安装器，apt 包名与 Linuxbrew formula 从版本声明生成。
- 固定库链在 Build/*LibraryChain.cs 中声明；命令参数在 *BuildAdapter.cs 中生成，共用静态库步骤在 LibraryChainScenario。
- SerialBuildScheduler 只执行明确的 BuildAction，不解析 Emscripten/Python 入口、不选择或更换安装。
- schema 2 中快照集中在 snapshots，场景通过 ToolSet/SDK ID 和布局索引引用；coverage 说明必需家族或安装是否满足。

## 外部 CI 验收

四个 Profile 分别检查：预装版本变化、部分安装跳过、独立安装覆盖、必需覆盖缺口失败、构建失败不回退、Emscripten 默认/PIC、WASI 执行与报告解释性。Windows 本地跳过的 Unix 合成安装用例由 Unix 单元测试环境验证。不得将本轮本地编译和单元测试等同于四环境 AutoTest 已通过。
