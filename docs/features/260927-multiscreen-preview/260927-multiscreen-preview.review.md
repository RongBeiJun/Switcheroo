# 检视报告

## 概要

检视范围：docs/features/260927-multiscreen-preview 计划的 6 项需求（依赖升级、页面放大、动态过滤标签、多屏定位、按屏过滤、悬停缩略图），涉及 Switcheroo/MainWindow.xaml、MainWindow.xaml.cs、新增 MultiMonitorHelper.cs / DwmThumbnail.cs / ThumbnailPreviewWindow.cs，以及 csproj/app.config/packages.config 的依赖变更。整体实现与计划高度一致，代码可读性与结构良好，多屏换算与 DWM 缩略图的关键边界（最小化窗口位置、销毁后注销、首次 Show 后注册）处理到位；存在少量与计划不符或需修正的项，无阻塞级缺陷。

## 需求对齐

| 需求 | 状态 | 说明 |
|---|---|---|
| 1. 依赖升级 | ✅ | Newtonsoft 13.0.4（HintPath net45、Reference 13.0.0.0）、NUnit 4.6.1（net462）、MSBuildTasks 1.5.0.235（按计划仅同步 packages.config）、supportedRuntime v4.8 |
| 2. 页面放大 | ⚠️ 部分 | 字号/行高/列宽/MinWidth/图标均到位；**帮助文本 FontSize 仍为 10，未按计划改 12**（S4） |
| 3. 动态过滤标签 | ⚠️ 部分 | 动态前 10、Distinct+OrderBy、点击/Alt+数字切换、Alt+0 清除均实现；**WrapPanel 在 SizeToContent 下实际不换行**（S1） |
| 4. 多屏定位 | ✅ | 鼠标所在屏 DIP 居中、Border.MaxHeight 按屏高；与计划差异：未用 GetDpiForMonitor，改系统 DPI 换算（有注释说明，见 N4） |
| 5. 按屏过滤 | ✅ | 中心点判断、最小化用 rcNormalPosition、其他用 GetWindowRect，与注释一致 |
| 6. 悬停缩略图 | ✅ | DWM thumbnail、letterbox、120ms 延迟隐藏、Hide 保留注册、源销毁注销，前轮 expert 发现的问题均已修复 |

行为变更（Alt+D6..D0 由 SwitchToIndex 改为 switchProcessFilter）与计划一致；SwitchToIndex 按计划保留但现为死代码（N2）。

## 阻塞问题

无。

## 建议修改

| ID | 位置 | 问题 | 建议 |
| --- | ---- | ---- | ---- |
| S1 | MainWindow.xaml:86（WrapPanel）+ MainWindow.xaml.cs:614-628（CenterWindow） | 窗口 `SizeToContent="WidthAndHeight"` 使内容按无限宽测量，WrapPanel 不会换行，标签只能单行排布。进程名多且长（10 个标签）时窗口宽度可远超屏幕，CenterWindow 会产生负 Left/Top，窗口部分落出屏外。计划明确要求"标签自动换行、窗口自适应"，当前实现未达成且存在出屏隐患 | 在 CenterWindow 中与 MaxHeight 对称地设置 `Border.MaxWidth = dipBounds.Width`：既约束窗口不超屏，又让 WrapPanel 在受限宽度下真正换行。需实机确认 |
| S2 | MainWindow.xaml.cs:932-937（ListBox_MouseLeave）+ 98-106（timer Tick） | 鼠标从列表移入缩略图窗口后，ListBox 触发 MouseLeave，120ms 后预览被隐藏——与注释"避免鼠标经过缩略图窗口时反复显示/隐藏"的意图相悖，用户无法停留查看预览 | Timer Tick 中先判断光标是否落在预览窗口边界内（或仍在 ListBox 上），是则跳过隐藏并重启计时；仅当光标离开列表和预览窗口后才隐藏 |
| S3 | MainWindow.xaml.cs:954-963 | 预览窗口回摆只检查右/下边缘：鼠标位于屏幕最左/最上时，翻转后 left/top 仍为负值，预览窗口落出屏幕 | 对 left/top 增加下界钳制（`Math.Max(dipBounds.X, ...)` 等），确保翻转后仍完全位于屏幕内 |
| S4 | MainWindow.xaml:52,56,62,68,74 | 帮助文本 FontSize 仍为 10，计划"页面放大"项明确要求 10→12 | 按计划统一改为 12，与放大后的整体比例一致 |
| S5 | MainWindow.xaml.cs:167-180（switchProcessFilter） | `switchProcessFilter(-1)` 以 `new TextBlock()` 假参数复用 TbProcessFilter_MouseDown，依赖 Tag==null 的隐式约定，事件处理器被当命令用，可读性差 | 抽取 `SetProcessFilter(string process)` 方法，事件处理器与快捷键共用；switchProcessFilter(-1) 直接调 `SetProcessFilter("")` |

## 非阻塞问题

| ID | 位置 | 问题 | 建议 |
| --- | ---- | ---- | ---- |
| N1 | app.config:47-49、Settings.settings:38、Settings.Designer.cs:161-166 | `ProcessFilters` 设置已无任何代码读取，成为死配置（残留旧值 chrome,code,idea,explorer2，易误导） | 后续从 Settings 中移除该设置并重新生成 Designer |
| N2 | MainWindow.xaml.cs:300-309 | SwitchToIndex 按计划保留但无绑定，为死代码 | 后续删除，或加注释说明保留原因 |
| N3 | DwmThumbnail.cs:66-71 | Update 中设置了 opacity=255 但 dwFlags 未含 DWM_TNP_OPACITY，opacity 实际被忽略（默认即 255），不影响行为 | 去掉 opacity 字段赋值或补上 flag，避免误导 |
| N4 | MultiMonitorHelper.cs:19-25 | 与计划差异：按系统 DPI 换算而非 GetDpiForMonitor 按屏取 DPI。对本应用（未启用 PerMonitorV2 的系统 DPI 感知进程）自洽，但混合 DPI 多屏下，150% 屏上的预览窗口内容由 DWM 拉伸，缩略图会略小于窗口；该取舍有注释说明，需确认接受 | 维持现状或升级为 PerMonitorV2 时同步重写换算逻辑 |
| N5 | MainWindow.xaml.cs:627-628 | CenterWindow 在 SizeToContent 切换后立即读 ActualWidth/ActualHeight，此刻布局可能尚未更新（动态标签导致宽度变化时激活瞬间可能短暂偏置，且之后不再校正） | 观察是否可见；必要时在布局完成后（Dispatcher 后置或 LayoutUpdated）再次定位 |
| N6 | MainWindow.xaml.cs:501,508,880 | LoadData→switchProcessFilter(-1)→TextChanged 链依赖 `_foregroundWindow` 非空。该链基线已存在（非本轮引入），但托盘菜单触发的 LoadData（如"Alphabetical Sort"）在从未激活过 Switcheroo 时存在 NRE 风险 | 顺手加固：`_foregroundWindow` 为空时回退取 `SystemWindow.ForegroundWindow` |

## 准入结论

**结论**：`条件准入`

**说明**：无阻塞问题，核心功能（动态标签、多屏定位、按屏过滤、悬停缩略图、依赖升级）实现完整，Release 编译通过、32 个单测全通过；但存在换行失效/窗口超宽（S1）、预览隐藏时机（S2）等应修复项，建议在合并前处理 S1、S2，其余（S3-S5、N1-N6）在后续迭代处理。
