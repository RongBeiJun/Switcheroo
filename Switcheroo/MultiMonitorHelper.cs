using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace Switcheroo
{
    /// <summary>
    /// 多显示器辅助工具：获取鼠标所在屏幕、屏幕物理像素与 WPF DIP 的换算。
    /// 应用已启用 PerMonitorV2 DPI 感知，WPF 窗口 Left/Top 使用 DIP 坐标系，
    /// 因此物理像素到 DIP 的换算必须基于目标屏幕的实际 DPI（而非主屏）。
    /// </summary>
    public static class MultiMonitorHelper
    {
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// 系统 DPI 缩放系数（主屏），仅作为后备。
        /// </summary>
        public static double SystemDpiScale
        {
            get
            {
                return Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
            }
        }

        /// <summary>
        /// 鼠标当前所在的显示器。
        /// </summary>
        public static Screen GetMouseScreen()
        {
            return Screen.FromPoint(Cursor.Position);
        }

        /// <summary>
        /// 目标屏幕的 DPI 缩放系数（物理像素 / DIP）。
        /// </summary>
        public static double GetDpiScale(Screen screen)
        {
            var hMonitor = MonitorFromPoint(new POINT
            {
                X = screen.Bounds.X + screen.Bounds.Width / 2,
                Y = screen.Bounds.Y + screen.Bounds.Height / 2
            }, MONITOR_DEFAULTTONEAREST);

            uint dpiX;
            uint dpiY;
            if (hMonitor != IntPtr.Zero && GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0)
            {
                return dpiX / 96.0;
            }
            return SystemDpiScale;
        }

        /// <summary>
        /// 将屏幕的物理像素范围换算为 WPF DIP 坐标范围。
        /// </summary>
        public static Rect ToDIPBounds(Screen screen)
        {
            var scale = GetDpiScale(screen);
            return new Rect(
                screen.Bounds.X / scale,
                screen.Bounds.Y / scale,
                screen.Bounds.Width / scale,
                screen.Bounds.Height / scale);
        }

        /// <summary>
        /// 将 Win32 物理像素坐标换算为 WPF DIP 坐标（按该点所在屏幕的 DPI）。
        /// </summary>
        public static System.Windows.Point ToDIP(PointF physicalPoint)
        {
            var scale = GetDpiScale(Screen.FromPoint(new System.Drawing.Point((int)physicalPoint.X, (int)physicalPoint.Y)));
            return new System.Windows.Point(physicalPoint.X / (float)scale, physicalPoint.Y / (float)scale);
        }

        /// <summary>
        /// 窗口当前的 DPI（GetDpiForWindow）。跨监视器切换时，WPF 窗口会按新监视器 DPI
        /// 重新解释 DIP 尺寸，但 Left/Top 可能在 DPI 上下文更新前被按旧 DPI 解释，
        /// 导致位置与尺寸缩放系数不一致而偏移。
        /// </summary>
        public static int GetWindowDpi(IntPtr hwnd)
        {
            return hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
        }

        /// <summary>
        /// 将窗口物理移动到指定物理像素坐标（不改变尺寸/层级/激活状态）。
        /// 用于先把窗口移入目标监视器，触发 WPF 的 DPI 上下文刷新。
        /// </summary>
        public static void MoveWindowPhysical(IntPtr hwnd, int physicalX, int physicalY)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            SetWindowPos(hwnd, IntPtr.Zero, physicalX, physicalY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}