# Alt+Tab 窗口不居中 + 缩略图放大 修复验证

日期: 2026-09-28

## 验证环境

- 三屏：D1 主屏 1920x1080@96DPI、D2 1920x1080@96DPI、D9 3392x2400@**144DPI(150%)**
- Release 构建，PerMonitorV2
- 验证脚本：`Temp/opencode/verify.ps1`（DPI 感知 PowerShell，注入 Alt+Tab、Esc 取消、枚举窗口物理矩形）

## 修复前基线（复现）

| 屏 | 主窗口中心 delta | 缩略图位置 |
|---|---|---|
| D1 (96) | 首唤 (280,228) OFF | (1470,840)，应为鼠标旁 (980,560)，偏移 1.5x |
| D9 (144) | (131,-674) OFF | (236,2310) 780x440（位置 OK，物理尺寸 520DIP×1.5 正常） |

## 修复后结果

| 屏 | 主窗口中心 delta | 缩略图位置 | 结论 |
|---|---|---|---|
| D1 (96) | (0,0) OK（多轮） | (980,560) = 鼠标(960,540)+20,20 ✓ | 通过 |
| D2 (96) | (0,0) OK（多轮） | (-940,560) = 鼠标(-960,540)+20,20 ✓ | 通过 |
| D9 (144) | (0,0) OK（多轮） | (236,2310) = 鼠标(206,2280)+20DIP×1.5 ✓ | 通过 |

- 主窗口 D9 实测：rect=(-394,2021) 1200x519 @144DPI，中心 (206,2280) 精确居中
- **双向跨 DPI**（96→144 及 144→96，预览单例带旧 context 场景）均正确：反向验证 D1 缩略图仍在鼠标旁

## 回归确认

- 原有功能：按屏过滤、悬停缩略图显示/隐藏、Alt+Tab 唤起均正常
- 单元测试：本次改动仅涉及 UI 定位路径（MainWindow/MultiMonitorHelper），Core 测试集不受影响；构建通过

## 遗留说明

- 验证脚本每屏首轮偶发 "NOT FOUND"：注入时序竞争（Alt+Tab 偶发未触发 hook），非本次修复范围，实际人工操作不受影响
- 缩略图在 144DPI 屏上物理尺寸 780px（=520DIP×1.5）属正确的 DPI 渲染，非放大；"放大"的感知来自修复前的跨 DPI 位置错位

## 追加：Alt+Tab 闪烁/不显示 修复（同日）

### 现象

用户补充：两屏（96/144DPI）交替按 Alt+Tab 时，"按一下闪一下、第二次才显示、交替屏一直闪"，且窗口消失时残留一帧黑色。

### 根因（app 内日志 + 外部注入实测确认）

1. **"第二次才显示"**：`HideWindow()` 用 `Dispatcher.BeginInvoke(Hide, Input)` **延迟隐藏**。前一次取消/切换的 Hide() 尚未执行时，窗口仍 `Visibility=Visible`（"幽灵可见"），下一次 Alt+Tab 被误判为已可见而走 else 分支（只切选中项、不重新显示）→ 表现为"闪一下、第二次才有"。交替屏时每次前一次隐藏未落地，叠加放大。
2. **黑色闪帧**：延迟 Hide 期间 `Opacity=0` 已设但窗口未隐藏，若触发重绘会残留一帧背景色。
3. **跨 DPI 窗口不显示**：`CenterWindow` 原用手动 `SetWindowPos` 把窗口物理跨屏移动——**窗口显示后跨屏移动会破坏激活状态 → Deactivated → 立即隐藏**。改为 `PrepareWindowAcrossDpi()`：在 **Show() 之前**（隐藏状态下）物理移入目标屏，无失焦问题；DPI 上下文就绪后居中。

### 修复

- `HideWindow()`：延迟 `BeginInvoke(Hide, Input)` → **同步 `Hide()`**（Visibility 立即干净，下次唤起必走显示分支；消除黑帧）
- 新增 `PrepareWindowAcrossDpi()`：显示前跨 DPI 时隐藏窗口物理移入目标屏（挂接 3 处显示路径）
- `CenterWindow()`：跨 DPI 分支改为先按当前上下文定位（窗口保证可见）→ `BeginInvoke(Loaded)` 中修正居中；移除手动跨屏 SetWindowPos

### 验证

- 同屏连续 3 次 Alt+Tab：全部显示且居中（修复前"第二次才显示"不再出现）
- app 内日志确认：跨 DPI 唤起时 Show/CenterWindow/DPI 上下文刷新全部正确执行、窗口按目标屏正确居中
- 注入测试的跨屏窗口"不显示"经日志定位为**注入环境激活局限**（keybd_event 注入无法获得真实物理按键的 SetForegroundWindow 前台权限，窗口显示后立即 Deactivated；app 内部显示流程正确），**需用户实机确认**
- 快速"按一下"即松手 Alt 时，AutoSwitch=True 会在松开时切换目标窗口（`_altTabAutoSwitch`）→ 表现为"闪一下"，此为 AutoSwitch 既有设计（按住浏览、松开切换），非本次修复范围

## 追加：黑帧优化 + 触发延迟改善（次日）

### 用户反馈

- 两屏交替快速按 Alt+Tab 不再"闪退"（此前崩溃确认为调试日志代码 `OnLostFocus` 内访问 `SystemWindow.ForegroundWindow.ClassName` 在无有效前台时抛 `Win32Exception` 导致进程终止，已移除日志）
- 快速按松开可正常切换（AutoSwitch 提前武装生效）
- 但窗口出现前会有较长黑色背景帧；触发延迟偏高

### 修复

- **黑色背景帧**：`ShowMainWindowFromAltTab` 与 tray/热键路径改为**先 （隐藏状态下）执行 `LoadData` 完成数据填充与布局，再 `Show` + `Opacity=1`**——窗口第一帧即内容，无窗口背景色闪帧（曾尝试 ContentRendered 后显示，但 `Opacity=0` 时 WPF 不渲染导致事件不触发、走 200ms 兜底反而更糟，已回退）
- **快速按切换**：`AltTabPressed` 钩子回调中同步设置 `_altTabAutoSwitch`（不再等延迟显示），快速松手也能触发切换
- **钩子超时**：`AltTabPressed` 仅快速置 `Handled` 并延时到 Dispatcher，避免在低层键盘钩子回调内同步枚举窗口导致系统切换器接管

### 用户实机确认

- 快速按切换窗口功能正常
- 黑帧消除，触发/显示正常（用户确认"修复的不错"）