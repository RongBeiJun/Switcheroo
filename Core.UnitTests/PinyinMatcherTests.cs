using NUnit.Framework;
using Switcheroo.Core.Matchers;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class PinyinMatcherTests
    {
        private PinyinMatcher _matcher;

        [SetUp]
        public void Setup()
        {
            _matcher = new PinyinMatcher();
        }

        [Test]
        public void FullPinyin_Weixin_Matches()
        {
            var result = _matcher.Evaluate("微信", "weixin");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(4));
            Assert.That(XamlText(result), Is.EqualTo("<Bold>微信</Bold>"));
        }

        [Test]
        public void FullPinyin_MixedCase_Matches()
        {
            var result = _matcher.Evaluate("微信", "WeiXin");

            Assert.That(result.Matched, Is.True);
        }

        [Test]
        public void Initials_WX_Matches()
        {
            // "微信" 的首字母恰为 "wx"，属首字母前缀命中
            var result = _matcher.Evaluate("微信", "wx");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(3));
            Assert.That(XamlText(result), Is.EqualTo("<Bold>微信</Bold>"));
        }

        [Test]
        public void InitialsContained_NotAtStart_Matches()
        {
            // "腾讯微信" 首字母 "txwx"，"wx" 位于中部，属首字母包含命中
            var result = _matcher.Evaluate("腾讯微信", "wx");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(1));
            Assert.That(XamlText(result), Is.EqualTo("腾讯<Bold>微信</Bold>"));
        }

        [Test]
        public void FullPinyin_BaiduLiulanqi_Matches()
        {
            var result = _matcher.Evaluate("百度浏览器", "baiduliulanqi");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(4));
        }

        [Test]
        public void Initials_BDLLQ_Matches()
        {
            var result = _matcher.Evaluate("百度浏览器", "bdllq");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(3));
        }

        [Test]
        public void PartialFullPinyin_Liu_MatchesAndHighlights()
        {
            var result = _matcher.Evaluate("百度浏览器", "liu");

            Assert.That(result.Matched, Is.True);
            Assert.That(result.Score, Is.EqualTo(2));
            Assert.That(XamlText(result), Is.EqualTo("百度<Bold>浏</Bold>览器"));
        }

        [Test]
        public void PartialPinyin_SpansTwoChars_MapsBoundary()
        {
            // "浏" 拼音 liu，"览" 拼音 lan；"liulan" 命中两字并映射整段
            var result = _matcher.Evaluate("百度浏览器", "liulan");

            Assert.That(result.Matched, Is.True);
            Assert.That(XamlText(result), Is.EqualTo("百度<Bold>浏览</Bold>器"));
        }

        [Test]
        public void MixedChineseEnglish_Chrome_MatchesAndHighlights()
        {
            var result = _matcher.Evaluate("谷歌Chrome浏览器", "chrome");

            Assert.That(result.Matched, Is.True);
            Assert.That(XamlText(result), Is.EqualTo("谷歌<Bold>Chrome</Bold>浏览器"));
        }

        [Test]
        public void MixedChineseEnglish_Guge_MatchesAndHighlights()
        {
            var result = _matcher.Evaluate("谷歌Chrome浏览器", "guge");

            Assert.That(result.Matched, Is.True);
            Assert.That(XamlText(result), Is.EqualTo("<Bold>谷歌</Bold>Chrome浏览器"));
        }

        [Test]
        public void NoChinese_NotHandledByPinyinMatcher()
        {
            var result = _matcher.Evaluate("visual studio code", "vs");

            Assert.That(result.Matched, Is.False);
        }

        [Test]
        public void ChineseInput_NotHandledByPinyinMatcher()
        {
            var result = _matcher.Evaluate("微信", "微");

            Assert.That(result.Matched, Is.False);
        }

        [Test]
        public void NullInput_ReturnsNoMatch_NoNre()
        {
            var result = _matcher.Evaluate(null, "wx");

            Assert.That(result.Matched, Is.False);
        }

        [Test]
        public void NullPattern_False()
        {
            var result = _matcher.Evaluate("微信", null);

            Assert.That(result.Matched, Is.False);
        }

        private static string XamlText(MatchResult result)
        {
            return new XamlHighlighter().Highlight(result.StringParts);
        }
    }
}