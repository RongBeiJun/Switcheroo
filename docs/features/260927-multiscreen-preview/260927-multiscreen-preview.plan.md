# 多屏预览增强 + 依赖升级 计划

日期: 2026-09-27

## 背景与需求

用户在使用 Switcheroo 时提出以下需求：

1. **依赖升级**：Newtonsoft.Json 12.0.3 有高危漏洞（NU1903, GHSA-5crp-9r3c-p9vr），升级到无漏洞版本；其他可更新依赖一并升级。
2. **页面放大**：窗口内容整体适当放大。
3. **程序过滤标签动态化**：顶部 `1.chrome 2.code ...` 固定配置过滤标签，改为从当前已开启程序动态生成并自适应。
4. **多屏定位**：Switcheroo 窗口能显示在任何屏幕，激活时定位到鼠标所在屏幕。
5. **按屏幕过滤**：窗口列表只显示当前屏幕内的应用。
6. **悬停缩略图**：鼠标指向列表项时，在旁边显示该窗口的缩略图。

## 现状分析

- 目标框架 v4.5 → 已升级到 v4.8（见上一轮构建），`app.config` 中 `supportedRuntime` 仍为 v4.5，需同步。
- `CenterWindow()` 硬编码 `SystemParameters.PrimaryScreenWidth/Height`（主屏），不支持多屏。
- 窗口列表来自 `WindowFinder.GetWindows()` 全部顶层窗口，未按屏幕过滤。
- 顶部过滤标签 `SetUpProcessFilter()` 从 `Settings.Default.ProcessFilters`（`chrome,code,idea,explorer2`）读取固定列表；Alt+1..5 切换过滤、Alt+6..0 为 SwitchToIndex（与过滤标签不一致）。
- 无任何 DWM thumbnail / 屏幕 / 鼠标位置相关代码。
- `SystemWindow.Position`（`GetWindowPlacement` 恢复位置）可用于最小化窗口的位置判断。

## 依赖升级方案

| 包 | 现版本 | 目标版本 | 说明 |
|---|---|---|---|
| Newtonsoft.Json | 12.0.3 | 13.0.4 | 修复 GHSA-5crp-9r3c-p9vr；项目仅用 `JsonConvert.SerializeObject`，兼容 |
| NUnit | 2.6.3 | 4.6.1 | 测试用 `Assert.That(x, Is.EqualTo(y))` 语法，NUnit 4 兼容；packages.config targetFramework 改 net48 |
| MSBuildTasks | 1.4.0.65 | 1.5.0.235 | 未被任何 csproj 引用，仅同步 packages.config |

同步修改：`Switcheroo.csproj` 中 Newtonsoft HintPath、`Core.UnitTests.csproj` 中 NUnit HintPath、`app.config` 中 supportedRuntime → v4.8、各 packages.config targetFramework → net48。

## 功能设计

### 1. 页面放大

- 搜索框 FontSize 15 → 20，搜索框 Padding 增大。
- 列表项字号（继承默认 12）→ 显式 15，图标 19 → 26，行 Padding `0,5` → `0,8`。
- 进程过滤标签 FontSize 17 → 20。
- 帮助文本 FontSize 10 → 12。
- 窗口 MinWidth 542 → 800；列表三列宽 22/400/106 → 30/560/160。
- 列表行高与缩略图区比例协调。

### 2. 程序过滤标签动态化

- 移除 `Settings.Default.ProcessFilters` 读取，改为在 `LoadData()` 时从 `_unfilteredWindowList` 提取去重 `ProcessTitle` 生成标签。
- 标签用 `WrapPanel` 自动换行，ProcessPanel 高度自适应（去掉固定 Height=20），窗口 SizeToContent 自适应。
- 显示前 10 个（Alt+1..9,0 一一对应），点击标签或 Alt+数字过滤，再点/再按清除。
- **行为变更**：Alt+D1..D9,D0 全部映射到 `switchProcessFilter(index)`，替代原 Alt+D6..D0 的 `SwitchToIndex`（与过滤标签语义一致）。`SwitchToIndex` 方法保留但不绑定快捷键。

### 3. 多屏定位（激活屏幕 = 鼠标所在屏幕）

新增 `MultiMonitorHelper`（Switcheroo 项目内静态类）：

- `GetMouseScreen()`：`Cursor.Position` + `System.Windows.Forms.Screen.FromPoint`。
- `GetDpiForScreen(Screen)`：P/Invoke `shcore.GetDpiForMonitor`（取目标屏真实 DPI，正确处理混合 DPI）。
- `GetScreenDIPBounds(Screen)`：物理像素 Bounds → DIP（`dip = px * 96 / dpi`），供 WPF 窗口 `Left/Top/Width/Height` 使用。

`CenterWindow()` 改为：以激活时鼠标所在屏幕为中心定位窗口；`Border.MaxHeight` 用该屏幕 DIP 高度。激活路径（hotkey / Alt+Tab / 托盘点击 / 启动）统一调用，激活瞬间记录 `_activeScreen`。

### 4. 按屏幕过滤窗口

- `LoadData()` 中过滤 `_unfilteredWindowList`：仅保留窗口 `Position`（恢复位置，兼容最小化窗口）中心点落在 `_activeScreen` DIP Bounds 内的窗口。
- 进程过滤标签基于过滤后的列表生成（保证标签与列表同屏一致）。

### 5. 悬停缩略图（DWM Thumbnail）

采用 DWM 实时缩略图（任务栏预览同款 API），被遮挡/最小化也能显示，非静态截图。

新增文件：

- `DwmThumbnail.cs`：P/Invoke `dwmapi.DwmRegisterThumbnail / DwmUpdateThumbnailProperties / DwmUnregisterThumbnail`，`DWM_THUMBNAIL_PROPERTIES` 结构体。
- `ThumbnailPreviewWindow.cs`：无边框、非透明、`ShowInTaskbar=False`、`WS_EX_NOACTIVATE`（不抢焦点）、Topmost 的小窗口，承载 DWM thumbnail，自动保持源窗口宽高比。

交互：

- 列表项 `MouseEnter` → 显示缩略图窗口并定位到鼠标右侧（超出屏幕自动回摆），`DwmRegisterThumbnail(dest=previewHwnd, source=targetHwnd)` 并 `DwmUpdateThumbnailProperties` 置可见。
- `MouseLeave`（离开 ListBox）/ `Deactivated` / `HideWindow` → 隐藏窗口、`DwmUnregisterThumbnail`。
- 源窗口被关闭时 DWM 自动停止更新，无需额外处理。

## 文件改动清单

- `Switcheroo/MainWindow.xaml`：放大尺寸、ProcessPanel 改造（WrapPanel）、列表项字号/列宽、悬停事件。
- `Switcheroo/MainWindow.xaml.cs`：动态过滤标签、多屏定位、按屏过滤、悬停缩略图接线、Alt 数字键映射。
- `Switcheroo/MultiMonitorHelper.cs`：新增。
- `Switcheroo/DwmThumbnail.cs`：新增。
- `Switcheroo/ThumbnailPreviewWindow.cs`：新增。
- `Switcheroo/Switcheroo.csproj`：Newtonsoft HintPath、新文件登记。
- `Switcheroo/app.config`：supportedRuntime → v4.8。
- `Switcheroo/packages.config`、`Core.UnitTests/packages.config`：版本与 targetFramework 更新。
- `Core.UnitTests/Core.UnitTests.csproj`：NUnit HintPath。

## 验证

1. `nuget restore` 成功，Release 编译通过（MSBuild）。
2. Core.UnitTests 编译通过（NUnit 4 API 兼容）。
3. 实机验证（用户）：双屏环境 Alt+Tab 激活定位在鼠标所在屏、列表只含当前屏窗口、悬停显示缩略图、过滤标签动态变化。
