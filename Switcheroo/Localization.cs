using System;
using System.Linq;
using System.Windows;
using Switcheroo.Properties;

namespace Switcheroo
{
    /// <summary>
    /// 界面中英文切换。字符串存于 Resources/Strings.{en|zh}.xaml，
    /// 作为 Application 资源合并字典，XAML 用 DynamicResource 引用；
    /// 代码中的文本通过 <see cref="Get"/> 读取。
    /// </summary>
    public static class Localization
    {
        public static event Action LanguageChanged;

        private static bool _loaded;

        public static string CurrentLang => Settings.Default.Language == "zh" ? "zh" : "en";

        /// <summary>
        /// 读取当前语言下的字符串。
        /// </summary>
        public static string Get(string key)
        {
            EnsureLoaded();
            return Application.Current != null
                ? Application.Current.TryFindResource(key) as string ?? key
                : key;
        }

        /// <summary>
        /// 应用并保存指定语言，替换资源字典并广播刷新。
        /// </summary>
        public static void Apply(string lang)
        {
            _loaded = true;
            Settings.Default.Language = lang == "zh" ? "zh" : "en";
            Settings.Default.Save();

            if (Application.Current == null) return;

            var dict = new ResourceDictionary
            {
                Source = new Uri("Resources/Strings." + CurrentLang + ".xaml", UriKind.Relative)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            var old = merged.Where(d => d.Source != null
                                        && d.Source.OriginalString.Contains("Strings.")).ToList();
            foreach (var d in old)
            {
                merged.Remove(d);
            }
            merged.Add(dict);

            var handler = LanguageChanged;
            if (handler != null) handler();
        }

        /// <summary>
        /// 确保已按当前设置加载语言资源（首次访问或窗口构造时调用）。
        /// </summary>
        public static void EnsureLoaded()
        {
            if (!_loaded && Application.Current != null)
            {
                Apply(Settings.Default.Language);
            }
        }
    }
}