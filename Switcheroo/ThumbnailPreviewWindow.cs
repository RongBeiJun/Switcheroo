using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        private IntPtr _thumbnailId;
        private IntPtr _sourceHwnd;

        public ThumbnailPreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = false;
            Background = System.Windows.Media.Brushes.Black;
            // 外轮廓：细边框 + 圆角，突出预览轮廓
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(200, 74, 144, 217));
            BorderThickness = new Thickness(2);
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
        /// 显示 source 窗口的实时缩略图。若 source 改变则重新注册并自适应窗口尺寸。
        /// </summary>
        public void ShowThumbnail(IntPtr sourceHwnd, double maxWidth, double maxHeight)
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
                Show();
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
            if (Visibility != Visibility.Visible)
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
        /// 已可见时平滑吸附移动到新位置（悬停切换列表项时跟随）。
        /// </summary>
        public void MoveAnimated(double left, double top)
        {
            if (Visibility != Visibility.Visible)
            {
                Left = left;
                Top = top;
                return;
            }

            if (Math.Abs(Left - left) > 0.5)
            {
                var animX = new DoubleAnimation(Left, left, TimeSpan.FromMilliseconds(140));
                animX.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
                BeginAnimation(LeftProperty, animX);
            }
            if (Math.Abs(Top - top) > 0.5)
            {
                var animY = new DoubleAnimation(Top, top, TimeSpan.FromMilliseconds(140));
                animY.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
                BeginAnimation(TopProperty, animY);
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
            var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(100));
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

            var dest = new RECT
            {
                Left = client.Left,
                Top = client.Top,
                Right = client.Right,
                Bottom = client.Bottom
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