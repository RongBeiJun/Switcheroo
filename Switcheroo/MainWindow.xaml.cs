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

        // 淡出动画进行中：窗口仍 Visible，但逻辑上应视为已隐藏（避免快速再次 Alt+Tab 误判）
        private bool _isHiding;

        // 逻辑显示状态：窗口常驻屏外缓存（Visibility 恒 Visible），显示/隐藏由该标志与屏外坐标控制
        private bool _isWindowShown;

        // 窗口已稳定显示（定位/DPI 布局完成）后才刷新缩略图，避免唤出瞬间缩略图出现在错误位置
        private bool _showStable;

        // 最近一次完成显示的时机：跨 DPI 激活不稳定（DPI 布局破坏焦点）时，
        // 若用户仍按住 Alt 浏览则重新激活；Alt 已释放（主动切换）则正常隐藏
        private DateTime _shownTimestamp = DateTime.MinValue;

        // 浏览中焦点维持：AutoSwitch 按住 Alt 期间，定期把焦点拉回窗口，
        // 对抗"唤出后 8-17ms 系统把焦点交给 Alt+Tab 目标"导致的失焦→消失
        private readonly System.Windows.Threading.DispatcherTimer _keepFocusTimer;
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

            _keepFocusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _keepFocusTimer.Tick += (s, e) => KeepFocusTick();
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
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                tb.Foreground = processFilterText == process ? Brushes.Red : Brushes.Black;
                tb.Margin = new Thickness(2, 0, 2, 0);
                tb.MouseDown += TbProcessFilter_MouseDown;
                // 悬停反馈：变为高亮蓝（选中态红色保持不变）
                tb.MouseEnter += (s, e) =>
                {
                    if (processFilterText != (tb.Tag as string)) tb.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9));
                };
                tb.MouseLeave += (s, e) =>
                {
                    tb.Foreground = processFilterText == (tb.Tag as string) ? Brushes.Red : Brushes.Black;
                };
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
                    if (Settings.Default.AutoSwitch && tb.IsEnabled)
                    {
                        // 已处于搜索状态 → 再次 Alt+Q 退出搜索，恢复 AutoSwitch 浏览/切换态
                        tb.Text = "";
                        tb.IsEnabled = false;
                        _altTabAutoSwitch = true;
                        tb.Text = Localization.Get("SearchPlaceholder");
                        RefreshFilter();
                        lb.Focus();
                    }
                    else
                    {
                        // 进入搜索状态：暂停自动切换，聚焦搜索框
                        _altTabAutoSwitch = false;
                        tb.Text = "";
                        tb.IsEnabled = true;
                        tb.Focus();
                    }
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
                else if (args.SystemKey == Key.LeftAlt && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _altTabAutoSwitch)
                {
                    LogDebug("AltKeyUp(SystemKey) → Switch, _altTabAutoSwitch=" + _altTabAutoSwitch);
                    Switch();
                }
                else if (args.Key == Key.LeftAlt && _altTabAutoSwitch)
                {
                    LogDebug("AltKeyUp(Key) → Switch, _altTabAutoSwitch=" + _altTabAutoSwitch);
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
                // 托盘唤出同样先退出后台模式
                EndBackgroundMode();
                if (!_isWindowShown || _isHiding)
                {
                    _isWindowShown = true;
                    // 先置为全透明（移动/DPI 更新全程透明）
                    PrepareFadeIn();
                   _foregroundWindow = SystemWindow.ForegroundWindow;
                    PrepareWindowAcrossDpi();
                    // 隐藏状态下先加载数据与布局：内容就绪后再显示，避免窗口出现前的黑色背景帧
                    LoadData(InitialFocus.NextItem);
                    CenterWindow();
                    tb.IsEnabled = true;
                    tb.Text = "";
                    CompleteShowForCurrentScreen();
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

            // 排除自身（窗口常驻屏外缓存，是可见顶层窗口，否则会出现在自己的切换列表里）
            var selfHwnd = new WindowInteropHelper(this).Handle;
            var windows = GetWindowSnapshot()
                .Where(w => w.HWnd != selfHwnd)
                .Select(window => new AppWindowViewModel(window)).ToList();
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
            if (window1 == null || window2 == null)
            {
                return false;
            }
            if (window1.HWnd == window2.HWnd)
            {
                return true;
            }
            // Process 在启动早期/特殊窗口可能不可用（拿不到进程句柄）
            var process1 = window1.Process;
            var process2 = window2.Process;
            return process1 != null && process2 != null && process1.Id == process2.Id;
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

            // 不再手动切换 SizeToContent（窗口保持 XAML 的 WidthAndHeight，内容更新后自动调整尺寸）：
            // 强制 Manual↔WidthAndHeight 会触发 hwnd resize → 重定向表面重建 → DWM 黑帧。
            // 跨屏/分辨率变化由 PrepareWindowAcrossDpi 触发 WM_DPICHANGED 自动重新布局。

            // 强制同步完成 measure/arrange，使 ActualWidth/ActualHeight 反映当前内容尺寸。
            // 此前切换 SizeToContent 不触发布局，读到的仍是上一次布局的陈旧值，导致窗口偶尔不居中
            //（尤其跨屏时内容高度差异大，偏移明显——副屏触发概率最高）。
            UpdateLayout();

            // 跨 DPI 时的位置精度修正交给 CompleteShow（等渲染稳定、DPI 刷新后、激活前执行），
            // 不在激活后延迟移动窗口（会破坏激活 → Deactivated → 闪一下消失）。
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
            LogDebug("Switch() 执行");
            var targets = lb.SelectedItems.Cast<AppWindowViewModel>().ToList();

            // 单目标 + 缩略图可见 → “缩略图生长”动画：缩略图平滑放大到目标窗口位置（内容实时、
            // 像素缩放无白帧），放大到位后无缝切入目标窗口。多选框/无缩略图走下方原有路径。
            if (targets.Count == 1 && TryPlayThumbnailZoom(targets[0]))
            {
                return;
            }

            foreach (var item in targets)
            {
                var win = (AppWindowViewModel)item;
                // 授予前台权限：模拟一次 Alt 键。Switcheroo 是后台进程（低层钩子），
                // 直接 SetForegroundWindow 目标会被前台锁定拒绝（目标只任务栏闪烁提醒）；
                // Windows 允许"响应 Alt 键"的进程切换前台。此时用户已松开 Alt，无副作用。
                SimulateAltGrant();
                win.AppWindow.SwitchToLastVisibleActivePopup();
                // AttachThreadInput 技巧双保险
                ForceForegroundWindow(win.AppWindow.HWnd);
            }

            HideWindow();
        }

        /// <summary>
        /// “缩略图生长”切换动画：缩略图窗口从当前位置平滑放大到目标窗口的物理位置与大小，
        /// 主窗口同步淡出；动画完成后切换目标窗口（内容与放大后的缩略图一致，无缝衔接）并隐藏缩略图。
        /// 返回 false 表示不满足动画条件（无缩略图 / 目标最小化 / 矩形无效），按原有方式直接切换。
        /// </summary>
        private bool TryPlayThumbnailZoom(AppWindowViewModel target)
        {
            if (_thumbnailPreviewWindow == null ||
                _thumbnailPreviewWindow.Visibility != Visibility.Visible)
            {
                return false;
            }

            var targetHwnd = target.AppWindow.HWnd;
            // 目标最小化时无可见内容可放大（且缩略图通常也为空），走原有切换
            if (IsIconic(targetHwnd))
            {
                return false;
            }

            var previewHwnd = new WindowInteropHelper(_thumbnailPreviewWindow).Handle;
            RECT pf;
            RECT tf;
            if (!GetWindowRect(previewHwnd, out pf) || !GetWindowRect(targetHwnd, out tf))
            {
                return false;
            }
            if (tf.Right - tf.Left <= 0 || tf.Bottom - tf.Top <= 0)
            {
                return false;
            }

            _thumbnailPreviewWindow.AnimateScaleTo(tf.Left, tf.Top, tf.Right - tf.Left, tf.Bottom - tf.Top, 160,
                prepare: () =>
                {
                    // 动画临近完成：提前切入目标窗口（真实窗口与放大中的缩略图融合，过渡无缝）
                    SimulateAltGrant();
                    target.AppWindow.SwitchToLastVisibleActivePopup();
                    ForceForegroundWindow(target.AppWindow.HWnd);
                },
                completed: () =>
                {
                    // 动画完成：隐藏缩略图，露出真实窗口（位置尺寸一致，无跳变）
                    if (_thumbnailPreviewWindow != null)
                    {
                        _thumbnailPreviewWindow.HideThumbnail();
                    }
                });

            // 主窗口同步淡出（缩小 hidePreview=false：缩略图保留放大动画待切入）
            HideWindow(false);
            return true;
        }

        /// <summary>
        /// 授予 SetForegroundWindow 前台权限（模拟 Alt 键按下/抬起）。
        /// </summary>
        private static void SimulateAltGrant()
        {
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private const uint KEYEVENTF_KEYUP = 0x2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // ---- 后台进程化：常驻托盘/钩子状态下把自身降为“后台进程”，降低系统资源占用 ----

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        // PROCESS_MODE_BACKGROUND_BEGIN/END（Windows 8+）：进入后台模式后系统自动降低本进程的
        // CPU 调度优先级、磁盘 IO 优先级与内存优先级，并优先回收其内存页 —— 官方“后台任务”机制。
        private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
        private const uint PROCESS_MODE_BACKGROUND_END = 0x00200000;

        /// <summary>进入后台模式（空闲/隐藏状态）：降低 CPU/IO/内存优先级，让系统优先回收资源。</summary>
        private void BeginBackgroundMode()
        {
            try
            {
                SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_BEGIN);
            }
            catch
            {
            }
        }

        /// <summary>退出后台模式（唤出显示）：恢复普通调度，保证显示与交互流畅。</summary>
        private void EndBackgroundMode()
        {
            try
            {
                SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_END);
            }
            catch
            {
            }
        }

        /// <summary>隐藏完成后延迟裁剪工作集：进一步把空闲内存页还给系统（不影响淡出动画）。</summary>
        private void TrimWorkingSetLater()
        {
            var trimmer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            trimmer.Tick += (s, e) =>
            {
                trimmer.Stop();
                if (!_isWindowShown)
                {
                    try
                    {
                        EmptyWorkingSet(GetCurrentProcess());
                    }
                    catch
                    {
                    }
                }
            };
            trimmer.Start();
        }

        /// <summary>
        /// 给主窗口加 WS_EX_TOOLWINDOW（工具窗口）：任务管理器"应用/后台进程"分类依据是进程是否有
        /// 可见的非工具顶层窗口；主窗口常驻透明可见（显示方案必须），加工具窗口样式后任务管理器将其
        /// 归为"后台进程"（工具窗口不显示在任务栏/Alt+Tab，Switcheroo 本就拦截 Alt+Tab，无功能影响）。
        /// </summary>
        private void MakeWindowToolWindow()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    const int WS_EX_TOOLWINDOW = 0x80;
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
                }
            }
            catch
            {
            }
        }

        private void HideWindow(bool hidePreview = true)
        {
            _hidePreviewTimer.Stop();
            _keepFocusTimer.Stop();
            if (hidePreview)
            {
                HideThumbnailPreview();
            }

            if (_windowCloser != null)
            {
                _windowCloser.Dispose();
                _windowCloser = null;
            }

            _altTabAutoSwitch = false;

            // 已隐藏或正在淡出：不重复处理
            if (!_isWindowShown || _isHiding)
            {
                return;
            }

            _isWindowShown = false;
            _isHiding = true;
            _showStable = false;
            LogDebug("HideWindow 淡出开始 Opacity=" + Opacity);

            // 透明淡出后停屏内（AllowsTransparency 真透明 → 无残留黑窗/边框）——退出加快到 80ms
            var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(80));
            fadeOut.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            fadeOut.Completed += (s, e) =>
            {
                Opacity = 0;
                _isHiding = false;
                LogDebug("HideWindow 淡出完成");
                // 隐藏完成即空闲：退回后台模式，并延迟裁剪工作集降低内存占用
                BeginBackgroundMode();
                TrimWorkingSetLater();
            };
            BeginAnimation(OpacityProperty, fadeOut);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;

        /// <summary>
        /// 显示前把窗口置为全透明（定位完成、动画前调用）。
        /// </summary>
        private void PrepareFadeIn()
        {
            _isHiding = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
        }

        /// <summary>
        /// 启动透明淡入动画（窗口内容已缓存渲染，无黑闪，可放心做纯淡入）。
        /// </summary>
        private void AnimateFadeIn()
        {
            LogDebug("AnimateFadeIn 淡入开始");
            // 进入减缓到 180ms（与缩略图进入动画一致）
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            fadeIn.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, fadeIn);
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

            if (!_isWindowShown || _isHiding)
            {
                _isWindowShown = true;
                tb.IsEnabled = true;

                // 先置为全透明（移动/DPI 更新全程透明）
                PrepareFadeIn();

                _foregroundWindow = SystemWindow.ForegroundWindow;
                PrepareWindowAcrossDpi();
                // 隐藏状态下先加载数据与布局：内容就绪后再显示，避免窗口出现前的黑色背景帧
                LoadData(InitialFocus.NextItem);
                CenterWindow();
                CompleteShowForCurrentScreen();
            }
            else
            {
                HideWindow();
            }
        }

        private void AltTabPressed(object sender, AltTabHookEventArgs e)
        {
            // 唤出链路开始：先退出后台模式恢复普通调度（hook 回调在高优先级的程序上执行），
            // 避免显示/交互受后台模式低优先级影响
            EndBackgroundMode();

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
            if (!_isWindowShown || _isHiding)
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
            if (_isWindowShown && !_isHiding)
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

            LogDebug("AltTab显示进入 shiftDown=" + shiftDown);
            _isWindowShown = true;
            tb.IsEnabled = true;

            // 先置为全透明：移动/DPI 更新全程透明，避免目标位置透出黑色表面
            PrepareFadeIn();

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

            // 定位到目标屏中心（窗口透明，移动无感）
            CenterWindow();

            // 按 DPI 分流完成显示（同屏同步激活，跨 DPI 等渲染稳定）
            CompleteShowForCurrentScreen();
        }

        /// <summary>
        /// 显示路径的统一"完成显示"：同屏直接同步激活（历史稳定路径，无延迟）；
        /// 跨 DPI 等渲染稳定后再激活（避免激活后重布局破坏焦点）。
        /// </summary>
        private void CompleteShowForCurrentScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(
                _activeScreen ?? MultiMonitorHelper.GetMouseScreen()));
            var curDpi = MultiMonitorHelper.GetWindowDpi(hwnd);
            LogDebug("CompleteShowForCurrentScreen curDpi=" + curDpi + " target=" + targetDpi
                + " → " + (hwnd != IntPtr.Zero && curDpi != targetDpi ? "跨DPI延迟" : "同步"));
            if (hwnd != IntPtr.Zero && curDpi != targetDpi)
            {
                FinishShowAfterStable();
            }
            else
            {
                CompleteShowCore();
            }
        }

        /// <summary>
        /// 跨 DPI 唤出：轮询等窗口 DPI 真正对齐目标屏（WM_DPICHANGED 已处理、布局稳定）后再激活。
        /// 若在 DPI 刷新前激活，激活后的 DPI 重布局会破坏焦点 → Deactivated → 窗口消失
        ///（表现为"每次换屏后第一次唤出消失，第二次才正常"）。
        /// </summary>
        private void FinishShowAfterStable()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = _activeScreen ?? MultiMonitorHelper.GetMouseScreen();
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
            if (hwnd == IntPtr.Zero)
            {
                CompleteShow();
                return;
            }

            var started = DateTime.Now;
            var checker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            checker.Tick += (s, e) =>
            {
                if (MultiMonitorHelper.GetWindowDpi(hwnd) == targetDpi ||
                    (DateTime.Now - started).TotalMilliseconds > 500)
                {
                    checker.Stop();
                    CompleteShow();
                }
            };
            checker.Start();
        }

        /// <summary>
        /// 跨 DPI 稳定后的完成路径：按新 DPI 精确定位（激活前）→ 常规完成。
        /// 竞态防护：等待期间窗口可能已被隐藏（用户快速切换/退出）→ 不再重新显示。
        /// </summary>
        private void CompleteShow()
        {
            LogDebug("CompleteShow(跨DPI稳定后) 执行, shown=" + _isWindowShown + " hiding=" + _isHiding);
            if (!_isWindowShown || _isHiding)
            {
                return;
            }
            var screen = _activeScreen ?? MultiMonitorHelper.GetMouseScreen();
            var dipBounds = MultiMonitorHelper.ToDIPBounds(screen);
            // CenterWindow 时 DPI 可能尚未刷新，这里按刷新后的新 DPI 精准居中（激活前完成，不再移动已激活窗口）
            SetCenteredPosition(dipBounds);
            CompleteShowCore();
        }

        /// <summary>
        /// 浏览中焦点维持：AutoSwitch 按住 Alt 期间窗口失焦（系统 Alt+Tab 焦点转移/DPI 布局干扰）
        /// 时，定期把焦点拉回窗口，避免"唤出后立即消失"。
        /// </summary>
        private void KeepFocusTick()
        {
            if (!_isWindowShown || _isHiding)
            {
                _keepFocusTimer.Stop();
                return;
            }
            var altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (altDown)
            {
                // 浏览中（Alt 按住）：拉回焦点
                if (!IsKeyboardFocusWithin)
                {
                    LogDebug("KeepFocus → ForceActivate()");
                    ForceActivate();
                }
            }
            else if (_altTabAutoSwitch)
            {
                // Alt 已松开：按住 Alt+Tab 时前台被系统锁定，焦点一直在 Alt+Tab 目标 → keyUp 收不到、
                // 松开时也无 Deactivated 事件 → 依赖这里主动切换退出
                LogDebug("KeepFocus → Alt已松开 → Switch()");
                Switch();
            }
        }

        /// <summary>
        /// 常规完成显示：激活 → 刷新选中项缩略图 → 淡入。
        /// 同屏唤出直接调用（历史稳定路径，无额外延迟）。
        /// </summary>
        private void CompleteShowCore()
        {
            _shownTimestamp = DateTime.Now;
            LogDebug("CompleteShowCore 显示完成");

            ActivateAndFocusMainWindow();
            Keyboard.Focus(tb);

            if (_altTabAutoSwitch)
            {
                tb.IsEnabled = false;
                tb.Text = Localization.Get("SearchPlaceholder");
                _keepFocusTimer.Start();
            }

            // 窗口已稳定，允许缩略图跟随选中/悬停刷新
            _showStable = true;

            // 强制刷新选中高亮：LoadData 设置首项选中时 ItemContainer 可能尚未生成（滑块被 Collapsed），
            // 布局稳定后补一次刷新让蓝色滑块显示
            UpdateSelectionHighlight(false);

            // 常显缩略图：主窗口定位稳定后显示选中项（正确位置）
            var selected = lb.SelectedItem as AppWindowViewModel;
            if (selected != null)
            {
                ShowThumbnailPreview(selected);
            }

            AnimateFadeIn();
        }

        private void ActivateAndFocusMainWindow()
        {
            // What happens below looks a bit weird, but for Switcheroo to get focus when using the Alt+Tab hook,
            // it is needed to simulate an Alt keypress will bring Switcheroo to the foreground. Otherwise Switcheroo
            // will become the foreground window, but the previous window will retain focus, and receive keep getting
            // the keyboard input.
            // http://www.codeproject.com/Tips/76427/How-to-bring-window-to-top-with-SetForegroundWindo

            // 用 GetAsyncKeyState（全局物理按键状态）判断 Alt 是否被按住：
            // AsyncState（GetKeyState）只反映本线程消息队列，低层钩子拦截 Alt+Tab 后 UI 线程收不到 Alt
            // 消息 → 误判未按住 → 错误地模拟 Alt press/release → 触发焦点转移 → Deactivated → 窗口消失。
            var altKey = new KeyboardKey(Keys.Alt);
            var altKeyPressed = false;
            if ((GetAsyncKeyState(VK_MENU) & 0x8000) == 0)
            {
                altKey.Press();
                altKeyPressed = true;
            }

            // 在窗口显示前先把窗口移到目标屏（若跨 DPI）：显示后移动会破坏激活导致窗口立即隐藏
            PrepareWindowAcrossDpi();

            // Bring the Switcheroo window to the foreground
            Show();

            // 强制激活（绕过前台锁定，按住 Alt 时也能把焦点给 Switcheroo）
            ForceActivate();

            // Release the Alt key if it was pressed above
            if (altKeyPressed)
            {
                altKey.Release();
            }
        }

        private const int VK_MENU = 0x12;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 强制把指定窗口设为前台（AttachThreadInput 绕过 Windows 前台锁定）。
        /// </summary>
        private static void ForceForegroundWindow(IntPtr hwnd)
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            if (foregroundThread != currentThread)
            {
                AttachThreadInput(foregroundThread, currentThread, true);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                AttachThreadInput(foregroundThread, currentThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }

        /// <summary>
        /// 强制激活（AttachThreadInput 绕过 Windows 前台锁定）：
        /// 按住 Alt 浏览时普通 Activate() 受前台锁定限制无效（焦点抢不回 → Alt KeyUp 收不到 → 松开 Alt 不退出）。
        /// </summary>
        private void ForceActivate()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ForceForegroundWindow(hwnd);
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

            // 无搜索词且无进程过滤时短路：直接用全量列表，跳过匹配与高亮重算。
            // LoadData 已对全量窗口生成格式化标题；但进程过滤（点击标签/Alt+数字）需走正常过滤。
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrEmpty(this.processFilterText))
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
            // 主窗口逻辑隐藏时（淡出中/已隐藏）忽略悬停，避免残留黑色缩略图窗口
            if (!_isWindowShown)
            {
                return;
            }

            var item = sender as ListBoxItem;
            var viewModel = item?.DataContext as AppWindowViewModel;
            if (viewModel == null) return;

            _hidePreviewTimer.Stop();
            ShowThumbnailPreview(viewModel);
        }

        private void ListBox_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isWindowShown)
            {
                return;
            }

            // 常显缩略图：鼠标离开列表后恢复显示选中项对应的预览（而非隐藏）
            var selected = lb.SelectedItem as AppWindowViewModel;
            if (selected != null)
            {
                ShowThumbnailPreview(selected);
            }
        }

        private void ShowThumbnailPreview(AppWindowViewModel viewModel)
        {
            // 主窗口逻辑隐藏时（淡出中/已隐藏）不再显示缩略图，避免残留黑色预览窗口
            if (!_isWindowShown)
            {
                HideThumbnailPreview();
                return;
            }

            if (_thumbnailPreviewWindow == null)
            {
                _thumbnailPreviewWindow = new ThumbnailPreviewWindow();
                _thumbnailPreviewWindow.Closed += (s, args) => _thumbnailPreviewWindow = null;
            }

            // 停止可能残留的“切换放大”动画（否则其 Rendering 回调会继续 SetWindowPos 干扰本次正常显示）
            _thumbnailPreviewWindow.StopScaleAnimation();

            // 缩略图固定显示在主窗口右侧，定位用主窗口所在屏（_activeScreen）；
            // 用鼠标屏在跨 DPI 键盘唤起时鼠标可能停在旧屏 → 缩略图错误出现在主屏幕
            var screen = _activeScreen ?? MultiMonitorHelper.GetMouseScreen();
            var dipBounds = MultiMonitorHelper.ToDIPBounds(screen);

            // 最大预览尺寸：屏幕内留出边距
            var maxW = dipBounds.Width - 80;
            var maxH = dipBounds.Height - 80;

            var hwnd = new WindowInteropHelper(_thumbnailPreviewWindow).Handle;
            var targetDpi = (int)Math.Round(96 * MultiMonitorHelper.GetDpiScale(screen));
            if (hwnd != IntPtr.Zero && MultiMonitorHelper.GetWindowDpi(hwnd) != targetDpi)
            {
                // 预览窗口 DPI 滞后（上次在别的 DPI 屏显示）：若直接 Show 会先在旧位置闪一帧。
                // 先真正隐藏（Opacity=0 在非分层窗口会显示黑块）→ 注册/适配源（不显示）→
                // 物理移入目标屏触发 DPI 刷新 → 轮询等 DPI 真正对齐（不赌渲染时序）→
                // 重新拟合尺寸 + 物理定位显示 → 保证正确屏幕正确位置，无首帧错位。
                _thumbnailPreviewWindow.Hide();
                _thumbnailPreviewWindow.ShowThumbnail(viewModel.HWnd, maxW, maxH, show: false);
                MultiMonitorHelper.MoveWindowPhysical(hwnd,
                    screen.Bounds.X + screen.Bounds.Width / 2,
                    screen.Bounds.Y + screen.Bounds.Height / 2);

                var started = DateTime.Now;
                var checker = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                checker.Tick += (s2, e2) =>
                {
                    if (MultiMonitorHelper.GetWindowDpi(hwnd) == targetDpi ||
                        (DateTime.Now - started).TotalMilliseconds > 500)
                    {
                        checker.Stop();
                        // DPI 已对齐：按新 DPI 重新拟合尺寸并定位
                        _thumbnailPreviewWindow.RefitToSource(maxW, maxH);
                        PositionThumbnailPreview(dipBounds);
                    }
                };
                checker.Start();
                return;
            }

            // 先按源窗口比例调整预览窗口尺寸并注册缩略图，再定位
            _thumbnailPreviewWindow.ShowThumbnail(viewModel.HWnd, maxW, maxH);
            PositionThumbnailPreview(dipBounds);
        }

        private void PositionThumbnailPreview(Rect dipBounds)
        {
            LogPreviewDebug("Position开始");
            var mainHwnd = new WindowInteropHelper(this).Handle;
            var previewHwnd = new WindowInteropHelper(_thumbnailPreviewWindow).Handle;
            if (mainHwnd == IntPtr.Zero || previewHwnd == IntPtr.Zero)
            {
                return;
            }

            RECT mainRect;
            RECT prevRect;
            if (!GetWindowRect(mainHwnd, out mainRect) || !GetWindowRect(previewHwnd, out prevRect))
            {
                return;
            }

            int previewW = prevRect.Right - prevRect.Left;
            int previewH = prevRect.Bottom - prevRect.Top;
            if (previewW <= 0 || previewH <= 0)
            {
                return;
            }

            // 全部用物理像素坐标：主窗口右侧垂直居中，右侧放不下移到左侧，钳制到目标屏
            var screen = _activeScreen ?? MultiMonitorHelper.GetMouseScreen();
            var sb = screen.Bounds;
            const int gap = 24;
            int left = mainRect.Right + gap;
            int top = mainRect.Top + (mainRect.Bottom - mainRect.Top - previewH) / 2;
            if (left + previewW > sb.X + sb.Width)
            {
                left = mainRect.Left - gap - previewW;
            }
            left = Math.Max(sb.X, Math.Min(left, sb.X + sb.Width - previewW));
            top = Math.Max(sb.Y, Math.Min(top, sb.Y + sb.Height - previewH));

            // 同屏微调（位置差小、真实显示中）→ 物理坐标平滑移动（新 MoveAnimated 内部物理插值，
            // 起点取当前物理矩形，不依赖 WPF Left/Top，跨 DPI 无错屏）；
            // 跨屏/首次/残留 → 物理 SetWindowPos 精确定位 + 纯淡入（杜绝 DIP 换算错屏）
            int dist = Math.Abs(prevRect.Left - left) + Math.Abs(prevRect.Top - top);
            if (_thumbnailPreviewWindow.Visibility == Visibility.Visible &&
                _thumbnailPreviewWindow.Opacity > 0.01 &&
                dist < 400)
            {
                _thumbnailPreviewWindow.MoveAnimated(left, top);
            }
            else
            {
                MultiMonitorHelper.MoveWindowPhysical(previewHwnd, left, top);
                _thumbnailPreviewWindow.ShowFadeIn();
            }
            LogPreviewDebug("Position结束 left=" + left + " top=" + top);
        }

        /// <summary>
        /// 调试日志（发布版为空操作）。
        /// </summary>
        private void LogDebug(string msg)
        {
        }

        private void LogPreviewDebug(string stage)
        {
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private void HideThumbnailPreview()
        {
            if (_thumbnailPreviewWindow != null)
            {
                _thumbnailPreviewWindow.HideAnimated();

                // 兜底：若淡出动画被鼠标事件打断，300ms 后强制隐藏，杜绝残留黑色预览窗口
                var guard = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                guard.Tick += (s, e) =>
                {
                    guard.Stop();
                    if (_thumbnailPreviewWindow != null &&
                        _thumbnailPreviewWindow.Visibility == Visibility.Visible)
                    {
                        _thumbnailPreviewWindow.Hide();
                    }
                };
                guard.Start();
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

        private async void MainWindow_OnLostFocus(object sender, EventArgs e)
        {
            LogDebug("OnLostFocus 触发");
            // 跨 DPI 唤起时激活状态可能短暂波动，延迟确认再处理
            await Task.Delay(150);
            if (!_isWindowShown || IsKeyboardFocusWithin)
            {
                return;
            }

            var altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            // AutoSwitch 浏览中（Alt 按住）：焦点由 KeepFocusTimer 主动维持，此时不隐藏
            if (altDown && _altTabAutoSwitch)
            {
                LogDebug("OnLostFocus → 浏览中(Alt按住)，交给焦点维持");
                return;
            }

            LogDebug("OnLostFocus → HideWindow(Alt已释放)");
            HideWindow();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            DisableSystemMenu();
            // 分层窗口（AllowsTransparency）下 DWM 圆角属性会绘制不受 Opacity 控制的圆角边框，
            // 造成透明后残留边框 → 圆角改由根 Border 的 CornerRadius 承担，不再启用 DWM 圆角
            //EnableRoundedCorners();
            TryApplyUiFont();
            HookUpSelectionHighlight();
            PrepareOffscreenCache();
            // 任务管理器分类依据 = 进程是否有可见的非工具顶层窗口。主窗口常驻透明可见，
            // 加 WS_EX_TOOLWINDOW 让任务管理器把它归为"后台进程"（不影响功能，见 MakeWindowToolWindow）
            MakeWindowToolWindow();
        }

        /// <summary>
        /// 窗口缓存：启动后把窗口停在鼠标所在屏中心（透明 + 点击穿透），
        /// 保持重定向表面有效且 DPI 与目标屏一致。此后切换只更新内容 + 淡入淡出，
        /// 无移动、无 DPI 变化、无表面重建 → 无 DWM 黑帧。
        /// </summary>
        private void PrepareOffscreenCache()
        {
            _isWindowShown = false;
            Opacity = 0;

            _activeScreen = MultiMonitorHelper.GetMouseScreen();
            LoadData(InitialFocus.NextItem);
            UpdateLayout();
            CenterWindow();
        }

        /// <summary>
        /// 选中高亮滑块：选中项变化时在列表项间平滑上下滑动（替代背景渐隐渐显）。
        /// </summary>
        private void HookUpSelectionHighlight()
        {
            lb.SelectionChanged += (s, e) => UpdateSelectionHighlight(true);
            lb.SelectionChanged += (s, e) => OnSelectionChangedShowThumbnail();
            lb.SizeChanged += (s, e) => UpdateSelectionHighlight(false);
            var scrollViewer = lb.Template?.FindName("ScrollViewer", lb) as System.Windows.Controls.ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollChanged += (s, e) => UpdateSelectionHighlight(false);
            }
            UpdateSelectionHighlight(false);
        }

        /// <summary>
        /// 常显缩略图需求：选中项变化时刷新预览（鼠标未悬停时始终显示选中项对应的窗口缩略图）。
        /// </summary>
        private void OnSelectionChangedShowThumbnail()
        {
            // 窗口稳定显示后才跟随刷新缩略图（唤出瞬间主窗口尚未定位/DPI 未稳定，避免缩略图出现在错误位置）
            if (!_isWindowShown || !_showStable)
            {
                return;
            }
            var selected = lb.SelectedItem as AppWindowViewModel;
            if (selected != null)
            {
                ShowThumbnailPreview(selected);
            }
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

            // 移除 OS 层边框样式（WS_BORDER/WS_DLGFRAME）：这类边框不受窗口 Opacity 控制，
            // 窗口透明后仍会留下"主窗口大小的边框"残留。
            var style = GetWindowLong(windowHandle, GWL_STYLE);
            style &= ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME);
            SetWindowLong(windowHandle, GWL_STYLE, style);
        }

        private const int GWL_STYLE = -16;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_THICKFRAME = 0x00040000;

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
