# 检视报告

## 概要

本次检视范围：中文窗口名拼音搜索需求（docs/features/260927-pinyin-search/260927-pinyin-search.plan.md），变更文件为 `Core/Matchers/PinyinMatcher.cs`（新增）、`Core/WindowFilterer.cs`（Score() 接入）、`Core/Core.csproj` 与 `Core/packages.config`（引入 NPinyin）、`Core.UnitTests/PinyinMatcherTests.cs`（新增 9 用例）及测试工程注册。整体评价：需求实现完整、映射逻辑经推演与实测均正确，无阻塞性代码缺陷；主要风险集中在**性能（每击键全量重算拼音别名）**、**NPinyin 许可证不明确与 GPL-3.0 项目的合规性**，以及**测试对 Score 与高亮映射断言不足**，均为建议级。

## 需求对齐

- **功能满足**：实测 NPinyin `GetPinyin("微信")="wei xin"`、`GetInitials("微信")="WX"`，与实现一致；`wx`/`weixin` 均可命中"微信"。非中文标题回退、中文 pattern 不处理、空 pattern/null 处理均已实现。
- **与计划差异（可接受）**：计划第 2 步称"复用 StartsWithMatcher + ContainsMatcher"，实现改为手写 `IndexOf` + `index==0` 判定前缀/包含并映射分数。语义等价且更轻量，无问题，但建议在计划文档中标注实现偏差。
- **未覆盖场景（记录）**：① 多音字（实测 "重庆"→`zhong qing`、"行"→`xing`，输入 `chongqing`/`hang` 无法匹配对应窗口，为 NPinyin 单音固定映射限制）；② 中英混合 pattern（如 "微xin"）两类向量均不命中（需求未要求，可接受）；③ 跨窗口标题多音字别名增强（后续项）。

## 阻塞问题

无。

## 建议修改

| ID | 位置 | 问题 | 建议 |
| --- | ---- | ---- | ---- |
| S1 | `Core/Matchers/PinyinMatcher.cs:115` | `CjkChar.IsMatch(ch.ToString())` 对每个字符新建 string 再跑正则。每击键 × 每含中文窗口 × 每字符一次分配 + 正则调用，随窗口数/标题长度线性放大，是全链路最热路径。 | 改为纯 char 区间判断：`if (ch >= '\u4E00' && ch <= '\u9FFF')`，消除逐字符字符串分配与正则调用。`HasChinese`（:27）保留正则一次扫全串即可。 |
| S2 | `Core/Matchers/PinyinMatcher.cs:50-54` | `candidates` 数组在每次 Evaluate 时**同时构建全拼与首字母两个别名**，即使全拼第一次就命中，首字母别名也白算（每击键重复开销）。 | 改为惰性顺序：先 `BuildFullAlias` 匹配，未命中再 `BuildInitialsAlias`；或仅在需要时构建，避免二倍拼音转换开销。 |
| S3 | `Core/Matchers/PinyinMatcher.cs:43` | pattern 含中文时未显式跳过，仅靠 alias 中恰好不含该中文而自然不命中。当 input 含 pattern 的汉字原文（如 `Evaluate("微信微", "微")`，alias=`weixin微`）时 PinyinMatcher 会命中纯汉字并计分，与 ContainsMatcher 重复计分，语义上"拼音匹配器匹配了汉字"。现有测试 `ChineseInput_NotHandledByPinyinMatcher` 只覆盖了 alias 无对应汉字的情形，掩盖了此边界。 | 在 pattern 空值检查处（:43 附近）增加 `if (HasChinese(pattern)) return noMatch;`，使"pattern 含中文即回退"成为显式契约，与测试意图一致。 |
| S4 | `Core/Matchers/PinyinMatcher.cs:58` | `System.StringComparison` 完全限定名与文件风格（顶部已集中 using）不符，属可读性噪音。 | 加 `using System;` 后改用 `StringComparison.OrdinalIgnoreCase`。 |
| S5 | `Core/Matchers/PinyinMatcher.cs:36-47, 70` | 三个 noMatch 分支重复 `noMatch.StringParts.Add(new StringPart(input))` 与 `new MatchResult()` 样板。 | 提取 `private static MatchResult NoMatch(string input)` 辅助方法，统一回退路径。 |
| S6 | `Core.UnitTests/PinyinMatcherTests.cs:29-32, 44-48` | 首字母/全拼用例仅断言 `Matched`，未断言 `Score`。Score 是窗口排序的关键输入（`WindowFilterer.cs:65` 按 Score 求和排序），未覆盖即未保护排序契约。 | 补 Score 断言：`weixin`→4（全拼前缀）、`bdllq`→3（首字母前缀）、`liu` 含"浏"→2（全拼包含）、`wx` 含中→1（首字母包含）。 |
| S7 | `Core.UnitTests/PinyinMatcherTests.cs:60-66` | `MixedChineseEnglish_Chrome` 只断言 Matched，未断言高亮输出。**别名→原文区间映射是本实现最核心、最易错的逻辑**（测试 `PartialFullPinyin` 已覆盖中间字映射，但混合段的映射无断言）。 | 补 `XamlText` 断言：`Evaluate("谷歌Chrome浏览器", "chrome")` 应为 `谷歌<Bold>Chrome</Bold>浏览器`；另补 `Evaluate("谷歌Chrome浏览器", "guge")` → `<Bold>谷歌</Bold>Chrome浏览器`。 |
| S8 | `Core.UnitTests/PinyinMatcherTests.cs`（整体） | 测试缺口：无大小写混合 pattern（如 "WeiXin"）、无 `input=null`（走 HasChinese=false 分支，确认无 NRE 且返回空 StringParts）、无 pattern 跨两个汉字边界的映射用例（如 `"weixinliu"` 命中部分拼音段）。 | 补上述三组用例；其中跨字映射用例直接检验 `Map[index + pattern.Length - 1]` 的边界正确性。 |
| S9 | `Core/packages.config:3`、`Core/Core.csproj:33-35` | **NPinyin 许可证不明确**：nuspec 无 license/licenseUrl 字段（版权 AWangSoft Inc. 2011），Google Code 原项目 `code.google.com/p/npinyin` 与 GitHub 导出仓库（cjjer/npinyin、Cat7373/npinyin）均无 LICENSE 文件，即"无许可证 = 保留所有权利"。本项目为 GPL-3.0（`WindowFilterer.cs:1-19` 头部声明），以二进制形式并入并分发存在 GPL 合规风险，无法确认合法再分发。 | ① 若项目公开分发：**必须先解决**，否则应视为阻塞——联系 NuGet 所有者确认授权，或替换为有明确许可证的同 API 库（实测 GitHub 上 WuTong1995/NPinyin、KingLion.WebUtils.NPinyin 为 MIT，namespace/API 相同，改动量小）；② 若仅本地个人使用：在计划文档中记录该决定即可。 |
| S10 | `Core/WindowFilterer.cs:82,90` | 每窗口每击键 `new PinyinMatcher()` 且无别名缓存：对含中文标题的窗口，每次击键全量重算全拼+首字母别名（实测 `GetPinyin` 为逐字字典查找）。当前窗口规模（几十个）可接受，但浏览器多标签等含中文标题较多场景下为 O(字符总数) 的重复计算。 | 短期：落实 S1/S2 即可将单次开销降到可忽略。中期（可选）：在 `FilterResult`/上下文层按「窗口标题 → 别名+映射」缓存，标题不变则复用（WPF UI 单线程，无并发问题）。 |

## 非阻塞问题

| ID | 位置 | 问题 | 建议 |
| --- | ---- | ---- | ---- |
| N1 | `Core/Matchers/PinyinMatcher.cs:50-68` | 多音字：NPinyin 固定单音映射（实测 "重庆"→`zhong qing`、"行"→`xing`），输入 `chongqing`/`hang` 匹配不到对应窗口。属库能力边界，非本实现缺陷。 | 若后续需要，可维护小型多音字白名单（如 重/行/乐/长/会/朝 等），为这些汉字生成第二候选别名。记录于需求文档即可。 |
| N2 | `Core/Matchers/PinyinMatcher.cs:17-20` | 首字母包含命中给 1 分，与 `IndividualCharactersMatcher`（任意字符散落命中）同分；首字母包含语义上更精确。单字母搜索时大量中文窗口以首字母前缀 3 分沉入原文 Contains(2) 与 StartsWith(4) 之间，排序是否贴近用户直觉需实机观察。 | 观察真实 UX 后调整分数表；当前无需改动。 |
| N3 | `Core/Matchers/PinyinMatcher.cs:58` | 中英混合 pattern（如 "微xin"）全拼/首字母 alias 与原文 matcher 均不命中（"微"是汉字、"xin"非连续原文），存在漏匹配窗口。 | 需求未要求支持，记录备忘；如需可后续支持"原文+别名双序列混合匹配"。 |
| N4 | docs/features/260927-pinyin-search/ | 该需求目录仅有 plan.md，按项目文档规范缺 validation/e2e 记录（验证结论目前仅体现在本审查上下文）。 | 检视通过后补齐 validation 文档，记录实机验证结果（含多音字限制演示）。 |
| N5 | 工作区其他未提交改动（`Core/Core.csproj` TF v4.5→v4.8、`Core.UnitTests` NUnit 2.6.3→4.6.1、`WindowFiltererProcessFilterTest.cs` 注册、Switcheroo 主工程大量改动等） | 与本次 Changes 描述范围重叠但非本次需求产物，提交时易混入无关变更。 | 提交时按逻辑拆分（本次仅 PinyinMatcher 相关文件 + NUnit 升级若为支撑项应单独注明），避免审查/回滚粒度混乱。 |

## 准入结论

**结论**：`条件准入`

**说明**：无阻塞性代码缺陷，拼音匹配正确性与映射逻辑经推演、实测（NPinyin 行为已用反射核实）均成立，可进入交付；但 S1/S2（每击键性能）、S6/S7（Score 与高亮映射断言缺口）应在合并前或紧随合并后处理，S9（NPinyin 许可证）须在对外分发前明确决定，本条件准入以此为前提。
