using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NPinyin;

namespace Switcheroo.Core.Matchers
{
    /// <summary>
    /// 中文拼音匹配器：允许用拼音全拼或首字母搜索中文窗口标题/进程名。
    /// 例如输入 "wx" 或 "weixin" 可匹配标题含"微信"的窗口。
    /// 通过构建「别名字符 → 原文字符索引」映射，命中后把高亮正确映射回原文。
    /// </summary>
    public class PinyinMatcher : IMatcher
    {
        private static readonly Regex CjkChar = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);

        internal static readonly int ScoreFullPrefix = 4;
        internal static readonly int ScoreFullContains = 2;
        internal static readonly int ScoreInitialsPrefix = 3;
        internal static readonly int ScoreInitialsContains = 1;

        private static readonly Pinyin PinyinConverter = new Pinyin();

        private static bool IsCjkChar(char ch)
        {
            return ch >= '\u4E00' && ch <= '\u9FFF';
        }

        /// <summary>
        /// 判断字符串是否含中文字符。
        /// </summary>
        public static bool HasChinese(string text)
        {
            return text != null && CjkChar.IsMatch(text);
        }

        public MatchResult Evaluate(string input, string pattern)
        {
            if (!HasChinese(input))
            {
                // 不含中文，拼音匹配不参与，交给其他 matcher
                return NoMatch(input);
            }

            if (string.IsNullOrEmpty(pattern))
            {
                return NoMatch(input);
            }

            // 拼音匹配器只处理拼音查询；pattern 含中文时回退给原文 matcher
            if (HasChinese(pattern))
            {
                return NoMatch(input);
            }

            // 惰性顺序：先试全拼（前缀分更高），未命中再试首字母，避免每次全量构建两个别名
            var fullAlias = BuildAlias(input, includeFullPinyin: true);
            var matched = MatchAlias(input, fullAlias, pattern, ScoreFullPrefix, ScoreFullContains);
            if (matched != null)
            {
                return matched;
            }

            var initialsAlias = BuildAlias(input, includeFullPinyin: false);
            matched = MatchAlias(input, initialsAlias, pattern, ScoreInitialsPrefix, ScoreInitialsContains);
            if (matched != null)
            {
                return matched;
            }

            return NoMatch(input);
        }

        private static MatchResult MatchAlias(string input, Alias alias, string pattern, int prefixScore, int containsScore)
        {
            var index = alias.Text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var originalStart = alias.Map[index];
            var originalEnd = alias.Map[index + pattern.Length - 1] + 1;
            var score = index == 0 ? prefixScore : containsScore;

            var result = new MatchResult
            {
                Matched = true,
                Score = score
            };
            if (originalStart > 0)
            {
                result.StringParts.Add(new StringPart(input.Substring(0, originalStart)));
            }
            result.StringParts.Add(new StringPart(input.Substring(originalStart, originalEnd - originalStart), true));
            if (originalEnd < input.Length)
            {
                result.StringParts.Add(new StringPart(input.Substring(originalEnd)));
            }
            return result;
        }

        private static MatchResult NoMatch(string input)
        {
            var result = new MatchResult();
            if (input != null)
            {
                result.StringParts.Add(new StringPart(input));
            }
            return result;
        }

        /// <summary>
        /// 逐字符生成拼音别名：中文字符转为拼音（全拼或首字母），
        /// 非中文字符原样保留；同时记录每个别名字符对应的原文字符索引。
        /// </summary>
        private static Alias BuildAlias(string input, bool includeFullPinyin)
        {
            var text = new System.Text.StringBuilder();
            var map = new List<int>();

            for (var i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (IsCjkChar(ch))
                {
                    var pinyin = PinyinConverter.ConvertToPinyin(ch.ToString());
                    if (string.IsNullOrEmpty(pinyin))
                    {
                        // 个别汉字可能查不到拼音，原样保留避免映射错位
                        text.Append(ch);
                        map.Add(i);
                        continue;
                    }

                    if (includeFullPinyin)
                    {
                        foreach (var letter in pinyin)
                        {
                            text.Append(letter);
                            map.Add(i);
                        }
                    }
                    else
                    {
                        // 首字母
                        text.Append(char.ToLowerInvariant(pinyin[0]));
                        map.Add(i);
                    }
                }
                else
                {
                    text.Append(ch);
                    map.Add(i);
                }
            }

            return new Alias(text.ToString(), map.ToArray());
        }

        private class Alias
        {
            public string Text { get; }
            public int[] Map { get; }

            public Alias(string text, int[] map)
            {
                Text = text;
                Map = map;
            }
        }
    }
}