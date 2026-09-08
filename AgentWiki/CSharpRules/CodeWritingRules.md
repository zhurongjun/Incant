# C# 代码书写规则

## 文本与布局

- 文本使用 UTF-8 编码。
- C# 代码使用 4 个空格缩进，不使用制表符。
- 文件末尾保留一个换行，删除行尾空白。
- 每行只写一条语句，每行只声明一个局部变量。
- 连续成员之间保留一个空行，不堆叠多个无意义的空行。

## 缩进、大括号与空白

- 使用 Allman 风格：左、右大括号各自独占一行，并与所属声明或控制语句对齐。
- `if`、`else`、`for`、`foreach`、`while`、`switch` 和 `using` 等控制结构默认使用大括号。
- `else`、`catch` 和 `finally` 放在前一个右大括号的下一行。
- 逗号后留空格，逗号前不留空格。
- 二元运算符两侧留空格；长表达式换行时，运算符放在新行开头。
- 方法名与左括号之间不留空格，参数列表括号内侧不留空格。

```csharp
if (target.IsEnabled)
{
    Build(target);
}
else
{
    Skip(target);
}
```

## 命名规范

| 元素 | 规则 | 示例 |
| --- | --- | --- |
| 命名空间、类、结构、枚举、委托 | `PascalCase` | `Incant.Build`, `TargetGraph` |
| 接口 | `I` + `PascalCase` | `IToolchain` |
| 方法、属性、事件 | `PascalCase` | `BuildTarget`, `OutputPath` |
| 类型参数 | `T` + `PascalCase` | `TTarget` |
| 普通参数、局部变量 | `camelCase` | `targetName`, `outputPath` |
| record 位置参数 | `PascalCase` | `record Target(string Name);` |
| 私有和内部实例字段 | `_camelCase` | `_targetGraph` |
| 私有和内部静态字段 | `s_camelCase` | `s_defaultOptions` |
| 线程静态字段 | `t_camelCase` | `t_currentContext` |
| 常量字段和局部常量 | `PascalCase` | `MaxRetryCount` |
| 公开或受保护字段 | `PascalCase`，但应尽量避免公开字段 | `DefaultCapacity` |

补充要求：

- 名称应描述用途，避免依赖类型信息的前后缀和无意义缩写。
- 除简单循环计数器等极小作用域变量外，不使用单字母名称。
- 接受通用缩写时仍按单词处理，例如 `HttpClient`、`JsonWriter`、`Id`。
- 异步且返回 `Task` 或 `ValueTask` 的方法通常使用 `Async` 后缀。
- 布尔名称应能读成条件，例如 `isEnabled`、`hasOutput`、`canBuild`。
- 标识符不得包含连续两个下划线；该形式保留给编译器生成代码。
- `[ThreadStatic]` 字段使用 `t_camelCase`；EditorConfig 无法依据特性可靠识别该规则，必须人工检查。

## 类型和局部变量

- 使用 C# 关键字而不是 BCL 类型名，例如使用 `string`、`int`、`bool`，不使用 `String`、`Int32`、`Boolean`。
- 仅当右侧表达式可以直接看出类型时使用 `var`，例如对象创建或显式转换。
- 当类型来自方法返回值、属性或复杂表达式且无法直接判断时，显式写出类型。
- 不在变量名中重复类型信息，应让名称表达业务含义。
- 能声明为 `readonly` 的字段应声明为 `readonly`。
- 除解决歧义外，不使用 `this.` 限定实例成员。

```csharp
var graph = new TargetGraph();
TargetResult result = graph.Build(target);
```

## 表达式

- 小而明确的只读操作可以使用表达式体；复杂逻辑使用完整块体。
- 使用对象初始化器、集合初始化器或集合表达式简化明确的初始化代码。
- 优先使用模式匹配、空传播、空合并和 switch 表达式等清晰的现代语法。
- 使用 `nameof` 代替手写的成员或参数名称字符串。
- 只有当类型在左侧明确可见时才使用目标类型 `new()`。

## 控制流、异常和异步

- 优先使用提前返回减少不必要的嵌套，但不要为了减少行数牺牲可读性。
- 只捕获能够处理的具体异常；不得无条件吞掉异常。
- 重新抛出当前异常时使用 `throw;`，不要使用 `throw exception;`。
- 参数校验应抛出准确的异常并使用 `nameof` 指明参数。
- I/O 操作优先提供异步路径，并正确传播 `CancellationToken`。
- 不使用 `.Result` 或 `.Wait()` 阻塞异步代码，除非调用边界明确要求且不会导致死锁。
- 资源应通过 `using` 声明、`using` 语句或明确的所有权模型释放。

## 注释和文档

- 注释解释原因、约束和权衡，不复述代码已经表达的行为。
- 简短说明使用 `//`，注释文本以大写字母开始并以句号结束。
- 注释放在相关代码之前，避免无必要的行尾注释。
- 对公开 API 使用 XML 文档注释，说明契约、参数、返回值、异常和重要副作用。
- 修改代码时同步更新相关注释；过期注释必须删除或修正。
- 不保留大段被注释掉的旧代码，历史应由版本控制保存。
