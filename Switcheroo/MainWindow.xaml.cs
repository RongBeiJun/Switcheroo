/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedWinapi;
using ManagedWinapi.Windows;
using Switcheroo.Core;
using Switcheroo.Core.Matchers;
using Switcheroo.Properties;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Forms.MenuItem;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Media;

namespace Switcheroo
{
    public partial class MainWindow : Window
    {
        private WindowCloser _windowCloser;
        private List<AppWindowViewModel> _unfilteredWindowList;
        private ObservableCollection<AppWindowViewModel> _filteredWindowList;
        private NotifyIcon _notifyIcon;
        private HotKey _hotkey;

        public static readonly RoutedUICommand CloseWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand SwitchToWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListDownCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListUpCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListPageDownCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListPageUpCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListHomeCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListEndCommand = new RoutedUICommand();
        private OptionsWindow _optionsWindow;
        private AboutWindow _aboutWindow;
        private AltTabHook _altTabHook;
        private SystemWindow _foregroundWindow;
        private bool _altTabAutoSwitch;
        private bool _sortWinList = false;

        private string processFilterText = "";
        private System.Windows.Forms.Screen _activeScreen;
        private ThumbnailPreviewWindow _thumbnailPreviewWindow;
        private readonly System.Windows.Threading.DispatcherTimer _hidePreviewTimer;


        public MainWindow()
        {
            InitializeComponent();

            SetUpKeyBindings();

            SetUpNotifyIcon();

            SetUpHotKey();

            SetUpAltTabHook();

            CheckForUpdates();

            Theme.SuscribeWindow(this);

            Theme.LoadTheme();

            Opacity = 0;

            _hidePreviewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _hidePreviewTimer.Tick += (s, e) =>
            {
                _hidePreviewTimer.Stop();
                // 鼠标仍停留在预览窗口或列表上则不隐藏，供用户停留查看
                if (MouseOverPreviewWindowOrList())
                {
                    _hidePreviewTimer.Start();
                    return;
                }
                HideThumbnailPreview();
            };
        }

        private bool MouseOverPreviewWindowOrList()
        {
            if (_thumbnailPreviewWindow != null && _thumbnailPreviewWindow.Visibility == Visibility.Visible)
            {
                var previewBounds = new Rect(_thumbnailPreviewWindow.Left, _thumbnailPreviewWindow.Top,
                    _thumbnailPreviewWindow.ActualWidth, _thumbnailPreviewWindow.ActualHeight);
                if (previewBounds.Contains(MultiMonitorHelper.ToDIP(new System.Drawing.PointF(
                    System.Windows.Forms.Cursor.Position.X,
                    System.Windows.Forms.Cursor.Position.Y))))
                {
                    return true;
                }
            }

            var cursorDIP = MultiMonitorHelper.ToDIP(new System.Drawing.PointF(
                System.Windows.Forms.Cursor.Position.X,
                System.Windows.Forms.Cursor.Position.Y));
            var listPoint = lb.PointFromScreen(cursorDIP);
            return lb.IsMouseOver || (listPoint.X >= 0 && listPoint.Y >= 0);
        }
        #region process filter

        /**
         * 根据当前已开启窗口列表生成程序过滤标签, 动态适应; 
         * 按进程名排序保证序号稳定。 
         */
        private void SetUpProcessFilter()
        {
            spProcessFilter.Children.Clear();

            var processes = _unfilteredWindowList?
                .Select(w => w.ProcessTitle)
                .Where(p => !String.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p)
                .Take(10)
                .ToList();
            if (processes == null) return;

            for (var i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                var tb = new TextBlock()
                {
                    Text = (i + 1) + "." + process,
                    FontSize = 20,
                    Tag = process,
                    Background = Brushes.Transparent
                };
                tb.Foreground = processFilterText == process ? Brushes.Red : Brushes.Black;
                tb.Margin = new Thickness(2, 0, 2, 0);
                tb.MouseDown += TbProcessFilter_MouseDown;
                spProcessFilter.Children.Add(tb);
            }
        }

        /**
         * 设置当前进程过滤并刷新界面; 
         * process 为空表示清除过滤。 
         */
        private void SetProcessFilter(string process)
        {
            if (String.IsNullOrEmpty(process) || this.processFilterText == process)
            {
                this.processFilterText = "";
            }
            else
            {
                this.processFilterText = process;
            }
            SetUpProcessFilter();
            RefreshFilter();
        }

        /**
         * 单击时切换程序过滤; 
         */
        private void TbProcessFilter_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as TextBlock;
            SetProcessFilter(tb?.Tag as String);
        }

        /**
         * 切换对应index的程序, 从0开始; 
         * 如果index小于0, 清除过滤; 
         */
        void switchProcessFilter(int index)
        {
            if(index >= spProcessFilter.Children.Count)
            {
                return;
            }
            if(index < 0)
            {
                SetProcessFilter("");
                return;
            }
            var  tb = spProcessFilter.Children[index] as TextBlock;
            SetProcessFilter(tb.Tag as String);
        }
        #endregion

        /// =================================

        #region Private Methods

        /// =================================

        private void SetUpKeyBindings()
        {
            // Enter and Esc bindings are not executed before the keys have been released.
            // This is done to prevent that the window being focused after the key presses
            // to get 'KeyUp' messages.

            KeyDown += (sender, args) =>
            {
                // Opacity is set to 0 right away so it appears that action has been taken right away...
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    //qxx 为了兼容输入法里的回车; 
                    Switch();
                    Opacity = 0;
                }
                else if (args.Key == Key.Escape)
                {
                    Opacity = 0;
                }
                else if (args.SystemKey == Key.O)
                {
                    Options();
                }
                else if (args.SystemKey == Key.W)
                {
                    ExportToJSON();
                }
                else if (args.SystemKey == Key.Q && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    _altTabAutoSwitch = false;
                    tb.Text = "";
                    tb.IsEnabled = true;
                    tb.Focus();
                }
                else if (args.SystemKey == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    Toggle_sortWinList();
                    LoadData(InitialFocus.NextItem);
                }
                else if ((args.SystemKey == Key.Up || args.SystemKey == Key.K) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    PreviousItem();
                }
                else if ((args.SystemKey == Key.Down || args.SystemKey == Key.J) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    NextItem();
                }
                else if (args.SystemKey == Key.D1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(0);
                }
                else if (args.SystemKey == Key.D2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(1);
                }
                else if (args.SystemKey == Key.D3 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(2);
                }
                else if (args.SystemKey == Key.D4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(3);
                }
                else if (args.SystemKey == Key.D5 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(4);
                }
                else if (args.SystemKey == Key.D6 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(5);
                }
                else if (args.SystemKey == Key.D7 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(6);
                }
                else if (args.SystemKey == Key.D8 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(7);
                }
                else if (args.SystemKey == Key.D9 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(8);
                }
                else if (args.SystemKey == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    switchProcessFilter(-1);
                }
            };

            KeyUp += (sender, args) =>
            {
                // ... But only when the keys are release, the action is actually executed
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    //Switch();
                }
                else if (args.Key == Key.Escape)
                {
                    HideWindow();
                }
                else if (args.SystemKey == Key.LeftAlt && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _altTabAutoSwitch )
                {
                    Switch();
                }
                else if (args.Key == Key.LeftAlt && _altTabAutoSwitch)
                {
                    Switch();
                }
            };
        }

        private void SetUpHotKey()
        {
            _hotkey = new HotKey();
            _hotkey.LoadSettings();

            Application.Current.Properties["hotkey"] = _hotkey;

            _hotkey.HotkeyPressed += hotkey_HotkeyPressed;
            try
            {
                _hotkey.Enabled = Settings.Default.EnableHotKey;
            }
            catch (HotkeyAlreadyInUseException)
            {
                var boxText = Localization.Get("MsgHotkeyInUse");
                MessageBox.Show(boxText, Localization.Get("MsgHotkeyInUseTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetUpAltTabHook()
        {
            _altTabHook = new AltTabHook();
            _altTabHook.Pressed += AltTabPressed;
        }

        private void SetUpNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Text = "Switcheroo",
                Icon = Properties.Resources.icon,
                Visible = true
            };
            _notifyIcon.MouseClick += new System.Windows.Forms.MouseEventHandler(NotifyIconMouseClick);

            BuildTrayMenu();

            // 语言切换时重建托盘菜单
            Localization.LanguageChanged += BuildTrayMenu;
        }

        private MenuItem _sortAZMenuItem;

        private void BuildTrayMenu()
        {
            if (_notifyIcon == null) return;

            var runOnStartupMenuItem = new MenuItem(Localization.Get("TrayRunOnStartup"), (s, e) => RunOnStartup(s as MenuItem))
            {
                Checked = new AutoStart().IsEnabled
            };

            if (_sortAZMenuItem == null)
            {
                _sortAZMenuItem = new MenuItem("", (s, e) => sortAZMenuItem_Click(s as MenuItem));
            }
            _sortAZMenuItem.Text = Localization.Get("TraySort");
            _sortAZMenuItem.Checked = _sortWinList;

            var exportToJSON_MenuItem = new MenuItem(Localization.Get("TrayExport"), (s, e) => exportToJSON_MenuItem_Click(s as MenuItem));

            _notifyIcon.ContextMenu = new System.Windows.Forms.ContextMenu(new[]
            {
                new MenuItem(Localization.Get("TrayOptions"), (s, e) => Options()),
                runOnStartupMenuItem,
                _sortAZMenuItem,
                exportToJSON_MenuItem,
                new MenuItem(Localization.Get("TrayAbout"), (s, e) => About()),
                new MenuItem(Localization.Get("TrayExit"), (s, e) => Quit())
            });
        }

        void NotifyIconMouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Visibility != Visibility.Visible)
                {
                   _foregroundWindow = SystemWindow.ForegroundWindow;
                    PrepareWindowAcrossDpi();
                    // 隐藏状态下先加载数据与布局：内容就绪后再显示，避免窗口出现前的黑色背景帧
                    LoadData(InitialFocus.NextItem);
                    Show();
                    Activate();
                    tb.IsEnabled = true;
                    tb.Text = "";
                    Keyboard.Focus(tb);
                    Opacity = 1;
                }
            }
        }

        private static void RunOnStartup(MenuItem menuItem)
        {
            try
            {
                var autoStart = new AutoStart
                {
                    IsEnabled = !menuItem.Checked
                };
                menuItem.Checked = autoStart.IsEnabled;
            }
            catch (AutoStartException e)
            {
                MessageBox.Show(e.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void CheckForUpdates()
        {
            var currentVersion = Assembly.GetEntryAssembly().GetName().Version;
            if (currentVersion == new Version(0, 0, 0, 0))
            {
                return;
            }

            var timer = new DispatcherTimer();

            timer.Tick += async (sender, args) =>
            {
                timer.Stop();
                var latestVersion = await GetLatestVersion();
                if (latestVersion != null && latestVersion > currentVersion)
                {
                    var result = MessageBox.Show(
                        string.Format(
                            Localization.Get("MsgUpdateBody"),
                            latestVersion, currentVersion),
                        Localization.Get("MsgUpdateTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Process.Start("https://github.com/kvakulo/Switcheroo/releases/latest");
                    }
                }
                else
                {
                    timer.Interval = new TimeSpan(24, 0, 0);
                    timer.Start();
                }
            };

            timer.Interval = new TimeSpan(0, 0, 0);
            timer.Start();
        }

        private static async Task<Version> GetLatestVersion()
        {
            try
            {
                var versionAsString =
                    await
                        new WebClient().DownloadStringTaskAsync(
                            "https://raw.github.com/kvakulo/Switcheroo/update/version.txt");
                Version newVersion;
                if (Version.TryParse(versionAsString, out newVersion))
                {
                    return newVersion;
                }
            }
            catch (WebException)
            {
            }
            return null;
        }

        /// <summary>
        /// Export JSON of currently running windows.
        /// JSON Structure: arrays of window titles are grouped under unique process names which are sorted alphabetically.
        /// </summary>
        private void ExportToJSON()
        {
            _unfilteredWindowList = new WindowFinder().GetWindows().Select(window => new AppWindowViewModel(window)).ToList();
            _unfilteredWindowList = _unfilteredWindowList.OrderBy(x => x.ProcessTitle).ToList();

            var winsDict = _unfilteredWindowList.GroupBy(x => x.ProcessTitle).ToDictionary(x => x.Key, x => x.Select(y => y.WindowTitle));

            string JSON_output = JsonConvert.SerializeObject(winsDict);

            SaveFileDialog SaveFileDialog1 = new SaveFileDialog();
            SaveFileDialog1.Title = "Save list to";
            SaveFileDialog1.DefaultExt = "txt";
            SaveFileDialog1.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            SaveFileDialog1.ShowDialog();

            if (SaveFileDialog1.FileName != "")
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(@SaveFileDialog1.FileName, false))
                {
                    file.Write(JSON_output);
                }
            }
        }

        // 窗口快照缓存：Alt+Tab 唤起间复用枚举结果，降低唤起延迟。
        // 短 TTL（2s）保证窗口增删仍能较及时反映。
        private static List<AppWindow> _windowSnapshotCache;
        private static DateTime _windowSnapshotTime;
        private static readonly TimeSpan WindowSnapshotTtl = TimeSpan.FromSeconds(2);

        private static List<AppWindow> GetWindowSnapshot()
        {
            var now = DateTime.Now;
            if (_windowSnapshotCache != null && now - _windowSnapshotTime < WindowSnapshotTtl)
            {
                return _windowSnapshotCache;
            }
            var snapshot = new WindowFinder().GetWindows();
            _windowSnapshotCache = snapshot;
            _windowSnapshotTime = now;
            return snapshot;
        }

        /// <summary>
        /// Populates the window list with the current running windows.
        /// </summary>
        private void LoadData(InitialFocus focus)
        {
            _activeScreen = MultiMonitorHelper.GetMouseScreen();

            var windows = GetWindowSnapshot().Select(window => new AppWindowViewModel(window)).ToList();
            _unfilteredWindowList = FilterWindowsOnScreen(windows);

            //qxx
            switchProcessFilter(-1);

            var firstWindow = _unfilteredWindowList.FirstOrDefault();

            var foregroundWindowMovedToBottom = false;
            
            // Move first window to the bottom of the list if it's related to the foreground window
            if (firstWindow != null && AreWindowsRelated(firstWindow.AppWindow, _foregroundWindow))
            {
                _unfilteredWindowList.RemoveAt(0);
                _unfilteredWindowList.Add(firstWindow);
                foregroundWindowMovedToBottom = true;
            }

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
            _windowCloser = new WindowCloser();

            for (var i = 0; i < _unfilteredWindowList.Count; i++)
            {
                _unfilteredWindowList[i].FormattedTitle = new XamlHighlighter().Highlight(new[] { new StringPart(_unfilteredWindowList[i].AppWindow.Title) });
                _unfilteredWindowList[i].FormattedProcessTitle =
                    new XamlHighlighter().Highlight(new[] { new StringPart(_unfilteredWindowList[i].AppWindow.ProcessTitle) });
            }

            if (_sortWinList == true)
            {
                _unfilteredWindowList = _unfilteredWindowList.OrderBy(x => x.FormattedProcessTitle).ToList();
            }
            
            var _itemsCountToHighlight = Math.Min(_unfilteredWindowList.Count, 10);
            for (var i = 0; i < _itemsCountToHighlight; i++)
            {
                _unfilteredWindowList[i].FormattedTitle = new XamlHighlighter().Highlight(new[] { new StringPart("" + (i + 1) + " ", true) }) + _unfilteredWindowList[i].FormattedTitle ;
            }

            if (_sortWinList == true)
            {
                lb.DataContext = null;
                lb.DataContext = _unfilteredWindowList;
            }
            else
            {
                lb.DataContext = _filteredWindowList;
            }

            FocusItemInList(focus, foregroundWindowMovedToBottom);

            if ( tb.IsEnabled ) tb.Clear();
            tb.Focus();
            CenterWindow();
            ScrollSelectedItemIntoView();
        }

        private static bool AreWindowsRelated(SystemWindow window1, SystemWindow window2)
        {
            return window1.HWnd == window2.HWnd || window1.Process.Id == window2.Process.Id;
        }

        /// <summary>
        /// 只保留落在当前激活屏幕内的窗口。
        /// 最小化窗口用恢复位置（rcNormalPosition），其余用实际窗口矩形 GetWindowRect，
        /// 取两者中心点判断。若只有一个屏幕，全保留。
        /// </summary>
        private List<AppWindowViewModel> FilterWindowsOnScreen(IEnumerable<AppWindowViewModel> windows)
        {
            if (_activeScreen == null)
            {
                return windows.ToList();
            }

            var bounds = _activeScreen.Bounds;

            return windows.Where(w =>
            {
                var win = w.AppWindow;
                ManagedWinapi.Windows.RECT rect;
                if (win.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                {
                    rect = win.Position;
                }
                else
                {
                    rect = win.Rectangle;
                }

                var center = new System.Drawing.Point(
                    (rect.Left + rect.Right) / 2,
                    (rect.Top + rect.Bottom) / 2);
                return bounds.Contains(center);
            }).ToList();
        }

        private void FocusItemInList(InitialFocus focus, bool foregroundWindowMovedToBottom)
        {
            if (focus == InitialFocus.PreviousItem)
            {
                var previousItemIndex = lb.Items.Count - 1;
                if (foregroundWindowMovedToBottom)
                {
                    previousItemIndex--;
                }

                lb.SelectedIndex = previousItemIndex > 0 ? previousItemIndex : 0;
            }
            else
            {
                lb.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// 显示窗口前调用：若目标屏（鼠标所在屏）与窗口当前 DPI 上下文不一致（跨 DPI 屏切换），
        /// 先在隐藏状态下把窗口物理移入目标屏，让 Windows 刷新窗口的 DPI 上下文。
        /// 关键：窗口显示之后再跨屏移动会破坏激活状态（Deactivated → 立即隐藏，表现为“闪一下/不显示”）；
        /// 隐藏状态下移动窗口则没有失焦问题。首次显示（hwnd 尚不存在）无需处理。
        /// </summary>
        private void PrepareWindowAcrossDpi()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var screen = MultiMonitorHelper.GetMouseScreen();
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
            var curDpi = MultiMonitorHelper.GetWindowDpi(hwnd);
            if (curDpi == targetDpi)
            {
                return;
            }

            MultiMonitorHelper.MoveWindowPhysical(hwnd,
                screen.Bounds.X + screen.Bounds.Width / 2,
                screen.Bounds.Y + screen.Bounds.Height / 2);
        }

        /// <summary>
        /// Place the Switcheroo window in the center of the active screen (where the mouse is)
        /// </summary>
        private void CenterWindow()
        {
            var screen = _activeScreen ?? MultiMonitorHelper.GetMouseScreen();
            var dipBounds = MultiMonitorHelper.ToDIPBounds(screen);

            // Reset size every time to ensure that resolution changes take effect.
            // MaxWidth 约束窗口不超屏, 同时让 WrapPanel 标签在受限宽度下换行。
            Border.MaxHeight = dipBounds.Height;
            Border.MaxWidth = dipBounds.Width;

            // Force a rendering before repositioning the window
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.WidthAndHeight;

            // 强制同步完成 measure/arrange，使 ActualWidth/ActualHeight 反映当前内容尺寸。
            // 此前切换 SizeToContent 不触发布局，读到的仍是上一次布局的陈旧值，导致窗口偶尔不居中
            //（尤其跨屏时内容高度差异大，偏移明显——副屏触发概率最高）。
            UpdateLayout();

            var hwnd = new WindowInteropHelper(this).Handle;
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
            var curDpi = MultiMonitorHelper.GetWindowDpi(hwnd);
            if (hwnd != IntPtr.Zero && curDpi != targetDpi)
            {
                // 跨 DPI 监视器切换：窗口已由 PrepareWindowAcrossDpi 在显示前移到目标屏，
                // 但 WM_DPICHANGED 可能尚未处理完，DPI 上下文仍是旧屏，此时 Left/Top（DIP）
                // 会被按旧 DPI 换算成物理位置，而尺寸却按新 DPI 渲染 → 位置偏。
                // 先按当前上下文定位（保证窗口可见），等 DPI 上下文刷新并重新布局后修正。
                // 注意：不能用手动 SetWindowPos 跨屏移动窗口——显示后跨屏移动会破坏激活，
                // 触发 Deactivated → 立即隐藏（表现为“闪一下/不显示”）。
                SetCenteredPosition(dipBounds);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    SetCenteredPosition(dipBounds);
                }), DispatcherPriority.Loaded);
                return;
            }

            // Position the window in the center of the active screen
            SetCenteredPosition(dipBounds);
        }

        private void SetCenteredPosition(Rect dipBounds)
        {
            Left = dipBounds.X + (dipBounds.Width - ActualWidth) / 2;
            Top = dipBounds.Y + (dipBounds.Height - ActualHeight) / 2;
        }

        /// <summary>
        /// Switches the window associated with the selected item.
        /// </summary>
        private void Switch()
        {
            foreach (var item in lb.SelectedItems)
            {
                var win = (AppWindowViewModel)item;
                win.AppWindow.SwitchToLastVisibleActivePopup();
            }

            HideWindow();
        }

        private void HideWindow()
        {
            _hidePreviewTimer.Stop();
            HideThumbnailPreview();

            if (_windowCloser != null)
            {
                _windowCloser.Dispose();
                _windowCloser = null;
            }

            _altTabAutoSwitch = false;
            Opacity = 0;

            // 同步隐藏（原为 BeginInvoke(Hide, Input) 延迟隐藏）：
            // 延迟隐藏会让窗口短暂保持 Visibility=Visible（“幽灵可见”），
            // 此时立刻再次 Alt+Tab 会误判为已可见而走 else 分支不显示 → 表现为“闪一下”。
            // 同时消除隐藏瞬间残留一帧窗口背景的黑色闪影。
            Hide();
        }

        #endregion

        /// =================================

        #region Right-click menu functions

        /// =================================
        /// <summary>
        /// Show Options dialog.
        /// </summary>
        private void Options()
        {
            if (_optionsWindow == null)
            {
                _optionsWindow = new OptionsWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _optionsWindow.Closed += (sender, args) => _optionsWindow = null;
                _optionsWindow.ShowDialog();
            }
            else
            {
                _optionsWindow.Activate();
            }
        }

        /// <summary>
        /// Show About dialog.
        /// </summary>
        private void About()
        {
            if (_aboutWindow == null)
            {
                _aboutWindow = new AboutWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _aboutWindow.Closed += (sender, args) => _aboutWindow = null;
                _aboutWindow.ShowDialog();
            }
            else
            {
                _aboutWindow.Activate();
            }
        }

        /// <summary>
        /// Quit Switcheroo
        /// </summary>
        private void Quit()
        {
            _notifyIcon.Dispose();
            _notifyIcon = null;
            _hotkey.Dispose();
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Toggle alphabetical order program sort
        /// </summary>
        private void sortAZMenuItem_Click(MenuItem menuItem)
        {
            Toggle_sortWinList();
            menuItem.Checked = _sortWinList;
        }

        /// <summary>
        /// Context menu "Export to Json"
        /// </summary>
        private void exportToJSON_MenuItem_Click(MenuItem menuItem)
        {
            ExportToJSON();
        }

        #endregion

        /// =================================

        #region Event Handlers

        /// =================================
        private void OnClose(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideWindow();
        }

        private void hotkey_HotkeyPressed(object sender, EventArgs e)
        {
            if (!Settings.Default.EnableHotKey)
            {
                return;
            }

            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;

                _foregroundWindow = SystemWindow.ForegroundWindow;
                PrepareWindowAcrossDpi();
                // 隐藏状态下先加载数据与布局：内容就绪后再显示，避免窗口出现前的黑色背景帧
                LoadData(InitialFocus.NextItem);
                Show();
                Activate();
                Keyboard.Focus(tb);
                Opacity = 1;
            }
            else
            {
                HideWindow();
            }
        }

        private void AltTabPressed(object sender, AltTabHookEventArgs e)
        {
            if (!Settings.Default.AltTabHook)
            {
                // Ignore Alt+Tab presses if the hook is not activated by the user
                return;
            }

            _foregroundWindow = SystemWindow.ForegroundWindow;

            if (_foregroundWindow.ClassName == "MultitaskingViewFrame")
            {
                // If Windows' task switcher is on the screen then don't do anything
                return;
            }

            e.Handled = true;

            // 低层键盘钩子回调必须快速返回：若在回调内同步执行 Show/LoadData
            //（枚举窗口、UI 更新等耗时操作），Windows 判定钩子超时会让系统切换器
            // 接管 Alt+Tab —— 表现为窗口“闪一下”或完全不显示。
            // 显示逻辑延迟到 Dispatcher 队列执行。
            if (Visibility != Visibility.Visible)
            {
                var shiftDown = e.ShiftDown;

                // 立即武装 AutoSwitch：显示经 Dispatcher 延迟，快速松手（Alt up）也要能触发切换
                if (Settings.Default.AutoSwitch && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    _altTabAutoSwitch = true;
                }

                Dispatcher.BeginInvoke(new Action(() => ShowMainWindowFromAltTab(shiftDown)),
                    DispatcherPriority.Input);
            }
            else
            {
                if (e.ShiftDown)
                {
                    PreviousItem();
                }
                else
                {
                    NextItem();
                }
            }
        }

        private void ShowMainWindowFromAltTab(bool shiftDown)
        {
            if (Visibility == Visibility.Visible)
            {
                // 队列延迟期间窗口可能已被显示（如快速连按），此时只切换选中项
                if (shiftDown)
                {
                    PreviousItem();
                }
                else
                {
                    NextItem();
                }
                return;
            }

            tb.IsEnabled = true;

            // 隐藏状态下先加载数据与布局：内容就绪后再显示窗口，
            // 窗口第一帧即内容（消除出现前的黑色背景帧）。
            PrepareWindowAcrossDpi();
            if (shiftDown)
            {
                LoadData(InitialFocus.PreviousItem);
            }
            else
            {
                LoadData(InitialFocus.NextItem);
            }

            ActivateAndFocusMainWindow();

            Keyboard.Focus(tb);

            if (_altTabAutoSwitch)
            {
                tb.IsEnabled = false;
                tb.Text = Localization.Get("SearchPlaceholder");
            }

            // 内容已加载并完成布局，直接显示
            Opacity = 1;
        }

        private void ActivateAndFocusMainWindow()
        {
            // What happens below looks a bit weird, but for Switcheroo to get focus when using the Alt+Tab hook,
            // it is needed to simulate an Alt keypress will bring Switcheroo to the foreground. Otherwise Switcheroo
            // will become the foreground window, but the previous window will retain focus, and receive keep getting
            // the keyboard input.
            // http://www.codeproject.com/Tips/76427/How-to-bring-window-to-top-with-SetForegroundWindo

            var altKey = new KeyboardKey(Keys.Alt);
            var altKeyPressed = false;

            // Press the Alt key if it is not already being pressed
            if ((altKey.AsyncState & 0x8000) == 0)
            {
                altKey.Press();
                altKeyPressed = true;
            }

            // 在窗口显示前先把窗口移到目标屏（若跨 DPI）：显示后移动会破坏激活导致窗口立即隐藏
            PrepareWindowAcrossDpi();

            // Bring the Switcheroo window to the foreground
            Show();

            // Handle 必须在 Show() 之后获取：首次显示前 hwnd 尚不存在（Handle 为 0）。
            var thisWindowHandle = new WindowInteropHelper(this).Handle;
            var thisWindow = new AppWindow(thisWindowHandle);

            SystemWindow.ForegroundWindow = thisWindow;
            Activate();

            // Release the Alt key if it was pressed above
            if (altKeyPressed)
            {
                altKey.Release();
            }
        }

        //
        private void TextChanged(object sender, TextChangedEventArgs args)
        {
            if (!tb.IsEnabled)
            {
                return;
            }

            RefreshFilter();
        }

        /// <summary>
        /// 按当前搜索词与进程过滤刷新窗口列表。
        /// </summary>
        private void RefreshFilter()
        {
            // AutoSwitch 模式下 tb 被禁用且内容为占位提示，此时视为空查询
            var query = tb.IsEnabled ? tb.Text : "";

            // 无搜索词（且未拼入进程过滤）时短路：直接用全量列表，跳过匹配与高亮重算。
            // LoadData 已对全量窗口生成格式化标题，空查询下无需再走 WindowFilterer。
            if (string.IsNullOrWhiteSpace(query))
            {
                _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
                lb.DataContext = _filteredWindowList;
                if (lb.Items.Count > 0)
                {
                    lb.SelectedItem = lb.Items[0];
                }
                return;
            }

            if (!query.Contains(".") && this.processFilterText != "")
            {
                query = this.processFilterText + "." + query;
            }

            var foreground = _foregroundWindow ?? SystemWindow.ForegroundWindow;
            var context = new WindowFilterContext<AppWindowViewModel>
            {
                Windows = _unfilteredWindowList,
                ForegroundWindowProcessTitle = foreground.HWnd == IntPtr.Zero
                    ? ""
                    : new AppWindow(foreground.HWnd).ProcessTitle
            };

            var filterResults = new WindowFilterer().Filter(context, query).ToList();

            foreach (var filterResult in filterResults)
            {
                filterResult.AppWindow.FormattedTitle =
                    GetFormattedTitleFromBestResult(filterResult.WindowTitleMatchResults);
                filterResult.AppWindow.FormattedProcessTitle =
                    GetFormattedTitleFromBestResult(filterResult.ProcessTitleMatchResults);
            }

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(filterResults.Select(r => r.AppWindow));
            lb.DataContext = _filteredWindowList;
            if (lb.Items.Count > 0)
            {
                lb.SelectedItem = lb.Items[0];
            }
        }

        private static string GetFormattedTitleFromBestResult(IList<MatchResult> matchResults)
        {
            var bestResult = matchResults.FirstOrDefault(r => r.Matched) ?? matchResults.First();
            return new XamlHighlighter().Highlight(bestResult.StringParts);
        }

        private void OnEnterPressed(object sender, ExecutedRoutedEventArgs e)
        {
            Switch();
            e.Handled = true;
        }

        private void ListBoxItem_MouseLBClick(object sender, MouseButtonEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                Switch();
            }
            e.Handled = true;
        }

        private void ListBoxItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var item = sender as ListBoxItem;
            var viewModel = item?.DataContext as AppWindowViewModel;
            if (viewModel == null) return;

            _hidePreviewTimer.Stop();
            ShowThumbnailPreview(viewModel);
        }

        private void ListBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 延迟隐藏，避免鼠标经过缩略图窗口时反复显示/隐藏
            _hidePreviewTimer.Stop();
            _hidePreviewTimer.Start();
        }

        private void ShowThumbnailPreview(AppWindowViewModel viewModel)
        {
            if (_thumbnailPreviewWindow == null)
            {
                _thumbnailPreviewWindow = new ThumbnailPreviewWindow();
                _thumbnailPreviewWindow.Closed += (s, args) => _thumbnailPreviewWindow = null;
            }

            // 缩略图跟随鼠标所在屏定位（悬停时鼠标必然在当前屏），
            // 不能沿用 Alt+Tab 时的 _activeScreen，否则跨屏后位置/钳制错误。
            var screen = MultiMonitorHelper.GetMouseScreen();
            var dipBounds = MultiMonitorHelper.ToDIPBounds(screen);

            // 最大预览尺寸：屏幕内留出边距
            var maxW = dipBounds.Width - 80;
            var maxH = dipBounds.Height - 80;

            // 先按源窗口比例调整预览窗口尺寸并注册缩略图
            _thumbnailPreviewWindow.ShowThumbnail(viewModel.HWnd, maxW, maxH);

            var hwnd = new WindowInteropHelper(_thumbnailPreviewWindow).Handle;
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
            if (hwnd != IntPtr.Zero && MultiMonitorHelper.GetWindowDpi(hwnd) != targetDpi)
            {
                // 预览窗口是复用的单例，上次可能在别的 DPI 屏显示，其 DPI 上下文滞后。
                // 若不刷新，DIP 位置/尺寸会被按旧 DPI 解释 → 缩略图被错位放大。
                // 先物理移入目标屏触发 DPI 上下文刷新，再于布局后定位。
                MultiMonitorHelper.MoveWindowPhysical(hwnd,
                    screen.Bounds.X + screen.Bounds.Width / 2,
                    screen.Bounds.Y + screen.Bounds.Height / 2);

                Dispatcher.BeginInvoke(new Action(() => PositionThumbnailPreview(dipBounds)),
                    DispatcherPriority.Loaded);
                return;
            }

            // 再以调整后的实际尺寸定位到鼠标旁边
            PositionThumbnailPreview(dipBounds);
        }

        private void PositionThumbnailPreview(Rect dipBounds)
        {
            // 固定在主窗口右侧、垂直居中于主窗口；右侧放不下则移到左侧（不再跟随鼠标）
            const double gap = 24;
            var previewW = _thumbnailPreviewWindow.Width;
            var previewH = _thumbnailPreviewWindow.Height;

            double left;
            if (Left + ActualWidth + gap + previewW <= dipBounds.Right)
            {
                left = Left + ActualWidth + gap;
            }
            else
            {
                left = Left - gap - previewW;
            }

            double top = Top + (ActualHeight - previewH) / 2.0;

            // 钳制到屏幕内
            left = Math.Max(dipBounds.X, Math.Min(left, dipBounds.Right - previewW));
            top = Math.Max(dipBounds.Y, Math.Min(top, dipBounds.Bottom - previewH));

            // 已可见（悬停切换项）→ 吸附平滑移动；首次显示 → 淡入
            if (_thumbnailPreviewWindow.Visibility == Visibility.Visible)
            {
                _thumbnailPreviewWindow.MoveAnimated(left, top);
            }
            else
            {
                _thumbnailPreviewWindow.ShowAnimated(left, top);
            }
        }

        private void HideThumbnailPreview()
        {
            if (_thumbnailPreviewWindow != null)
            {
                _thumbnailPreviewWindow.HideAnimated();
            }
        }
        
        private void MenuItem_Click_toFront(object sender, RoutedEventArgs e)
        {
            Switch();
        }

        private async void MenuItem_Click_toClose(object sender, RoutedEventArgs e)
        {
            var windows = lb.SelectedItems.Cast<AppWindowViewModel>().ToList();
            foreach (var win in windows)
            {
                bool isClosed = await _windowCloser.TryCloseAsync(win);
                if (isClosed)
                    RemoveWindow(win);
            }

            if (lb.Items.Count == 0)
                HideWindow();
        }

        private void MenuItem_Duplicate(object sender, RoutedEventArgs e)
        {
            foreach (var item in lb.SelectedItems)
            {
                var torun = _unfilteredWindowList[lb.SelectedIndex].AppWindow.ExecutablePath.ToString();
                System.Diagnostics.Process.Start(torun);
            }

            HideWindow();
        }

        private async void CloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            var windows = lb.SelectedItems.Cast<AppWindowViewModel>().ToList();
            foreach (var win in windows)
            {
                bool isClosed = await _windowCloser.TryCloseAsync(win);
                if(isClosed)
                    RemoveWindow(win);
            }

            if (lb.Items.Count == 0)
                HideWindow();

            e.Handled = true;
        }

        private void RemoveWindow(AppWindowViewModel window)
        {
            int index = _filteredWindowList.IndexOf(window);
            if (index < 0)
                return;

            if (lb.SelectedIndex == index)
            {
                if (_filteredWindowList.Count > index + 1)
                    lb.SelectedIndex++;
                else
                {
                    if (index > 0)
                        lb.SelectedIndex--;
                }
            }

            _filteredWindowList.Remove(window);
            _unfilteredWindowList.Remove(window);
        }

        private void ScrollListUp(object sender, ExecutedRoutedEventArgs e)
        {
            PreviousItem();
            e.Handled = true;
        }

        private void PreviousItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != 0)
                {
                    lb.SelectedIndex--;
                }
                else
                {
                    lb.SelectedIndex = lb.Items.Count - 1;
                }

                ScrollSelectedItemIntoView();
            }
        }

        private void ScrollListDown(object sender, ExecutedRoutedEventArgs e)
        {
            NextItem();
            e.Handled = true;
        }

        private void NextItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != lb.Items.Count - 1)
                {
                    lb.SelectedIndex++;
                }
                else
                {
                    lb.SelectedIndex = 0;
                }

                ScrollSelectedItemIntoView();
            }
        }

        /// <summary>
        /// 鼠标滚轮上下切换选中项（与 Tab/方向键行为一致），而非默认的仅滚动视口。
        /// </summary>
        private void ListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
            {
                PreviousItem();
            }
            else if (e.Delta < 0)
            {
                NextItem();
            }
            e.Handled = true;
        }
        
        private void ScrollListPageUp(object sender, ExecutedRoutedEventArgs e)
        {
            double n = NumOfVisibleRows();

            if (lb.SelectedIndex - n >= 0)
                lb.SelectedIndex = Convert.ToInt32(lb.SelectedIndex - n);
            else
                lb.SelectedIndex = 0;
            ScrollSelectedItemIntoView();

            e.Handled = true;
        }

        private void ScrollListPageDown(object sender, ExecutedRoutedEventArgs e)
        {
            double n = NumOfVisibleRows();

            if (n + lb.SelectedIndex <= lb.Items.Count - 1)
                lb.SelectedIndex = Convert.ToInt32(n);
            else
                lb.SelectedIndex = lb.Items.Count - 1;
            ScrollSelectedItemIntoView();

            e.Handled = true;
        }

        private double NumOfVisibleRows()
        {
            return Math.Round(lb.ActualHeight / SearchGrid.ActualHeight); 
        }

        private void ScrollListHome(object sender, ExecutedRoutedEventArgs e)
        {
            lb.SelectedIndex = 0;
            ScrollSelectedItemIntoView();

            e.Handled = true;
        }

        private void ScrollListEnd(object sender, ExecutedRoutedEventArgs e)
        {
            lb.SelectedIndex = lb.Items.Count-1;
            ScrollSelectedItemIntoView();

            e.Handled = true;
        }
        
        private void ScrollSelectedItemIntoView()
        {
            var selectedItem = lb.SelectedItem;
            if (selectedItem != null)
            {
                lb.ScrollIntoView(selectedItem);
            }
        }

        private void MainWindow_OnLostFocus(object sender, EventArgs e)
        {
            HideWindow();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            DisableSystemMenu();
            EnableRoundedCorners();
            TryApplyUiFont();
            HookUpSelectionHighlight();
        }

        /// <summary>
        /// 选中高亮滑块：选中项变化时在列表项间平滑上下滑动（替代背景渐隐渐显）。
        /// </summary>
        private void HookUpSelectionHighlight()
        {
            lb.SelectionChanged += (s, e) => UpdateSelectionHighlight(true);
            lb.SizeChanged += (s, e) => UpdateSelectionHighlight(false);
            var scrollViewer = lb.Template?.FindName("ScrollViewer", lb) as System.Windows.Controls.ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollChanged += (s, e) => UpdateSelectionHighlight(false);
            }
            UpdateSelectionHighlight(false);
        }

        private void UpdateSelectionHighlight(bool animate)
        {
            if (lb == null || lb.Template == null)
            {
                return;
            }

            var highlight = lb.Template.FindName("SelectionHighlight", lb) as FrameworkElement;
            var translate = lb.Template.FindName("HighlightTranslate", lb) as TranslateTransform;
            if (highlight == null || translate == null)
            {
                return;
            }

            if (lb.SelectedIndex < 0 || lb.SelectedIndex >= lb.Items.Count)
            {
                highlight.Visibility = Visibility.Collapsed;
                return;
            }

            var container = lb.ItemContainerGenerator.ContainerFromIndex(lb.SelectedIndex) as ListBoxItem;
            if (container == null)
            {
                // 容器未生成（虚拟化/未布局）
                highlight.Visibility = Visibility.Collapsed;
                return;
            }

            // item 相对 ListBox 视口的坐标（含滚动偏移）
            var pos = container.TransformToAncestor(lb).Transform(new Point(0, 0));
            double targetY = pos.Y;

            highlight.Visibility = Visibility.Visible;
            highlight.Height = container.ActualHeight;
            if (animate)
            {
                // 吸附质感：快速冲过目标再吸回（轻微过冲），而非匀速平滑
                var anim = new DoubleAnimation(translate.Y, targetY, TimeSpan.FromMilliseconds(180));
                anim.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
                translate.BeginAnimation(TranslateTransform.YProperty, anim);
            }
            else
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = targetY;
            }
        }

        /// <summary>
        /// 尝试使用构建目录 fonts/ 下的自定义字体（如霞鹜文楷）。
        /// 仅按相对路径查找（不硬编码本机路径），缺失时静默回退系统默认字体，不影响可读性。
        /// </summary>
        private void TryApplyUiFont()
        {
            try
            {
                var fontPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", "LXGWWenKai-Regular.ttf");
                if (!System.IO.File.Exists(fontPath))
                {
                    return;
                }

                // 用完整 "file:///...#family" 字符串形式：WPF 才按文件加载；
                // 传 Uri+family 会退化为纯 family 名去系统搜索字体（系统未安装 → 回退默认）。
                var family = new FontFamily("file:///" + fontPath.Replace('\\', '/') + "#LXGW WenKai");
                FontFamily = family;
                tb.FontFamily = family;
                lb.FontFamily = family;
            }
            catch
            {
                // 字体损坏或加载失败时回退系统字体
            }
        }

        /// <summary>
        /// Windows 11 下给无边框窗口启用 DWM 圆角，视觉更现代。
        /// 不依赖 AllowsTransparency（避免多 DPI 下分层窗口模糊），Windows 10 及更早直接忽略。
        /// </summary>
        private void EnableRoundedCorners()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }
                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
                const int DWMWCP_ROUND = 2;
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Windows 10 或更早（build < 22000）不支持该属性，忽略即可
            }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        private void DisableSystemMenu()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var window = new SystemWindow(windowHandle);
            window.Style = window.Style & ~WindowStyleFlags.SYSMENU;
        }

        private void ShowHelpTextBlock_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var duration = new Duration(TimeSpan.FromSeconds(0.150));
            var newHeight = HelpPanel.Height > 0 ? 0 : +17;
            HelpPanel.BeginAnimation(HeightProperty, new DoubleAnimation(HelpPanel.Height, newHeight, duration));
        }

        #endregion

        private enum InitialFocus
        {
            NextItem,
            PreviousItem
        }
        
        void Toggle_sortWinList()
        {
            _sortWinList = !_sortWinList;
            if (_sortAZMenuItem != null)
            {
                _sortAZMenuItem.Checked = _sortWinList;
            }
        }
        
    }
}
