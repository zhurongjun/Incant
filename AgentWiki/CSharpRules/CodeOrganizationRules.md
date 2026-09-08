# C# 代码组织规则

## 文件与命名空间

- 通常一个文件只包含一个主要类型，文件名与主要类型名一致。
- `using` 指令放在命名空间外部，`System` 命名空间在前，其余按字母顺序排列。
- 单一命名空间的文件优先使用文件作用域命名空间。

```csharp
using System.Collections.Generic;

namespace Incant.Build;

public sealed class TargetGraph
{
}
```

## 类型与成员设计

- 明确写出非接口成员的可访问性。
- 修饰符按 `.editorconfig` 指定的顺序排列，可访问性修饰符在最前。
- 默认将不需要继承的内部或私有类声明为 `sealed` 或 `static`。
- 优先使用属性表达对象状态，避免暴露可变公开字段。
