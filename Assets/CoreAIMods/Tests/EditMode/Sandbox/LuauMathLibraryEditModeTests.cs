#if COREAI_LUA
using System;
using System.Globalization;
using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Sandbox
{
    /// <summary>
    /// The Luau math slice of MVP13 (<c>math.noise</c>, <c>math.clamp</c>, <c>math.round</c>,
    /// <c>math.sign</c>) against the mirror's <c>math</c> library page and Luau's own argument rules,
    /// through the sandbox every mod and <c>execute_lua</c> chunk runs in. Each behaviour has its
    /// negative twin (the bad-argument line, or the documented value that must not move).
    /// </summary>
    [TestFixture]
    public sealed class LuauMathLibraryEditModeTests
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

        private static double Number(string expression)
        {
            LuaValue[] results = Run("return " + expression);
            Assert.IsTrue(results.Length > 0, expression + " returned nothing");
            return results[0].Read<double>();
        }

        private static string Error(string call)
        {
            LuaValue[] results = Run("local ok, err = pcall(function() return " + call + " end) return tostring(ok) .. '|' .. tostring(err)");
            return results[0].Read<string>();
        }

        private static string G14(double value)
        {
            return value.ToString("G14", CultureInfo.InvariantCulture);
        }

        // ---- math.noise ------------------------------------------------------------------------

        [Test]
        public void Noise_ReturnsTheMirrorsDocumentedValues()
        {
            Assert.AreEqual("0.48397532105446", G14(Number("math.noise(1.158, 5.723)")));
            Assert.AreEqual("0.15315161645412", G14(Number("math.noise(1.158, 6)")));
        }

        [Test]
        public void Noise_IsZeroOnIntegerCoordinates_AndNotZeroBetweenThem()
        {
            Assert.AreEqual(0d, Number("math.noise(1, 2, 3)"));
            Assert.AreEqual(0d, Number("math.noise(0)"));
            Assert.AreEqual(0d, Number("math.noise(-5, 7)"));
            Assert.AreNotEqual(0d, Number("math.noise(1.158, 5.723)"), "a fractional point is not a lattice point");
        }

        [Test]
        public void Noise_RepeatsEvery256OnEachAxis()
        {
            LuaValue[] results = Run(@"
                local same, gap = true, 0
                for _, p in ipairs({ {1.25, 5.75, 0.375}, {-3.25, 0.5, 7.75}, {100.125, -200.5, 33.0625} }) do
                    local base = math.noise(p[1], p[2], p[3])
                    same = same and base == math.noise(p[1] + 256, p[2], p[3])
                        and base == math.noise(p[1], p[2] + 256, p[3])
                        and base == math.noise(p[1], p[2], p[3] - 256)
                end
                gap = math.abs(math.noise(1.158, 5.723) - math.noise(1.158 + 256, 5.723))
                return same, gap, math.noise(0.5, 0.25, 0.75) == math.noise(0.5 + 128, 0.25, 0.75)");

            Assert.IsTrue(results[0].Read<bool>(), "a shift of 256 on any axis gives the same value");
            Assert.Less(results[1].Read<double>(), 1e-4d,
                "a coordinate single precision cannot hold exactly moves only by its rounding, as in Luau");
            Assert.IsFalse(results[2].Read<bool>(), "a shift of 128 is not a period");
        }

        [Test]
        public void Noise_OmittedOrNilAxesReadAsZero()
        {
            LuaValue[] results = Run(@"
                return math.noise(1.158) == math.noise(1.158, 0, 0),
                    math.noise(1.158, 5.723) == math.noise(1.158, 5.723, 0),
                    math.noise(1.158, nil, 0.4) == math.noise(1.158, 0, 0.4),
                    math.noise('1.5', '0.25') == math.noise(1.5, 0.25)");

            Assert.IsTrue(results[0].Read<bool>());
            Assert.IsTrue(results[1].Read<bool>());
            Assert.IsTrue(results[2].Read<bool>());
            Assert.IsTrue(results[3].Read<bool>(), "numeric strings coerce like tonumber, as Luau does");
        }

        [Test]
        public void Noise_StaysWithinOneAndChangesSmoothly()
        {
            LuaValue[] results = Run(@"
                local maxAbs, maxStep = 0, 0
                for i = 0, 2000 do
                    local x = i * 0.137
                    local a = math.noise(x, x * 0.5, 3.3)
                    local b = math.noise(x + 0.001, x * 0.5, 3.3)
                    maxAbs = math.max(maxAbs, math.abs(a))
                    maxStep = math.max(maxStep, math.abs(a - b))
                end
                return maxAbs, maxStep");

            Assert.LessOrEqual(results[0].Read<double>(), 1d);
            Assert.Greater(results[0].Read<double>(), 0.1d, "the field is not flat");
            Assert.Less(results[1].Read<double>(), 0.01d, "close coordinates give close values");
        }

        [Test]
        public void Noise_MatchesTheEngineFreeImplementation_ForTerrainGenerators()
        {
            double lua = Number("math.noise(12.34, -5.67, 0.89)");
            Assert.AreEqual(RbxPerlinNoise.Noise(12.34d, -5.67d, 0.89d), lua);
            Assert.AreEqual(RbxPerlinNoise.Noise(12.34d, -5.67d, 0.89d), RbxPerlinNoise.Noise(12.34d, -5.67d, 0.89d),
                "the same seed gives the same world");
        }

        [Test]
        public void Noise_RefusesANonNumber_NegativeTwin()
        {
            StringAssert.StartsWith("false|", Error("math.noise()"));
            StringAssert.Contains("bad argument #1", Error("math.noise()"));
            StringAssert.Contains("(number expected, got no value)", Error("math.noise()"));
            StringAssert.Contains("(number expected, got string)", Error("math.noise('north')"));
            StringAssert.Contains("bad argument #2", Error("math.noise(1, {})"));
            StringAssert.Contains("(number expected, got table)", Error("math.noise(1, {})"));
            StringAssert.Contains("bad argument #3", Error("math.noise(1, 2, true)"));
        }

        [Test]
        public void Noise_OfANonFiniteCoordinate_IsNaN()
        {
            Assert.IsTrue(double.IsNaN(Number("math.noise(math.huge)")));
            Assert.IsTrue(double.IsNaN(Number("math.noise(0/0, 1.5)")));
        }

        // ---- math.clamp ------------------------------------------------------------------------

        [Test]
        public void Clamp_ReturnsTheValueBetweenMinAndMaxInclusive()
        {
            Assert.AreEqual(3d, Number("math.clamp(5, 0, 3)"));
            Assert.AreEqual(0d, Number("math.clamp(-1, 0, 3)"));
            Assert.AreEqual(2.5d, Number("math.clamp(2.5, 0, 3)"));
            Assert.AreEqual(4d, Number("math.clamp(9, 4, 4)"), "min equal to max is allowed");
            Assert.AreEqual(2d, Number("math.clamp('2', '0', '3')"), "numeric strings coerce");
            Assert.IsTrue(double.IsNaN(Number("math.clamp(0/0, 0, 1)")), "a NaN value passes through, as in C");
        }

        [Test]
        public void Clamp_RefusesMaxBelowMin_AndANonNumber_NegativeTwin()
        {
            string reversed = Error("math.clamp(1, 3, 0)");
            StringAssert.StartsWith("false|", reversed);
            StringAssert.Contains("bad argument #3", reversed);
            StringAssert.Contains(LuaCsSecureEnvironment.ClampRangeMessage, reversed);
            StringAssert.Contains(LuaCsSecureEnvironment.ClampRangeMessage, Error("math.clamp(1, 0/0, 3)"),
                "a NaN bound fails the min <= max check, as in Luau");
            StringAssert.Contains("bad argument #2", Error("math.clamp(1, 'low', 3)"));
            StringAssert.Contains("(number expected, got no value)", Error("math.clamp(1, 2)"));
        }

        // ---- math.round ------------------------------------------------------------------------

        [Test]
        public void Round_RoundsHalvesAwayFromZero()
        {
            Assert.AreEqual(6d, Number("math.round(5.8)"));
            Assert.AreEqual(1d, Number("math.round(0.5)"));
            Assert.AreEqual(-1d, Number("math.round(-0.5)"));
            Assert.AreEqual(3d, Number("math.round(2.5)"));
            Assert.AreEqual(-3d, Number("math.round(-2.5)"));
            Assert.AreEqual(5d, Number("math.round(5.2)"));
            Assert.AreEqual(0d, Number("math.round(0.49999999999999994)"), "the largest double below a half");
            Assert.AreEqual(double.NegativeInfinity, Number("1 / math.round(-0.4)"), "C round keeps the sign of zero");
            Assert.AreEqual(1e300d, Number("math.round(1e300)"));
            Assert.AreEqual(double.PositiveInfinity, Number("math.round(math.huge)"));
            Assert.IsTrue(double.IsNaN(Number("math.round(0/0)")));
        }

        [Test]
        public void Round_RefusesANonNumber_NegativeTwin()
        {
            StringAssert.Contains("(number expected, got boolean)", Error("math.round(true)"));
            StringAssert.Contains("(number expected, got no value)", Error("math.round()"));
        }

        // ---- math.sign -------------------------------------------------------------------------

        [Test]
        public void Sign_IsMinusOneZeroOrOne()
        {
            Assert.AreEqual(1d, Number("math.sign(5)"));
            Assert.AreEqual(-1d, Number("math.sign(-0.001)"));
            Assert.AreEqual(0d, Number("math.sign(0)"));
            Assert.AreEqual(double.PositiveInfinity, Number("1 / math.sign(-0)"), "zero of either sign gives +0");
            Assert.AreEqual(0d, Number("math.sign(0/0)"), "NaN is neither above nor below zero");
            Assert.AreEqual(1d, Number("math.sign(math.huge)"));
        }

        [Test]
        public void Sign_RefusesANonNumber_NegativeTwin()
        {
            StringAssert.Contains("(number expected, got table)", Error("math.sign({})"));
        }

        // ---- the library around them -----------------------------------------------------------

        [Test]
        public void TheLua52MathLibraryIsUnchanged()
        {
            Assert.AreEqual(2d, Number("math.floor(2.7)"));
            Assert.AreEqual(Math.PI, Number("math.pi"));
            LuaValue[] types = Run("return type(math.noise), type(math.clamp), type(math.round), type(math.sign)");
            foreach (LuaValue type in types)
            {
                Assert.AreEqual("function", type.Read<string>());
            }
        }
    }
}
#endif
