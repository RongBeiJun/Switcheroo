# Alt+Tab 唤起窗口不居中 + 缩略图被放大 问题诊断

日期: 2026-09-28

## 问题描述

用户反馈：有时用 Alt+Tab 唤起 Switcheroo，主窗口不显示在屏幕中间，且悬停缩略图"似乎被放大了"。偶尔触发，**在副屏触发概率最高**。

## 环境

- 三显示器：DISPLAY1 主屏 1920x1080@96DPI (0,0)；DISPLAY2 1920x1080@96DPI (-1920,0)；**DISPLAY9 3392x2400@144DPI（150% 缩放）(-1490,1080)**
- 应用 PerMonitorV2 DPI 感知（`GetProcessDpiAwareness=2`），Release 构建 2026/9/28
- 设置：AltTabHook=True、AutoSwitch=True、EnableHotKey=False、Language=zh

## 根因分析（确信度 >95%）

经实机复现（注入 Alt+Tab + 感知进程枚举窗口物理矩形）确认了两个独立根因：

### 根因 1：主窗口不居中 —— `CenterWindow()` 布局未完成就读陈旧尺寸 + WPF 跨 DPI 位置换算竞态

`CenterWindow()` 原实现：

```csharp
SizeToContent = SizeToContent.Manual;
SizeToContent = SizeToContent.WidthAndHeight;
Left = dipBounds.X + (dipBounds.Width - ActualWidth) / 2;   // ActualWidth 是陈旧值
Top  = dipBounds.Y + (dipBounds.Height - ActualHeight) / 2;
```

两个叠加因素：

1. **陈旧尺寸**：`SizeToContent` 切换不触发同步布局，`ActualWidth/Height` 仍是上一次显示的测量值。内容高度随不同屏幕（按屏过滤后窗口数量不同）变化大时，按旧高度居中导致纵向偏移。复现：修复前 DISPLAY1 首唤偏移 (280,228)。

2. **跨 DPI 位置换算竞态（主因，副屏必现）**：窗口从 96DPI 屏切换到 144DPI 副屏时，WPF 的 DPI 上下文（HwndSource DPI）更新滞后于 `Left/Top` 设置——
   - 窗口**尺寸**按新 DPI（144）渲染：800 DIP → 1200 物理 px ✓
   - 窗口**位置**却按旧 DPI（96）解释 `Left/Top`：`Left=-262.7 DIP → 物理 -262.7px`（应为 -394px）
   
   结果位置与尺寸缩放系数不一致，窗口偏移。复现：DISPLAY9 上实测主窗口 1200x519px @144DPI，中心 (337,1606) vs 屏中心 (206,2280)，**偏移 (131,-674)**。窗口已停在副屏时再唤起则正常 → "偶尔"；副屏是 150% 缩放 → "副屏概率最高"。

### 根因 2：缩略图"被放大" —— 预览窗口单例跨 DPI 上下文滞后

`ThumbnailPreviewWindow` 是**复用单例**。上次在某个 DPI 屏显示后，其 DPI 上下文停留该屏。再次在另一 DPI 屏悬停时：

- `ShowThumbnailPreview` 按目标屏（鼠标屏）DPI 计算 DIP 位置；
- 但 WPF 对已存在的窗口**按旧 DPI 上下文解释该 DIP 位置**；
- 结果预览窗口出现在 1.5 倍偏移的位置，物理尺寸被放大。

复现：修复前在 DISPLAY1（96DPI）悬停，预览窗口实测 (1470,840) = 期望位置 (980,560) × 1.5，尺寸 520x286 @96DPI，位置却按 144 解释 → "缩略图被放大"。

另：缩略图定位原用 Alt+Tab 时的 `_activeScreen`（过期），跨屏悬停时定位与钳制基准错误。

## 修复方案

### 修复 1：`MainWindow.CenterWindow()`

```csharp
UpdateLayout();  // 强制同步 measure/arrange，读到当前内容尺寸

var hwnd = new WindowInteropHelper(this).Handle;
var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
if (hwnd != IntPtr.Zero && MultiMonitorHelper.GetWindowDpi(hwnd) != targetDpi)
{
    // 跨 DPI：先物理移入目标屏触发 DPI 上下文刷新，
    // 再于布局完成后按目标屏 DIP 重新居中
    MultiMonitorHelper.MoveWindowPhysical(hwnd, ...目标屏中心...);
    Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded);  // 居中
    return;
}
SetCenteredPosition(dipBounds);
```

- `UpdateLayout()` 解决陈旧尺寸；
- 跨 DPI 时先 `MoveWindowPhysical`（SetWindowPos 物理坐标）把窗口移入目标屏，WPF 收到 WM_DPICHANGED 后 DPI 上下文刷新，再于 Loaded 优先级（布局完成后）按正确 scale 设置 DIP 居中坐标。因窗口在 `Opacity=1` 前完成定位，无闪烁。

### 修复 2：`MainWindow.ShowThumbnailPreview()`

- 目标屏改用**当前鼠标屏** `GetMouseScreen()`（替代过期的 `_activeScreen`）；
- 预览窗口跨 DPI 时同样先物理移入目标屏刷新上下文，再于 Loaded 优先级通过 `PositionThumbnailPreview()` 定位；
- 定位逻辑抽成独立方法。

### 新增工具：`MultiMonitorHelper`

- `GetWindowDpi(IntPtr)`：`GetDpiForWindow`
- `MoveWindowPhysical(IntPtr, x, y)`：`SetWindowPos`（NOSIZE|NOZORDER|NOACTIVATE）

## 复现与验证方法

用 DPI 感知 PowerShell 注入 Alt+Tab（keybd_event），Alt 释放前按 Esc 取消避免切换窗口；枚举进程可见窗口物理矩形，与目标屏物理中心比对。

修复前 DISPLAY9 delta=(131,-674)；修复后全部 delta=(0,0)。缩略图位置 D1=(980,560)=鼠标+20,20（修复前 1470,840 偏移 1.5x）；D9=(236,2310)=鼠标+30,30（144DPI 下 20DIP×1.5）。双向跨 DPI（96→144、144→96）均验证通过。

## 影响面

- 仅影响 `CenterWindow()` 与 `ShowThumbnailPreview()` 定位路径，不改窗口列表/过滤/切换逻辑。
- 无行为回退的公共 API 变化；新增方法仅内部使用。