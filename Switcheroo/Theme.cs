using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Switcheroo.Properties;

namespace Switcheroo
{
    public static class Theme
    {
        private static SolidColorBrush Background;
        private static SolidColorBrush Foreground;
        private static MainWindow mainWindow;

        private enum Mode
        {
            Light,
            Dark
        }

        public static void SuscribeWindow(MainWindow main)
        {
            mainWindow = main;
        }

        public static void LoadTheme()
        {
            Mode mode;
            Enum.TryParse(Settings.Default.Theme, out mode);
            switch (mode)
            {
                case Mode.Light:
                    Background = new SolidColorBrush(Color.FromRgb(248, 248, 248));
                    Foreground = new SolidColorBrush(Color.FromRgb(0,0,0));
                    break;
                case Mode.Dark:
                    Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                    Foreground = new SolidColorBrush(Color.FromRgb(248, 248, 248));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            SetUpTheme();
        }

        private static void SetUpTheme()
        {
            // 窗口本身透明（AllowsTransparency），圆角/背景由根 Border 承担，避免圆角外露出方角
            mainWindow.Background = System.Windows.Media.Brushes.Transparent;

            mainWindow.Border.Background =
                mainWindow.tb.Background = mainWindow.lb.Background
                = mainWindow.Border.BorderBrush = Background;

            mainWindow.tb.Foreground = mainWindow.lb.Foreground = Foreground;
        }
    }
}
