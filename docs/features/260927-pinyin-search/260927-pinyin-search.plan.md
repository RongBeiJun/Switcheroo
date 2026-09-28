# 中文窗口名拼音搜索 计划

日期: 2026-09-27

## 背景与需求

Switcheroo 是增量搜索任务切换器。当前对中文窗口名（如"微信"）只能按汉字搜索，用户希望可以输入拼音全拼或首字母来搜索，例如输入 `wx` / `weixin` 能匹配"微信"。

## 现状分析

- 匹配链路：`MainWindow.RefreshFilter()` → `WindowFilterer.Filter(query)` → 对窗口标题/进程名分别跑 4 个 matcher（StartsWith/Contains/SignificantCharacters/IndividualCharacters），全部基于**原文**字符串匹配。
- 无任何拼音转换能力。无法对"微信"这类纯中文标题匹配 `wx`。

## 方案

### 1. 引入 NPinyin 库

- NuGet 包 `NPinyin 0.2.6321.26573`（net2.0，兼容本项目 net4.8，无依赖）。
- 添加到 `Core/packages.config` + `Core.csproj` Reference。
- 实测输出：
  - `GetPinyin("百度浏览器")` → `bai du liu lan qi`（空格分隔全拼）
  - `GetInitials("百度浏览器")` → `BDLLQ`（大写首字母）
  - 非中文（如 "Chrome"）会被逐字符转拼音，`GetPinyin` 会拆成 `C h r o m e` —— **不可直接用作匹配串**，需过滤非中文。

### 2. 新增 `PinyinMatcher`（Core/Matchers/PinyinMatcher.cs）

继承 `IMatcher`，`Evaluate(input, pattern)` 中对**原文含中文**的标题/进程名：
1. 提取输入中的中文字段，分别生成**无空格全拼**（`baiduliulanqi`）与**小写首字母**（`bdllq`）；非中文字符保留原样拼接到别名串。
2. 用两种别名分别跑 `StartsWithMatcher` + `ContainsMatcher`（复用现有 matcher，将 pattern 与别名匹配）。
3. 命中则 `Matched=true` 返回，Score 与现有 matcher 同级（StartsWith 命中给高优先级前缀匹配分）。

**关键点**：PinyinMatcher 匹配的是**别名串**而非原文，但输出 StringParts 必须对应**原文**，高亮才正确。因此：
- 内部构造别名时建立「别名字符 ↔ 原文字符索引」映射。
- 命中时把别名中匹配区间映射回原文区间，仅对匹配到的中文字段标记 IsMatch=true，其余原样输出。

### 3. 接入 `WindowFilterer`

在 `Score()` 的 matcher 列表末尾加入 `new PinyinMatcher()`，复用既有流程（取任一 matcher Matched 即入选，按 Score 排序）。无需改主窗口代码。

## 文件改动清单

- `Core/packages.config`：新增 NPinyin。
- `Core/Core.csproj`：新增 Reference + 编译项 `Matchers/PinyinMatcher.cs`。
- `Core/Matchers/PinyinMatcher.cs`：新增。
- `Core/WindowFilterer.cs`：Score 增加 PinyinMatcher。
- `Core.UnitTests/PinyinMatcherTests.cs`：新增测试（全拼/首字母/混合中英/非中文回退）。

## 验证

1. `nuget restore` 成功；Release 编译通过。
2. 单测：新增拼音匹配用例 + 既有 34 测试全绿。
3. 实机（用户）：打开含中文标题的窗口，输入拼音全拼/首字母可过滤到该窗口，且高亮正确。