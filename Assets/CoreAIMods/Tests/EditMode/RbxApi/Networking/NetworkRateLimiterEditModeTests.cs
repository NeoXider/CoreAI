using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP11 gate: the per-actor request budget every bridge shares, and the v2 seam members a real
    /// transport has to answer.
    /// </summary>
    /// <remarks>
    /// WHY the limiter is tested on its own: it used to live inside the loopback bridge, where a
    /// Mirror bridge written later could simply not have called it — a transport facing a real
    /// network with no budget at all, which is the one place the budget matters. These tests pin the
    /// behaviour to the shared type so the next transport inherits it instead of reinventing it.
    /// </remarks>
    [TestFixture]
    public sealed class NetworkRateLimiterEditModeTests
    {
        [Test]
        public void Budget_AcceptsExactlyItsLimitPerSecond()
        {
            double now = 100d;
            RbxNetworkRateLimiter limiter = new(3, () => now);

            for (int request = 0; request < 3; request++)
            {
                Assert.DoesNotThrow(
                    () => limiter.Admit("a", RbxNetworkRateGroup.ReliableRemoteEvent),
                    "request " + request + " is inside the budget");
            }

            RbxError error = Assert.Throws<RbxError>(
                () => limiter.Admit("a", RbxNetworkRateGroup.ReliableRemoteEvent));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            StringAssert.Contains("reliable RemoteEvent", error.Message,
                "the refusal must name which budget ran out, or an operator cannot act on it");
            StringAssert.Contains("'a'", error.Message);
        }

        [Test]
        public void Budget_ResetsOnTheNextSecond()
        {
            double now = 10d;
            RbxNetworkRateLimiter limiter = new(1, () => now);
            limiter.Admit("a", RbxNetworkRateGroup.RemoteFunction);
            Assert.Throws<RbxError>(() => limiter.Admit("a", RbxNetworkRateGroup.RemoteFunction));

            now = 11.01d;

            Assert.DoesNotThrow(() => limiter.Admit("a", RbxNetworkRateGroup.RemoteFunction));
        }

        [Test]
        public void Budget_IsPerActorAndPerGroup()
        {
            // A noisy client must not spend a quiet one's budget, and a flood of unreliable fires
            // must not lock out the RemoteFunction a game needs to answer with.
            double now = 0d;
            RbxNetworkRateLimiter limiter = new(1, () => now);
            limiter.Admit("a", RbxNetworkRateGroup.UnreliableRemoteEvent);

            Assert.DoesNotThrow(() => limiter.Admit("b", RbxNetworkRateGroup.UnreliableRemoteEvent));
            Assert.DoesNotThrow(() => limiter.Admit("a", RbxNetworkRateGroup.RemoteFunction));
            Assert.Throws<RbxError>(
                () => limiter.Admit("a", RbxNetworkRateGroup.UnreliableRemoteEvent));
        }

        [Test]
        public void Negative_ABackwardsClock_ReopensTheWindowInsteadOfFreezingIt()
        {
            // A host correcting its system time must not lock every client out until the clock
            // catches up — the failure would look exactly like a server that stopped responding.
            double now = 1000d;
            RbxNetworkRateLimiter limiter = new(1, () => now);
            limiter.Admit("a", RbxNetworkRateGroup.ReliableRemoteEvent);

            now = 5d;

            Assert.DoesNotThrow(() => limiter.Admit("a", RbxNetworkRateGroup.ReliableRemoteEvent));
        }

        [Test]
        public void Forget_ReleasesAnActorsWindows()
        {
            double now = 0d;
            RbxNetworkRateLimiter limiter = new(5, () => now);
            limiter.Admit("a", RbxNetworkRateGroup.ReliableRemoteEvent);
            limiter.Admit("b", RbxNetworkRateGroup.ReliableRemoteEvent);
            Assert.AreEqual(2, limiter.TrackedActorCount);

            limiter.Forget("a");

            Assert.AreEqual(1, limiter.TrackedActorCount,
                "a disconnected actor must not keep a window alive; that is the leak a long-running "
                + "server accumulates");
        }

        [Test]
        public void Negative_AnActorlessRequestIsRefused()
        {
            // The sender is resolved by the bridge from its own connection map. An empty actor here
            // means the bridge trusted the packet, which is the shape MVP11's admission forbids.
            RbxNetworkRateLimiter limiter = new(5, () => 0d);

            Assert.Throws<ArgumentException>(
                () => limiter.Admit("", RbxNetworkRateGroup.ReliableRemoteEvent));
            Assert.Throws<ArgumentException>(
                () => limiter.Admit(null, RbxNetworkRateGroup.ReliableRemoteEvent));
        }

        [Test]
        public void Negative_AZeroBudgetIsRefusedAtConstruction()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RbxNetworkRateLimiter(0, () => 0d));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RbxNetworkRateLimiter(-1, () => 0d));
            Assert.Throws<ArgumentNullException>(() => new RbxNetworkRateLimiter(5, null));
        }

        [Test]
        public void LoopbackBridge_StillEnforcesTheBudgetThroughTheSharedLimiter()
        {
            // The refactor's own regression twin: the loopback must keep refusing a flood, and it
            // must do so through the shared type rather than a copy left behind.
            NullNetworkBridge bridge = new(maxClientRequestsPerSecond: 2, clockSeconds: () => 0d);
            bridge.RegisterActor("a");

            bridge.SendEvent(ClientEvent("a"));
            bridge.SendEvent(ClientEvent("a"));
            RbxError error = Assert.Throws<RbxError>(() => bridge.SendEvent(ClientEvent("a")));

            Assert.AreEqual(RbxErrorCode.BudgetExceeded, error.Code);
            Assert.AreEqual(2, bridge.MaxClientRequestsPerSecond);
            Assert.AreEqual(1, bridge.RateWindowCount);
        }

        [Test]
        public void EveryShippedBridge_AnswersTheV2SeamMembers()
        {
            // WHY reflection over the assemblies: a new transport that leaves MaxPayloadBytes at zero
            // would refuse every message, and one that reports int.MaxValue would accept payloads its
            // wire cannot carry. Both are silent until someone sends a big message online.
            List<string> problems = new();

            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(assembly => assembly.GetName().Name.StartsWith("CoreAI", StringComparison.Ordinal))
                         .SelectMany(SafeTypes)
                         .Where(candidate => candidate != null
                                             && !candidate.IsInterface
                                             && !candidate.IsAbstract
                                             && typeof(INetworkBridge).IsAssignableFrom(candidate)))
            {
                PropertyInfo payload = type.GetProperty(nameof(INetworkBridge.MaxPayloadBytes));
                if (payload == null)
                {
                    problems.Add(type.FullName + " does not expose MaxPayloadBytes");
                }
            }

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void LoopbackBridge_ReportsTheCodecCeilingAndNoClockOffset()
        {
            NullNetworkBridge bridge = new();

            Assert.AreEqual(65536, bridge.MaxPayloadBytes,
                "the loopback has no MTU, so the codec's own ceiling is the only bound");
            Assert.AreEqual(0d, bridge.ServerClockOffsetSeconds, 1e-12d,
                "the loopback IS the server; there is nothing to correct for");
        }

        private static RbxNetworkEventMessage ClientEvent(string actorId)
        {
            return new RbxNetworkEventMessage(
                new InstanceId(1UL),
                RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered,
                actorId,
                null,
                Array.Empty<byte>());
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }
    }
}
