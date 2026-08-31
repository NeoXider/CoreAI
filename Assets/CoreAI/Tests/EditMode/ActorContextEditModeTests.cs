using System;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>Contract tests for actor identity and narrowing-only grants.</summary>
    public sealed class ActorContextEditModeTests
    {
        [Test]
        public void Grants_CannotBeWidenedAfterNarrowing()
        {
            ActorGrantSet original = ActorGrantSet.Create(new[] { "read" });
            LocalActorIdentityProvider provider = new(
                "actor-a",
                "session-a",
                "world-a",
                original,
                AgentMemoryScope.Empty);
            ActorContext context = provider.GetActorContext("shared-role");

            ActorContext attemptedWidening = context.NarrowGrants(
                ActorGrantSet.Create(new[] { "read", "write" }));

            Assert.IsTrue(attemptedWidening.Grants.Contains("read"));
            Assert.IsFalse(attemptedWidening.Grants.Contains("write"));
            Assert.AreEqual(0, typeof(ActorContext).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
            Assert.Throws<InvalidOperationException>(() => default(ActorContext).NarrowGrants(ActorGrantSet.Unrestricted));
        }

        [Test]
        public void Grants_NarrowByIntersectionWithoutMutatingSource()
        {
            ActorGrantSet source = ActorGrantSet.Create(new[] { "read", "write" });

            ActorGrantSet narrowed = source.NarrowTo(ActorGrantSet.Create(new[] { "read" }));

            Assert.IsTrue(source.Contains("read"));
            Assert.IsTrue(source.Contains("write"));
            Assert.IsTrue(narrowed.Contains("read"));
            Assert.IsFalse(narrowed.Contains("write"));
        }

        [Test]
        public void DefaultLocalActor_PreservesLegacyRoleOnlyMemoryBehavior()
        {
            LocalActorIdentityProvider provider = ActorIdentityComposition.CreateLocalHost();
            ActorContext first = provider.GetActorContext("Merchant");
            ActorContext second = provider.GetActorContext("Merchant");

            Assert.AreEqual(LocalActorIdentityProvider.DefaultActorId, first.ActorId);
            Assert.AreEqual(first.ActorId, second.ActorId);
            Assert.AreEqual(first.SessionId, second.SessionId);
            Assert.AreEqual("Merchant", first.RoleId);
            Assert.AreEqual("", first.WorldId);
            Assert.IsTrue(first.Grants.IsUnrestricted);
            Assert.AreEqual("Merchant", AgentMemoryScopeKey.Resolve(first.MemoryScope, first.RoleId));
        }

        [Test]
        public void RuntimeLocalProvider_CannotIssueUnrestrictedContext()
        {
            ActorContext context = new LocalActorIdentityProvider("runtime-actor")
                .GetActorContext("runtime-role");

            Assert.IsTrue(context.IsTrusted);
            Assert.IsFalse(context.Grants.IsUnrestricted);
            Assert.IsNull(typeof(LocalActorIdentityProvider).GetProperty(
                "Default",
                BindingFlags.Public | BindingFlags.Static));
            Assert.Throws<InvalidOperationException>(() => new LocalActorIdentityProvider(
                "runtime-actor",
                "runtime-session",
                "",
                ActorGrantSet.Unrestricted,
                AgentMemoryScope.Empty));
        }

        [Test]
        public void PublicProviderSubclass_CannotEscalate()
        {
            ActorContext context = new PublicProviderSubclass().GetActorContext("runtime-role");

            Assert.IsTrue(context.IsTrusted);
            Assert.IsFalse(context.Grants.IsUnrestricted);
            Assert.IsNull(typeof(ActorIdentityProviderBase).GetMethod(
                "IssueActorContext",
                BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Test]
        public void InvalidCompositionCapability_CannotIssueUnrestrictedContext()
        {
            Assert.Throws<InvalidOperationException>(() => LocalActorIdentityProvider.CreateForComposition(
                new object(),
                "forged-actor",
                "forged-session",
                "",
                AgentMemoryScope.Empty));
        }

        [Test]
        public void SameRoleActors_RemainDistinctIdentities()
        {
            ActorContext first = new LocalActorIdentityProvider("actor-a").GetActorContext("shared-role");
            ActorContext second = new LocalActorIdentityProvider("actor-b").GetActorContext("shared-role");

            Assert.AreEqual(first.RoleId, second.RoleId);
            Assert.AreNotEqual(first.ActorId, second.ActorId);
        }

        [Test]
        public void Reconnect_RotatesSessionWithoutChangingActorId()
        {
            ActorContext firstConnection = new LocalActorIdentityProvider("durable-actor")
                .GetActorContext("shared-role");
            ActorContext secondConnection = new LocalActorIdentityProvider("durable-actor")
                .GetActorContext("shared-role");

            Assert.AreEqual(firstConnection.ActorId, secondConnection.ActorId);
            Assert.AreNotEqual(firstConnection.SessionId, secondConnection.SessionId);
        }

        private sealed class PublicProviderSubclass : ActorIdentityProviderBase
        {
            public override ActorContext GetActorContext(string roleId)
            {
                return new LocalActorIdentityProvider("subclass-actor").GetActorContext(roleId);
            }
        }
    }
}
