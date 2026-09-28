# 中文窗口名拼音搜索 实施小结

日期: 2026-09-28

## 背景与需求

Switcheroo 是增量搜索任务切换器。原实现只能按**原文**匹配窗口标题/进程名，中文标题（如"微信"）无法用拼音检索。需求：中文名称的窗口可用**拼音全拼**（`weixin`）或**拼音首字母**（`wx`）搜索。

## 实施内容

| 文件 | 改动 |
| --- | --- |
| `Core/Matchers/PinyinMatcher.cs` | 新增 `IMatcher` 实现：对含中文的输入构建「拼音别名 → 原文字符索引」映射，用全拼/首字母别名匹配，命中后把区间映射回原文以正确高亮 |
| `Core/WindowFilterer.cs` | `Score()` 的 matcher 列表末尾接入 `PinyinMatcher`（取任一 matcher 命中即入选，按 Score 排序） |
| `Core/packages.config`、`Core/Core.csproj` | 引入 `WuTong.NPinyin 1.0.0`（MIT）；Core 目标框架 v4.5→v4.8 |
| `Core.UnitTests/PinyinMatcherTests.cs` | 12 条用例：全拼/首字母、前缀/包含、中英混合、跨字映射（`liulan`→`浏览`）、大小写、null、非中文回退 |

### 匹配与打分

别名匹配采用**惰性顺序**：先全拼、未命中再首字母。打分与既有 matcher 同为「前缀 4 / 包含 2」量级：

- 全拼前缀 4（`weixin`）
- 首字母前缀 3（`wx`、`bdllq`）
- 全拼包含 2（`liu`→`浏`）
- 首字母包含 1（`腾讯微信` 中段 `wx`）

### 依赖替换（许可证）

原 `NPinyin 0.2.6321.26573` 的 nuspec/仓库均无 LICENSE，本项目为 GPL-3.0 且对外分发（GitHub Releases），存在合规风险。替换为 **`WuTong.NPinyin 1.0.0`（NuGet 明确声明 MIT，源自 MIT 的 hotoo/pinyin）**：命名空间同为 `NPinyin`、程序集同为 `NPinyin.dll`、`net35` 目标（兼容 net48）。经实测，常用汉字转换结果与原库一致；唯一 API 差异是 `Pinyin.GetPinyin(char)`（静态）→ `Pinyin.ConvertToPinyin(string)`（实例），`PinyinMatcher` 内改一处即可。

## 关键决策

- **别名映射**：逐字符生成别名字符串并记录「别名字符 → 原文字符索引」，命中区间映射回原文，`StringPart` 高亮落在中文原字上。
- **中文 pattern 回退**：`Evaluate` 显式判定「pattern 含中文 → 不处理」，交由原文 matcher，避免与 `ContainsMatcher` 重复计分。
- **性能**：`CjkChar` 用 char 区间判断（去掉逐字符字符串分配+正则）；别名惰性构建（全拼命中即跳过首字母）。

## 验证

- 单元测试：**48 passed / 0 failed**（`Core.UnitTests`，NUnit Console 3.17）。
- 编译：VS2022 `MSBuild` 全量 Release 编译通过（Core / ManagedWinapi / Switcheroo）。
- 实机：见 `260927-pinyin-search.validation.md`。

## 已知限制

- **多音字**：`WuTong.NPinyin` / `NPinyin` 均为单音固定映射，如"重庆"→`zhongqing`，输入 `chongqing` 不命中（`zhongqing` 可命中）。属字库能力边界，非缺陷。

## 遗留

- 本轮修正一处**单元测试断言错误**（`Initials_WX_Matches`：`"微信"+wx` 属首字母**前缀**应为 `Score=3`，非 1），并补一条真正的"首字母包含"用例。
