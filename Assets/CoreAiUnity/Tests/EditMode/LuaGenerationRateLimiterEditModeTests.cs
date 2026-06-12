using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="LuaGenerationRateLimiter"/> — the sliding-window guard
    /// against runaway LLM Lua generation loops. Pure C#, injected clock (no MoonSharp needed).
    /// </summary>
    public sealed class LuaGenerationRateLimiterEditModeTests
    {
        [Test]
        public void TryAcquire_AllowsUpToMaxPerWindow_ThenRejects()
        {
            LuaGenerationRateLimiter limiter = new(3, 60);

            Assert.IsTrue(limiter.TryAcquire(0));
            Assert.IsTrue(limiter.TryAcquire(1));
            Assert.IsTrue(limiter.TryAcquire(2));
            Assert.IsFalse(limiter.TryAcquire(3));
            Assert.AreEqual(1, limiter.TotalRejected);
            Assert.AreEqual(3, limiter.GetAcceptedInWindow(3));
        }

        [Test]
        public void TryAcquire_SlidingWindow_FreesSlotsAfterWindowElapses()
        {
            LuaGenerationRateLimiter limiter = new(2, 10);

            Assert.IsTrue(limiter.TryAcquire(0));
            Assert.IsTrue(limiter.TryAcquire(5));
            Assert.IsFalse(limiter.TryAcquire(9));

            // t=10: the t=0 acquisition leaves the window, one slot frees up.
            Assert.IsTrue(limiter.TryAcquire(10));
            Assert.IsFalse(limiter.TryAcquire(11));

            // t=15: the t=5 acquisition expires too.
            Assert.IsTrue(limiter.TryAcquire(15));
        }

        [Test]
        public void TryAcquire_NonPositiveMax_DisablesLimit()
        {
            LuaGenerationRateLimiter limiter = new(0, 1);

            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(limiter.TryAcquire(0));
            }

            Assert.AreEqual(0, limiter.TotalRejected);
        }

        [Test]
        public void Constructor_NonPositiveWindow_FallsBackToDefault()
        {
            LuaGenerationRateLimiter limiter = new(1, -5);
            Assert.AreEqual(LuaGenerationRateLimiter.DefaultWindowSeconds, limiter.WindowSeconds);
        }
    }
}