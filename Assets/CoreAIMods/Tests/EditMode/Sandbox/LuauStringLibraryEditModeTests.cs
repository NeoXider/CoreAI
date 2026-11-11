#if COREAI_LUA
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The Luau <c>string</c> extensions of MVP13 (RT5): <c>string.split</c> against the mirror's corner cases, and
    /// <c>string.format</c>'s <c>%*</c> and numeric conversions against C's output (the goldens are glibc's, computed
    /// by Python's C-semantics formatter), with the mirror's own examples; <c>string.pack</c> and friends are loud
    /// stubs. Each behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class LuauStringLibraryEditModeTests
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

        private static LuaValue[] Run(string chunk)
        {
            LuaCsSecureEnvironment environment = new();
            LuaState state = environment.Create();
            return environment.RunChunk(state, chunk);
        }

        private static string Text(string expression)
        {
            return Run("return " + expression)[0].Read<string>();
        }

        private static string Error(string call)
        {
            LuaValue[] results = Run("local ok, err = pcall(function() return " + call + " end) "
                                     + "return tostring(ok) .. '|' .. tostring(err)");
            return results[0].Read<string>();
        }

        /// <summary>The pieces of <c>string.split</c> as <c>[a][b]</c>, so empty pieces stay visible.</summary>
        private static string Split(string arguments)
        {
            return Text("(function() local out = {} for i, v in ipairs(string.split(" + arguments + ")) do "
                        + "out[i] = '[' .. v .. ']' end return table.concat(out) end)()");
        }

        // ---- string.split ---------------------------------------------------------------------------

        [Test]
        public void Split_FollowsTheMirrorsCornerCases()
        {
            Assert.AreEqual("[abc][][def]", Split("'abc||def', '|'"));
            Assert.AreEqual("[]", Split("''"), "an empty string is one empty piece");
            Assert.AreEqual("[foo][][bar]", Split("'foo,,bar'"));
            Assert.AreEqual("[][foo]", Split("',foo'"));
            Assert.AreEqual("[foo][]", Split("'foo,'"));
            Assert.AreEqual("[][]", Split("','"));
            Assert.AreEqual("[][][]", Split("',,'"));
            Assert.AreEqual("[   whitespace   ]", Split("'   whitespace   '"));
            Assert.AreEqual("[foo ][ bar]", Split("'foo , bar'"));
            Assert.AreEqual("[abc ][ def]", Split("'abc _ def', '_'"));
        }

        [Test]
        public void Split_UsesAPlainSeparator_OfAnyLength_AndSplitsCharactersOnAnEmptyOne()
        {
            Assert.AreEqual("[a][b]", Split("'a::b', '::'"));
            Assert.AreEqual("[a.b]", Split("'a.b', '%.'"), "the separator is plain text, never a pattern");
            Assert.AreEqual("[][a]", Split("'aaa', 'aa'"), "matches do not overlap");
            Assert.AreEqual("[a][b][c]", Split("'abc', ''"));
            Assert.AreEqual("", Split("'', ''"), "an empty string with an empty separator has no pieces");
            Assert.AreEqual("[1][3]", Split("123, 2"), "numbers convert as luaL_checkstring converts them");
            Assert.AreEqual("[a][b]", Text("(function() local t = ('a b'):split(' ') return '[' .. t[1] .. '][' .. t[2] .. ']' end)()"),
                "method syntax reaches it through the string metatable");
            Assert.AreEqual("[我很高兴][你呢？]", Split("'我很高兴，你呢？', '，'"));
        }

        [Test]
        public void Split_RefusesBadArguments_AndCapsThePieces()
        {
            StringAssert.Contains("bad argument #1 to 'split' (string expected, got no value)", Error("string.split()"));
            StringAssert.Contains("bad argument #2 to 'split' (string expected, got table)", Error("string.split('a', {})"));
            string refusal = Error("(function() local s = string.rep(',', 600000) return string.split(s .. s) end)()");
            StringAssert.StartsWith("false|sandbox: string.split would build more than", refusal);
            Assert.AreEqual("false|600001", Error("(function() error(#string.split(string.rep(',', 600000)), 0) end)()"),
                "a split under the cap completes");
        }

        // ---- string.format --------------------------------------------------------------------------

        [TestCase("%.2f", "1/3", "0.33")]
        [TestCase("%5.1f", "3.14159", "  3.1")]
        [TestCase("%.2f", "2.675", "2.67")]
        [TestCase("%.0f", "0.5", "0")]
        [TestCase("%.0f", "1.5", "2")]
        [TestCase("%.0f", "2.5", "2")]
        [TestCase("%.1f", "0.05", "0.1")]
        [TestCase("%f", "2^70", "1180591620717411303424.000000")]
        [TestCase("%10.3f|", "-3.14159", "    -3.142|")]
        [TestCase("%-10.3f|", "3.14159", "3.142     |")]
        [TestCase("%05.1f", "-3.14159", "-03.1")]
        [TestCase("%+.1f", "2", "+2.0")]
        [TestCase("% .1f", "2", " 2.0")]
        [TestCase("%#.0f", "3", "3.")]
        [TestCase("%f", "-0.0", "-0.000000")]
        [TestCase("%.3f", "1e-10", "0.000")]
        [TestCase("%.20f", "0.1", "0.10000000000000000555")]
        [TestCase("%e", "0", "0.000000e+00")]
        [TestCase("%.2e", "12345.678", "1.23e+04")]
        [TestCase("%E", "12345.678", "1.234568E+04")]
        [TestCase("%e", "1e-300", "1.000000e-300")]
        [TestCase("%.0e", "5e10", "5e+10")]
        [TestCase("%#.0e", "5e10", "5.e+10")]
        [TestCase("%e", "9.9999999", "1.000000e+01")]
        [TestCase("%e", "5e-324", "4.940656e-324")]
        [TestCase("%g", "1e20", "1e+20")]
        [TestCase("%g", "1e6", "1e+06")]
        [TestCase("%g", "123456", "123456")]
        [TestCase("%g", "1234567", "1.23457e+06")]
        [TestCase("%g", "1e-5", "1e-05")]
        [TestCase("%g", "0.0001", "0.0001")]
        [TestCase("%g", "100000", "100000")]
        [TestCase("%g", "0.5", "0.5")]
        [TestCase("%.3g", "0.0001234", "0.000123")]
        [TestCase("%g", "0", "0")]
        [TestCase("%#g", "1", "1.00000")]
        [TestCase("%.3g", "3.14159", "3.14")]
        [TestCase("%G", "1e-10", "1E-10")]
        [TestCase("%.14g", "0.1", "0.1")]
        [TestCase("%.17g", "0.1", "0.10000000000000001")]
        [TestCase("%g", "2^70", "1.18059e+21")]
        [TestCase("%10.4g|", "3.14159", "     3.142|")]
        [TestCase("%-10g|", "2.5", "2.5       |")]
        [TestCase("%.0g", "123", "1e+02")]
        [TestCase("%g", "999999.5", "1e+06")]
        public void Format_FloatConversions_MatchC(string format, string value, string expected)
        {
            Assert.AreEqual(expected, Text("string.format('" + format + "', " + value + ")"));
        }

        [TestCase("%f", "1/0", "inf")]
        [TestCase("%f", "-1/0", "-inf")]
        [TestCase("%F", "1/0", "INF")]
        [TestCase("%+e", "1/0", "+inf")]
        [TestCase("%g", "math.huge", "inf")]
        [TestCase("%05f", "1/0", "  inf")]
        public void Format_NonFiniteNumbers_PrintAsC_NotAsDotNet(string format, string value, string expected)
        {
            Assert.AreEqual(expected, Text("string.format('" + format + "', " + value + ")"),
                "Lua-CSharp printed Infinity and NaN here");
        }

        [Test]
        public void Format_NaN_PrintsNan_WithTheSignBitsSign()
        {
            // WHY no fixed sign: the sign of 0/0 is the processor's; glibc prints it, so either form is C's.
            StringAssert.IsMatch("^ *-?nan\\|$", Text("string.format('%5.1f|', 0/0)"));
            StringAssert.IsMatch("^-?NAN$", Text("string.format('%G', math.nan)"));
        }

        [TestCase("%d", "3.7", "3")]
        [TestCase("%d", "-3.7", "-3")]
        [TestCase("%i", "'42'", "42")]
        [TestCase("%d", "-0.0", "0")]
        [TestCase("%5d|", "42", "   42|")]
        [TestCase("%-5d|", "42", "42   |")]
        [TestCase("%+d", "42", "+42")]
        [TestCase("% d", "42", " 42")]
        [TestCase("%05d", "-42", "-0042")]
        [TestCase("%.3d", "7", "007")]
        [TestCase("%.0d", "0", "")]
        [TestCase("%d", "2^53", "9007199254740992")]
        [TestCase("%d", "1e20", "-9223372036854775808")]
        [TestCase("%x", "-1", "ffffffffffffffff")]
        [TestCase("%X", "255", "FF")]
        [TestCase("%#x", "255", "0xff")]
        [TestCase("%#X", "0", "0")]
        [TestCase("%08X", "0xABCD", "0000ABCD")]
        [TestCase("%o", "8", "10")]
        [TestCase("%#o", "8", "010")]
        [TestCase("%u", "-1", "18446744073709551615")]
        [TestCase("%x", "2^64", "8000000000000000")]
        [TestCase("%c", "65", "A")]
        [TestCase("%c", "321", "A")]
        [TestCase("%3c|", "66", "  B|")]
        public void Format_IntegerConversions_FollowLuau(string format, string value, string expected)
        {
            Assert.AreEqual(expected, Text("string.format('" + format + "', " + value + ")"));
        }

        [Test]
        public void Format_TheMirrorsOwnExamples()
        {
            Assert.AreEqual("300       ]", Text("string.format('%-10d', 300) .. ']'"));
            Assert.AreEqual("+300 versus -300", Text("string.format('%+i versus %+i', 300, -300)"));
            Assert.AreEqual("There is a 100% chance of rain in Seattle today.",
                Text("string.format('There is a% i%% chance of rain in Seattle today.', 100)"));
            Assert.AreEqual("Score: 000000015000", Text("'Score: ' .. string.format('%012i', 15000)"));
            Assert.AreEqual("$300.00", Text("string.format('$%.2f', 300)"));
            Assert.AreEqual("Roblox", Text("string.format('%.6s', 'Robloxian')"));
            Assert.AreEqual("Once upon a time, there was a dragon named Pi and it had 3.14159265 horns.",
                Text("string.format('Once upon a time, there was a dragon named %s and it had %.8f horns.', 'Pi', math.pi)"));
            Assert.AreEqual("The magic word is \"Roblox\"", Text("string.format('The magic word is %q', 'Roblox')"));
        }

        [Test]
        public void Format_Star_ConvertsAnyValueLikeTostring()
        {
            Assert.AreEqual("nil|true|1.5|x", Text("string.format('%*|%*|%*|%*', nil, true, 1.5, 'x')"));
            StringAssert.StartsWith("table: ", Text("string.format('%*', {})"));
            Assert.AreEqual("custom", Text("string.format('%*', setmetatable({}, {__tostring = function() return 'custom' end}))"));
            Assert.AreEqual("a 5 b", Text("string.format('%s %* %s', 'a', 5, 'b')"), "it consumes one argument");
            StringAssert.Contains("'%*' does not take a form", Error("string.format('%5*', 1)"));
            StringAssert.Contains("'%*' does not take a form", Error("string.format('%-*', 1)"));
        }

        [Test]
        public void Format_StringConversionsAndTheCapsAreUnchanged()
        {
            Assert.AreEqual("x=3", Text("string.format('%s=%d', 'x', 3)"));
            Assert.AreEqual("   ab|", Text("string.format('%5s|', 'ab')"));
            Assert.AreEqual("100%", Text("string.format('100%%')"));
            Assert.AreEqual("\"a\\\"b\"", Text("string.format('%q', 'a\"b')"));
            Assert.AreEqual("7", Text("('%d'):format(7.9)"), "method syntax reaches the same function");
            StringAssert.Contains("string.format result would exceed", Error(
                "(function() local s = string.rep('x', 1000000) return string.format(string.rep('%s', 40), "
                + string.Join(", ", System.Linq.Enumerable.Repeat("s", 40)) + ") end)()"));
        }

        [Test]
        public void Format_RefusesMalformedConversions_WithLuausText()
        {
            StringAssert.Contains("invalid conversion '%5' to 'format'", Error("string.format('%5', 1)"));
            StringAssert.Contains("invalid conversion '%y' to 'format'", Error("string.format('%y', 1)"));
            StringAssert.Contains("invalid conversion '%a' to 'format'", Error("string.format('%a', 1)"));
            StringAssert.Contains("invalid format (width or precision too long)", Error("string.format('%123d', 1)"));
            StringAssert.Contains("bad argument #2 to 'format' (number expected, got no value)", Error("string.format('%d')"));
            StringAssert.Contains("bad argument #3 to 'format' (number expected, got table)",
                Error("string.format('%d %x', 1, {})"));
            StringAssert.DoesNotContain("System.", Error("string.format('%x', -1e300)"),
                "a CLR exception never reaches the script");
        }

        [Test]
        public void PackFamily_IsALoudStub_NamingItsRung()
        {
            foreach (string call in new[] { "string.pack('i4', 1)", "string.packsize('i4')", "string.unpack('i4', 'abcd')" })
            {
                string error = Error(call);
                StringAssert.Contains("NOT_IMPLEMENTED", error, call);
                StringAssert.Contains("planned for MVP14", error, call);
            }
        }
    }
}
#endif
