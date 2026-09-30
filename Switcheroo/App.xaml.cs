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

using System.Windows;

namespace Switcheroo
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Localization.EnsureLoaded();
            base.OnStartup(e);

            // 常驻透明方案依赖主窗口启动即预显示（透明停屏内，见 PrepareOffscreenCache）：
            // 保证首次 Alt+Tab 唤出无需现场建窗/布局，消除启动后首次唤出的大延迟。
            // （StartupUri 的自动显示偶发未生效时兜底，重复 Show 无害）
            if (MainWindow != null && !MainWindow.IsVisible)
            {
                MainWindow.Show();
            }
        }
    }
}