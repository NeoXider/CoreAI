using CoreAI.LuaAssets;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaSourceCapEditModeTests
    {
        [Test]
        public void Cap_SourceUnderLimit_IsReturnedUnchanged()
        {
            const string source = "local x = 1";
            LuaSourceCap.Result result = LuaSourceCap.Cap(source, 1024);

            Assert.IsFalse(result.WasTruncated);
            Assert.AreEqual(source, result.Text);
            Assert.AreEqual(source.Length, result.OriginalLength);
        }

        [Test]
        public void Cap_SourceOverLimit_IsTruncatedAndFlagged()
        {
            string source = new string('a', 5000);
            LuaSourceCap.Result result = LuaSourceCap.Cap(source, 1000);

            Assert.IsTrue(result.WasTruncated);
            Assert.LessOrEqual(result.Text.Length, 1000);
            Assert.AreEqual(5000, result.OriginalLength);
        }

        [Test]
        public void Cap_TruncatesOnLineBoundary_WhenOneIsCloseToTheLimit()
        {
            // WHY: a newline sits well past halfway through the cap, so the cut should land there
            // rather than mid-line.
            string longLine = new string('a', 100);
            string source = longLine + "\n" + new string('b', 5000);

            LuaSourceCap.Result result = LuaSourceCap.Cap(source, 200);

            Assert.IsTrue(result.WasTruncated);
            Assert.AreEqual(longLine, result.Text);
        }

        [Test]
        public void Cap_EmptySource_ReturnsEmptyUntruncated()
        {
            LuaSourceCap.Result result = LuaSourceCap.Cap("", 1000);

            Assert.IsFalse(result.WasTruncated);
            Assert.AreEqual("", result.Text);
        }

        [Test]
        public void Cap_NullSource_ReturnsEmptyUntruncated()
        {
            LuaSourceCap.Result result = LuaSourceCap.Cap(null, 1000);

            Assert.IsFalse(result.WasTruncated);
            Assert.AreEqual("", result.Text);
        }

        [Test]
        public void Cap_DefaultMaxChars_Is64KiB()
        {
            Assert.AreEqual(64 * 1024, LuaSourceCap.DefaultMaxChars);
        }
    }
}
