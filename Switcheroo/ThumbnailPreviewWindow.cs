using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedWinapi.Windows;

namespace Switcheroo
{
    /// <summary>
    /// 显示单个窗口实时 DWM 缩略图的无边框小窗口，悬停列表项时出现。
    /// 窗口尺寸跟随被预览窗口的宽高比，避免 letterbox 黑边。
    /// </summary>
    public class ThumbnailPreviewWindow : Window
    {
        private const int GWL_EXSTYLE = -20;

        // 圆角描边环宽度（物理像素）：缩略图 dest rect 相应内缩
        private const int OutlinePx = 2;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private IntPtr _thumbnailId;
        private IntPtr _sourceHwnd;

        /// <summary>物理坐标移动动画（CompositionTarget.Rendering 逐帧 SetWindowPos）。</summary>
        private EventHandler _moveRenderHandler;
        private int _moveFromX, _moveFromY, _moveToX, _moveToY;
        private DateTime _moveStarted;

        public ThumbnailPreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = false;
            Background = System.Windows.Media.Brushes.Black;
            // 圆角外轮廓：Content 层的圆角描边环（WPF 层在 DWM 缩略图之下，缩略图内缩 2px 露出）。
            // 不用窗口 BorderBrush（方形边框）——DWM 圆角会把它裁出缺口。
            Content = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(200, 74, 144, 217)),
                BorderThickness = new Thickness(2),
                Background = null,
                IsHitTestVisible = false
            };
            SizeToContent = SizeToContent.Manual;
            Width = 480;
            Height = 300;
            SizeChanged += (s, e) => UpdateThumbnailRect();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            DisableActivation(handle);
            EnableRoundedCorners(handle);

            // 首次显示：此时 hwnd 才存在，若已登记了源窗口则补齐注册
            if (_sourceHwnd != IntPtr.Zero && _thumbnailId == IntPtr.Zero)
            {
                _thumbnailId = DwmThumbnail.Register(handle, _sourceHwnd);
                UpdateThumbnailRect();
            }
        }

        private static void DisableActivation(IntPtr hwnd)
        {
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= (int)WindowExStyleFlags.NOACTIVATE;
            exStyle |= (int)WindowExStyleFlags.TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        /// <summary>
        /// Windows 11 下给缩略图窗口启用 DWM 圆角，剪裁缩略图与描边四角（与主窗口一致）。
        /// </summary>
        private static void EnableRoundedCorners(IntPtr hwnd)
        {
            try
            {
                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
                const int DWMWCP_ROUND = 2;
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Windows 10 或更早不支持，忽略
            }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        /// <summary>
        /// DPI 上下文刷新后按源窗口比例重新拟合窗口尺寸（跨 DPI 屏显示前调用）。
        /// </summary>
        public void RefitToSource(double maxWidth, double maxHeight)
        {
            FitWindowToSource(maxWidth, maxHeight);
        }

        /// <summary>
        /// 显示 source 窗口的实时缩略图。若 source 改变则重新注册并自适应窗口尺寸。
        /// show=false 时仅注册/适配，不显示（跨 DPI 先移动对齐后再显示，避免错屏闪帧）。
        /// </summary>
        public void ShowThumbnail(IntPtr sourceHwnd, double maxWidth, double maxHeight, bool show = true)
        {
            if (sourceHwnd == IntPtr.Zero)
            {
                HideThumbnail();
                return;
            }

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                // 尚未显示过（hwnd 不存在）：先记录源窗口并显示，注册延后到 OnSourceInitialized
                _sourceHwnd = sourceHwnd;
                FitWindowToSource(maxWidth, maxHeight);
                if (show)
                {
                    Show();
                }
                return;
            }

            if (_sourceHwnd != sourceHwnd)
            {
                UnregisterThumbnail();
                _sourceHwnd = sourceHwnd;
                FitWindowToSource(maxWidth, maxHeight);
                _thumbnailId = DwmThumbnail.Register(handle, sourceHwnd);
            }

            if (_thumbnailId == IntPtr.Zero)
            {
                return;
            }

            // 尺寸/比例可能已变，布局完成后重算目标矩形
            Dispatcher.BeginInvoke(new Action(UpdateThumbnailRect), DispatcherPriority.Loaded);
            if (show && Visibility != Visibility.Visible)
            {
                Show();
            }
        }

        /// <summary>
        /// 将预览窗口调整为与源窗口同比例的最大可用尺寸，消除黑边。
        /// </summary>
        private void FitWindowToSource(double maxWidth, double maxHeight)
        {
            RECT srcRect;
            if (_sourceHwnd != IntPtr.Zero)
            {
                var srcWin = new SystemWindow(_sourceHwnd);
                srcRect = srcWin.WindowState == System.Windows.Forms.FormWindowState.Minimized
                    ? srcWin.Position
                    : srcWin.Rectangle;
            }
            else
            {
                srcRect = new RECT { Right = 16, Bottom = 10 };
            }

            var srcW = Math.Max(1, srcRect.Right - srcRect.Left);
            var srcH = Math.Max(1, srcRect.Bottom - srcRect.Top);

            double w = Math.Max(200, Math.Min(maxWidth, 520));
            double h = w * srcH / (double)srcW;
            if (h > maxHeight)
            {
                h = maxHeight;
                w = h * srcW / (double)srcH;
            }
            Width = Math.Max(200, w);
            Height = Math.Max(120, h);
        }

        /// <summary>
        /// 隐藏缩略图窗口，但保留注册，以便鼠标再次悬停同一窗口时免于重建。
        /// </summary>
        public void HideThumbnail()
        {
            Hide();
        }

        /// <summary>
        /// 纯淡入显示（位置已由外部用物理坐标 SetWindowPos 定位，不再经 WPF Left/Top 跨屏）。
        /// </summary>
        public void ShowFadeIn()
        {
            StopMoveAnimation();
            Show();
            Opacity = 0;
            // 与主窗口进入动画一致（180ms 减缓的淡入）
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, anim);
        }

        /// <summary>
        /// 淡入显示并定位（显示动画，与主窗口滑块质感一致）。
        /// </summary>
        public void ShowAnimated(double left, double top)
        {
            Left = left;
            Top = top;
            Show();
            Opacity = 0;
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, anim);
        }

        /// <summary>
        /// 已可见时平滑吸附移动到新物理坐标（悬停/切换列表项时跟随）。
        /// 用物理坐标逐帧插值（起点取 GetWindowRect 当前物理矩形），不依赖 WPF Left/Top 与 DPI context——
        /// 跨 DPI 屏物理定位后 WPF Left/Top 未同步，若用 WPF 属性动画会从错误起点跳变；设置 WPF Left/Top 又会
        /// 触发 WPF 按错误 DPI context 重新定位（缩略图被拉回主屏）。
        /// </summary>
        public void MoveAnimated(int physLeft, int physTop)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            RECT r;
            if (Visibility != Visibility.Visible || !GetWindowRect(hwnd, out r))
            {
                // 不可见/读不到矩形：直接物理定位
                SetWindowPos(hwnd, IntPtr.Zero, physLeft, physTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return;
            }

            _moveFromX = r.Left;
            _moveFromY = r.Top;
            _moveToX = physLeft;
            _moveToY = physTop;
            if (Math.Abs(_moveFromX - _moveToX) < 1 && Math.Abs(_moveFromY - _moveToY) < 1)
            {
                return;
            }

            // 连续切换时取消上一次未完成的移动动画（起点总是当前物理位置）
            StopMoveAnimation();
            _moveStarted = DateTime.Now;
            _moveRenderHandler = (s, e) => MoveRenderTick(hwnd);
            CompositionTarget.Rendering += _moveRenderHandler;
        }

        private void MoveRenderTick(IntPtr hwnd)
        {
            double t = (DateTime.Now - _moveStarted).TotalMilliseconds / 140.0;
            if (t >= 1)
            {
                t = 1;
                StopMoveAnimation();
            }

            // 复刻原 WPF BackEase(EaseOut, Amplitude=0.25) 的轻微过头吸附感
            const double overshoot = 0.25;
            double u = t - 1;
            double eased = 1 + (overshoot + 1) * u * u * u + overshoot * u * u;
            int x = (int)Math.Round(_moveFromX + (_moveToX - _moveFromX) * eased);
            int y = (int)Math.Round(_moveFromY + (_moveToY - _moveFromY) * eased);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void StopMoveAnimation()
        {
            if (_moveRenderHandler != null)
            {
                CompositionTarget.Rendering -= _moveRenderHandler;
                _moveRenderHandler = null;
            }
        }

        /// <summary>
        /// 淡出后隐藏（隐藏动画）。
        /// </summary>
        public void HideAnimated()
        {
            if (Visibility != Visibility.Visible)
            {
                return;
            }
            StopMoveAnimation();
            // 与主窗口退出动画一致（80ms 加快的淡出）
            var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(80));
            anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            anim.Completed += (s, e) =>
            {
                Opacity = 0;
                Hide();
            };
            BeginAnimation(OpacityProperty, anim);
        }

        /// <summary>
        /// 注销缩略图（源窗口变化或窗口关闭时调用）。
        /// </summary>
        private void UnregisterThumbnail()
        {
            DwmThumbnail.Unregister(_thumbnailId);
            _thumbnailId = IntPtr.Zero;
            _sourceHwnd = IntPtr.Zero;
        }

        /// <summary>
        /// 更新缩略图铺满整个客户区（窗口比例已与源窗口一致，无黑边）。
        /// </summary>
        private void UpdateThumbnailRect()
        {
            if (_thumbnailId == IntPtr.Zero)
            {
                return;
            }

            RECT client;
            if (!GetClientRect(new WindowInteropHelper(this).Handle, out client))
            {
                return;
            }

            // 内缩 2px 露出圆角描边环（缩略图本身铺满内缩后的区域）
            var dest = new RECT
            {
                Left = client.Left + OutlinePx,
                Top = client.Top + OutlinePx,
                Right = client.Right - OutlinePx,
                Bottom = client.Bottom - OutlinePx
            };
            if (!DwmThumbnail.Update(_thumbnailId, dest, true))
            {
                // 源窗口可能已被销毁（或句柄被复用），注销以便重新注册
                UnregisterThumbnail();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterThumbnail();
            base.OnClosed(e);
        }
    }
}