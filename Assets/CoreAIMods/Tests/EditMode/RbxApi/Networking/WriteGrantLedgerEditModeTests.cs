using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP12 gate: who may write to a replicated world, and how the host hands that out.
    /// </summary>
    /// <remarks>
    /// WHY the ledger is worth this many tests: it is the only thing standing between "the host let
    /// a friend build" and "any client rewrites the world". Each negative below is a way that could
    /// have been true by accident — a grant issued by a client, a grant that outlives its revocation,
    /// a subtree grant that keeps working after the part left the subtree.
    /// </remarks>
    [TestFixture]
    public sealed class WriteGrantLedgerEditModeTests
    {
        private InstanceRegistry _registry;
        private WriteGrantLedger _ledger;
        private List<string> _audit;
        private long _now;

        [SetUp]
        public void CreateWorld()
        {
            _now = 1_000L;
            _audit = new List<string>();
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "grant-world");
            DataModelBootstrap.CreateGame(_registry);
            _ledger = new WriteGrantLedger(_registry, () => _now, _audit.Add);
        }

        [Test]
        public void Host_CanGrantAClientWriteAccessToOnePart()
        {
            // The owner's case: the host lets one player edit one thing, and nothing else.
            RbxInstance part = Part("Door");
            RbxInstance other = Part("Wall");

            _ledger.Issue("host", true, "actor-friend", WriteGrantScope.Instance, part.Id,
                WriteGrantActions.WriteProperty);

            Assert.IsTrue(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));
            Assert.IsFalse(_ledger.Allows("actor-friend", other.Id, WriteGrantActions.WriteProperty),
                "an instance grant reaches exactly one instance");
            Assert.IsFalse(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.Destroy),
                "the actions a grant does not name are not granted");
            Assert.IsFalse(_ledger.Allows("actor-stranger", part.Id, WriteGrantActions.WriteProperty));
        }

        [Test]
        public void SubtreeGrant_FollowsTheLiveTreeAndStopsWhenThePartLeaves()
        {
            // Resolving against a snapshot taken at issue time would leave a client writing to
            // something the host has since moved out of the granted area.
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance limb = Part("Limb");
            limb.Parent = model;

            _ledger.Issue("host", true, "actor-friend", WriteGrantScope.Subtree, model.Id,
                WriteGrantActions.WriteProperty);
            Assert.IsTrue(_ledger.Allows("actor-friend", limb.Id, WriteGrantActions.WriteProperty));

            limb.Parent = _registry.WorldRoot;

            Assert.IsFalse(_ledger.Allows("actor-friend", limb.Id, WriteGrantActions.WriteProperty),
                "a part moved out of the granted subtree stops being writable the moment it moves");
        }

        [Test]
        public void WorldGrant_CoversEverythingIncludingInstancesCreatedLater()
        {
            _ledger.Issue("host", true, "actor-builder", WriteGrantScope.World, default,
                WriteGrantActions.All);

            RbxInstance latecomer = Part("Spawned");

            Assert.IsTrue(_ledger.Allows("actor-builder", latecomer.Id, WriteGrantActions.Create));
            Assert.IsTrue(_ledger.Allows("actor-builder", latecomer.Id, WriteGrantActions.Destroy));
        }

        [Test]
        public void Negative_ARestrictedActorCannotIssueAGrant()
        {
            // The one that matters: a client that could grant itself access would make the whole
            // ledger decorative.
            RbxInstance part = Part("Door");

            ActorContext client = ClientContext("actor-friend");
            RbxError error = Assert.Throws<RbxError>(() => _ledger.Issue(
                client.ActorId, client.Grants.IsUnrestricted, "actor-friend",
                WriteGrantScope.World, default, WriteGrantActions.All));

            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            Assert.IsFalse(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));
        }

        [Test]
        public void Negative_ARestrictedActorCannotRevokeSomeoneElsesGrant()
        {
            WriteGrant grant = _ledger.Issue("host", true, "actor-a", WriteGrantScope.World,
                default, WriteGrantActions.All);

            ActorContext client = ClientContext("actor-b");
            Assert.Throws<RbxError>(() => _ledger.Revoke(
                client.ActorId, client.Grants.IsUnrestricted, grant.GrantId));
            Assert.Throws<RbxError>(() => _ledger.RevokeAllFor(
                client.ActorId, client.Grants.IsUnrestricted, "actor-a"));
            Assert.IsFalse(grant.Revoked);
        }

        [Test]
        public void Revocation_TakesEffectImmediately()
        {
            RbxInstance part = Part("Door");
            WriteGrant grant = _ledger.Issue("host", true, "actor-friend",
                WriteGrantScope.Instance, part.Id, WriteGrantActions.WriteProperty);
            Assert.IsTrue(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));

            Assert.IsTrue(_ledger.Revoke("host", true, grant.GrantId));

            Assert.IsFalse(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));
            Assert.IsFalse(_ledger.Revoke("host", true, grant.GrantId),
                "revoking twice changes nothing and says so");
        }

        [Test]
        public void Expiry_EndsTheGrantWithoutAnyoneRevokingIt()
        {
            RbxInstance part = Part("Door");
            _ledger.Issue("host", true, "actor-friend", WriteGrantScope.Instance, part.Id,
                WriteGrantActions.WriteProperty, expiresAtUnixSeconds: _now + 60L);
            Assert.IsTrue(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));

            _now += 61L;

            Assert.IsFalse(_ledger.Allows("actor-friend", part.Id, WriteGrantActions.WriteProperty));
            Assert.IsEmpty(_ledger.LiveGrantsFor("actor-friend"));
        }

        [Test]
        public void RevokeAllFor_ClearsEveryGrantAnActorHolds()
        {
            // What a disconnect has to do: a returning player must be granted afresh, not inherit
            // what a previous session was given.
            RbxInstance first = Part("A");
            RbxInstance second = Part("B");
            _ledger.Issue("host", true, "actor-friend", WriteGrantScope.Instance, first.Id,
                WriteGrantActions.WriteProperty);
            _ledger.Issue("host", true, "actor-friend", WriteGrantScope.Instance, second.Id,
                WriteGrantActions.Destroy);

            Assert.AreEqual(2, _ledger.RevokeAllFor("host", true, "actor-friend"));

            Assert.IsEmpty(_ledger.LiveGrantsFor("actor-friend"));
            Assert.AreEqual(0, _ledger.RevokeAllFor("host", true, "actor-friend"));
        }

        [Test]
        public void Negative_AGrantWithNoActionsOrNoGranteeIsRefused()
        {
            RbxInstance part = Part("Door");

            Assert.Throws<RbxError>(() => _ledger.Issue("host", true, "actor-a",
                WriteGrantScope.Instance, part.Id, WriteGrantActions.None),
                "a grant that permits nothing would tell the host it had given access");
            Assert.Throws<RbxError>(() => _ledger.Issue("host", true, "",
                WriteGrantScope.World, default, WriteGrantActions.All));
        }

        [Test]
        public void Negative_AGrantAnchoredToNothingIsRefused()
        {
            // Silently permitting nothing is worse than refusing: the host would believe the friend
            // could build, and only the friend would find out otherwise.
            RbxError error = Assert.Throws<RbxError>(() => _ledger.Issue("host", true, "actor-a",
                WriteGrantScope.Subtree, new InstanceId(999_999UL), WriteGrantActions.All));

            StringAssert.Contains("not in this world", error.Message);
        }

        [Test]
        public void Negative_AGrantThatExpiresInThePastIsRefused()
        {
            RbxInstance part = Part("Door");

            Assert.Throws<RbxError>(() => _ledger.Issue("host", true, "actor-a",
                WriteGrantScope.Instance, part.Id, WriteGrantActions.WriteProperty,
                expiresAtUnixSeconds: _now - 1L));
        }

        [Test]
        public void EveryIssueAndRevoke_IsAudited()
        {
            // A griefed world has to be explainable afterwards, and "who opened the door" is the
            // first question asked.
            RbxInstance part = Part("Door");
            WriteGrant grant = _ledger.Issue("host", true, "actor-friend",
                WriteGrantScope.Instance, part.Id, WriteGrantActions.WriteProperty);
            _ledger.Revoke("host", true, grant.GrantId);

            Assert.AreEqual(2, _audit.Count);
            StringAssert.Contains("issued", _audit[0]);
            StringAssert.Contains("actor-friend", _audit[0]);
            StringAssert.Contains("by=host", _audit[0]);
            StringAssert.Contains("revoked", _audit[1]);
        }

        [Test]
        public void Negative_TheHostIsNotAGranteeInTheLedger()
        {
            // The host holds every right because its writes never enter the client path — not
            // because it has a row here. A row would be something to forge.
            RbxInstance part = Part("Door");

            Assert.IsFalse(_ledger.Allows("host", part.Id, WriteGrantActions.WriteProperty));
            Assert.IsEmpty(_ledger.LiveGrantsFor("host"));
        }

        private RbxInstance Part(string name)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = name;
            part.Parent = _registry.WorldRoot;
            return part;
        }

        private static ActorContext ClientContext(string actorId)
        {
            return new LocalActorIdentityProvider(
                    actorId,
                    "session-" + actorId,
                    "grant-world",
                    ActorGrantSet.Create(new[] { "read" }),
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
        }

    }
}
