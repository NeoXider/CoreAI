#if COREAI_LUA
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The Luau <c>utf8</c> and <c>bit32</c> libraries and the rest of the Luau <c>math</c> members (MVP13, RT5).
    /// <c>utf8</c> reads this VM's strings by the rules of DEV-109: typed text is UTF-16, text built from bytes is
    /// UTF-8, and both give the same code points; <c>bit32</c> converts its arguments as Luau's
    /// <c>(unsigned)(long long)</c> cast does. Each behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class LuauUtf8Bit32LibraryEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static string Text(string chunk)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state, "local r = table.pack(" + chunk + ") "
                + "for i = 1, r.n do r[i] = tostring(r[i]) end return table.concat(r, '|', 1, r.n)");
            return results[0].Read<string>();
        }

        private static string Error(string call)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            LuaValue[] results = environment.RunChunk(state, "local ok, err = pcall(function() return " + call
                + " end) return tostring(ok) .. '|' .. tostring(err)");
            return results[0].Read<string>();
        }

        // ---- utf8 -----------------------------------------------------------------------------------

        [Test]
        public void Char_BuildsTheSameTextALiteralIs()
        {
            Assert.AreEqual("true|true|true", Text("utf8.char(72, 105) == 'Hi', utf8.char(233) == 'é', "
                                                   + "utf8.char(0x65E5, 0x1F600) == '日😀'"));
            Assert.AreEqual("", Text("utf8.char()"));
            StringAssert.Contains("bad argument #1 to 'char' (value out of range)", Error("utf8.char(0x110000)"));
            StringAssert.Contains("bad argument #2 to 'char' (value out of range)", Error("utf8.char(65, -1)"));
            StringAssert.Contains("(number expected, got string)", Error("utf8.char('x')"));
        }

        [Test]
        public void Len_CountsCodePoints_OfTypedTextAndOfUtf8Bytes()
        {
            Assert.AreEqual("4|1|1|2", Text("utf8.len('Hé日😀'), utf8.len('\\195\\169'), utf8.len('\\240\\159\\152\\128'), "
                                            + "utf8.len('abc', 2)"));
            Assert.AreEqual("0|3", Text("utf8.len(''), utf8.len('abc', -3)"));
            Assert.AreEqual("1", Text("utf8.len('\\233')"), "a lone 0xE9 is Latin-1 é, not an invalid byte (DEV-109)");
            Assert.AreEqual("nil|2", Text("utf8.len('a' .. ('😀'):sub(1, 1))"),
                "an unpaired surrogate is the one invalid code: nil and its position");
            StringAssert.Contains("(initial position out of string)", Error("utf8.len('abc', 5)"));
            StringAssert.Contains("(final position out of string)", Error("utf8.len('abc', 1, 5)"));
        }

        [Test]
        public void Codes_IteratesPositionsAndCodePoints()
        {
            Assert.AreEqual("1:97 2:233 3:128512 5:98", Text(
                "(function() local r = {} for p, c in utf8.codes('aé😀b') do r[#r + 1] = p .. ':' .. c end "
                + "return table.concat(r, ' ') end)()"));
            Assert.AreEqual("1:97 2:233 4:98", Text(
                "(function() local r = {} for p, c in utf8.codes('a\\195\\169b') do r[#r + 1] = p .. ':' .. c end "
                + "return table.concat(r, ' ') end)()"), "positions are char positions, the ones string.sub takes");
            StringAssert.Contains("invalid UTF-8 code",
                Error("(function() for _ in utf8.codes(('😀'):sub(2)) do end end)()"));
        }

        [Test]
        public void CodePoint_ReturnsTheRange_AndRefusesPositionsOutside()
        {
            Assert.AreEqual("104|233|108|108|111", Text("utf8.codepoint('héllo', 1, -1)"));
            Assert.AreEqual("104", Text("utf8.codepoint('héllo')"));
            Assert.AreEqual("0", Text("select('#', utf8.codepoint('abc', 3, 2))"));
            StringAssert.Contains("bad argument #2 to 'codepoint' (out of range)", Error("utf8.codepoint('abc', 0)"));
            StringAssert.Contains("bad argument #3 to 'codepoint' (out of range)", Error("utf8.codepoint('abc', 1, 4)"));
            StringAssert.Contains("string slice too long", Error("utf8.codepoint(string.rep('a', 9000), 1, -1)"));
        }

        [Test]
        public void Offset_MovesByCodePoints()
        {
            Assert.AreEqual("4|4|2|2|nil", Text("utf8.offset('a😀b', 3), utf8.offset('a😀b', -1), "
                                                + "utf8.offset('abc', 0, 2), utf8.offset('a😀b', 0, 3), utf8.offset('abc', 5)"));
            Assert.AreEqual("4", Text("utf8.offset('abc', 4)"), "one past the end is a position, as in Luau");
            StringAssert.Contains("initial position is a continuation byte", Error("utf8.offset('a😀b', 1, 3)"));
            StringAssert.Contains("bad argument #3 to 'offset' (position out of range)", Error("utf8.offset('abc', 1, 9)"));
        }

        [Test]
        public void CharPattern_IsTheMirrorsPattern_AndMatchesEachCharacter()
        {
            Assert.AreEqual("4|4", Text("(function() local n = 0 for _ in ('a日😀é'):gmatch(utf8.charpattern) do n = n + 1 end "
                                        + "local m = 0 for _ in ('a\\230\\151\\165\\240\\159\\152\\128\\195\\169'):gmatch(utf8.charpattern) do m = m + 1 end "
                                        + "return n, m end)()"));
            StringAssert.StartsWith("[%z\u0001-\u007FÂ-ô", LuaCsSecureEnvironment.Utf8CharPattern);
        }

        [Test]
        public void Graphemes_KeepsCombiningMarksAndJoinedEmojiTogether()
        {
            Assert.AreEqual("1-3 4-4", Text("(function() local r = {} for a, b in utf8.graphemes('e\\204\\129x') do "
                                            + "r[#r + 1] = a .. '-' .. b end return table.concat(r, ' ') end)()"));
            Assert.AreEqual("1", Text("(function() local n = 0 for _ in utf8.graphemes('👍🏽') do n = n + 1 end return n end)()"),
                "an emoji modifier joins its base");
            Assert.AreEqual("3", Text("(function() local n = 0 for _ in utf8.graphemes('abc') do n = n + 1 end return n end)()"));
        }

        [Test]
        public void Normalize_ComposesAndDecomposes()
        {
            Assert.AreEqual("2|true|true", Text("#utf8.nfdnormalize('é'), utf8.nfcnormalize(utf8.nfdnormalize('é')) == 'é', "
                                                + "utf8.nfcnormalize('e\\204\\129') == 'é'"));
        }

        // ---- bit32 ----------------------------------------------------------------------------------

        [Test]
        public void Bit32_ConvertsArgumentsAsLuauDoes()
        {
            Assert.AreEqual("5|4294967295|5|5", Text("bit32.band(5.7), bit32.band(-1), bit32.band(2^32 + 5), bit32.band('5')"),
                "Lua-CSharp refused bit32.band(5.7); Luau truncates");
            Assert.AreEqual("4294967295|0|0", Text("bit32.band(), bit32.bor(), bit32.bxor()"));
            StringAssert.Contains("bad argument #1 to 'band' (number expected, got table)", Error("bit32.band({})"));
            StringAssert.Contains("bad argument #2 to 'bor' (number expected, got nil)", Error("bit32.bor(1, nil)"));
        }

        [Test]
        public void Bit32_LogicAndShifts()
        {
            Assert.AreEqual("15|7|2|4294967295|false|true", Text("bit32.band(0xFF, 0x0F), bit32.bor(1, 2, 4), "
                                                                 + "bit32.bxor(3, 1), bit32.bnot(0), bit32.btest(1, 2), bit32.btest(3, 2)"));
            Assert.AreEqual("2147483648|0|1|4294967295|4294967292|2", Text("bit32.lshift(1, 31), bit32.lshift(1, 32), "
                + "bit32.rshift(0x80000000, 31), bit32.arshift(0x80000000, 31), bit32.arshift(-8, 1), bit32.lshift(4, -1)"));
            Assert.AreEqual("2|2147483648|2", Text("bit32.lrotate(1, 33), bit32.rrotate(1, 1), bit32.rrotate(1, -1)"));
        }

        [Test]
        public void Bit32_FieldsAndCounts()
        {
            Assert.AreEqual("15|240|1", Text("bit32.extract(0xF0, 4, 4), bit32.replace(0, 0xF, 4, 4), bit32.extract(1, 0)"));
            Assert.AreEqual("31|32|3|0|32|2018915346", Text("bit32.countlz(1), bit32.countlz(0), bit32.countrz(8), "
                                                            + "bit32.countrz(1), bit32.countrz(0), bit32.byteswap(0x12345678)"));
            StringAssert.Contains("trying to access non-existent bits", Error("bit32.extract(1, 31, 2)"));
            StringAssert.Contains("(field cannot be negative)", Error("bit32.extract(1, -1)"));
            StringAssert.Contains("(width must be positive)", Error("bit32.extract(1, 0, 0)"));
        }

        // ---- math -----------------------------------------------------------------------------------

        [Test]
        public void Math_TheMembersTheMirrorAdds()
        {
            Assert.AreEqual("3|5|3|50|10", Text("math.log10(1000), math.lerp(0, 10, 0.5), math.lerp(1, 3, 1), "
                                                + "math.map(5, 0, 10, 0, 100), math.map(0, -1, 1, 0, 20)"));
            Assert.AreEqual("true|false|true|false|true|false", Text("math.isnan(0/0), math.isnan(1), math.isinf(-1/0), "
                                                                     + "math.isinf(1), math.isfinite(1), math.isfinite(0/0)"));
            Assert.AreEqual("true|true|true|true|true", Text("math.e == math.exp(1), math.abs(math.phi - (1 + math.sqrt(5)) / 2) < 1e-15, "
                                                             + "math.sqrt2 == math.sqrt(2), math.tau == 2 * math.pi, math.isnan(math.nan)"));
            StringAssert.Contains("bad argument #3 to 'lerp' (number expected, got no value)", Error("math.lerp(1, 2)"));
            StringAssert.Contains("bad argument #1 to 'isnan' (number expected, got nil)", Error("math.isnan(nil)"));
        }

        [Test]
        public void Lerp_IsExactAtBothEnds()
        {
            Assert.AreEqual(1e16d, LuaCsSecureEnvironment.LuauLerpValue(1e16d, 1d, 0d));
            Assert.AreEqual(1d, LuaCsSecureEnvironment.LuauLerpValue(1e16d, 1d, 1d),
                "a + (b - a) * 1 is 0 for these doubles; Luau returns b");
            double a = 1e16d;
            double b = 1d;
            Assert.AreNotEqual(b, a + (b - a) * 1d, "the twin: the plain formula is not exact there");
        }
    }
}
#endif
