using NUnit.Framework;
using Switcheroo.Core;
using System.Collections.Generic;
using System.Linq;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class WindowFiltererProcessFilterTest
    {
        private class FakeWindow : IWindowText
        {
            public string Title;
            public string Process;
            public string WindowTitle => Title;
            public string ProcessTitle => Process;
            public FakeWindow(string t, string p) { Title = t; Process = p; }
        }

        [Test]
        public void ProcessFilter_ChromeDot_ReturnsOnlyChrome()
        {
            var windows = new List<FakeWindow>
            {
                new FakeWindow("My Docs - Google Chrome", "chrome"),
                new FakeWindow("Main.java - Visual Studio Code", "code"),
                new FakeWindow("Explorer", "explorer"),
            };
            var context = new WindowFilterContext<FakeWindow> { Windows = windows, ForegroundWindowProcessTitle = "" };

            var result = new WindowFilterer().Filter(context, "chrome.").ToList();

            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result[0].AppWindow.ProcessTitle, Is.EqualTo("chrome"));
        }

        [Test]
        public void ProcessFilter_EmptyQueryAfterDot_MatchesAllTitles()
        {
            var windows = new List<FakeWindow>
            {
                new FakeWindow("Chrome Tab", "chrome"),
                new FakeWindow("Other", "notepad"),
            };
            var context = new WindowFilterContext<FakeWindow> { Windows = windows, ForegroundWindowProcessTitle = "" };

            var result = new WindowFilterer().Filter(context, "chrome.").ToList();

            Assert.That(result.Count, Is.EqualTo(1));
        }
    }
}