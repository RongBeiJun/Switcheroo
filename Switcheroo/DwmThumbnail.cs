using System;
using System.Runtime.InteropServices;
using ManagedWinapi.Windows;

namespace Switcheroo
{
    /// <summary>
    /// dwmapi.dll DWM 缩略图（实时窗口预览）封装，与任务栏悬停预览同一套 API。
    /// </summary>
    public static class DwmThumbnail
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public ManagedWinapi.Windows.RECT rcDestination;
            public ManagedWinapi.Windows.RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fSourceClientAreaOnly;
        }

        private const int DWM_TNP_RECTDESTINATION = 0x00000001;
        private const int DWM_TNP_RECTSOURCE = 0x00000002;
        private const int DWM_TNP_OPACITY = 0x00000004;
        private const int DWM_TNP_VISIBLE = 0x00000008;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

        /// <summary>
        /// 将 source 窗口的实时内容注册到 destination 窗口。
        /// </summary>
        public static IntPtr Register(IntPtr destinationHwnd, IntPtr sourceHwnd)
        {
            IntPtr thumbId;
            if (DwmRegisterThumbnail(destinationHwnd, sourceHwnd, out thumbId) == 0)
            {
                return thumbId;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 更新缩略图的显示区域与可见性。
        /// rcDestination 为目标窗口客户区物理像素坐标。
        /// rcSource 传入空矩形（0,0,0,0）且不设置 DWM_TNP_RECTSOURCE，
        /// DWM 将使用源窗口整个客户区 —— 避免坐标单位/原点差异导致黑屏。
        /// </summary>
        public static bool Update(IntPtr thumbId, RECT destination, bool visible)
        {
            if (thumbId == IntPtr.Zero)
            {
                return false;
            }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE,
                rcDestination = destination,
                fVisible = visible
            };
            return DwmUpdateThumbnailProperties(thumbId, ref props) == 0;
        }

        /// <summary>
        /// 注销缩略图。
        /// </summary>
        public static void Unregister(IntPtr thumbId)
        {
            if (thumbId != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(thumbId);
            }
        }
    }
}