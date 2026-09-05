using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Networking
{
    /// <summary>
    /// MVP12 gate: what a client's write does before the server has said anything.
    /// </summary>
    [TestFixture]
    public sealed class ClientWritePolicyEditModeTests
    {
        private InstanceRegistry _registry;
        private WriteGrantLedger _ledger;
        private RbxInstance _part;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "policy-world");
            DataModelBootstrap.CreateGame(_registry);
            _ledger = new WriteGrantLedger(_registry, () => 1_000L);
            _part = _registry.Create("Part");
            _part.Name = "Door";
            _part.Parent = _registry.WorldRoot;
        }

        [Test]
        public void Negative_ThereIsNoOpenPolicy()
        {
            // An "apply locally and let it stand" mode is a client authoritative over the world,
            // which is the thing server-authoritative replication exists to prevent. Its absence has
            // to be a test, because a third value would be reachable by configuration — and
            // configuration is what gets copied from a tutorial.
            string[] values = Enum.GetNames(typeof(ClientWritePolicy));

            CollectionAssert.AreEquivalent(new[] { "RobloxParity", "Strict" }, values);
            Assert.IsFalse(values.Contains("Open", StringComparer.OrdinalIgnoreCase));
        }

        [Test]
        public void WithoutAGrant_RobloxParityAppliesLocallyAndReplicatesNothing()
        {
            ClientWriteDisposition disposition = ClientWriteAuthority.Resolve(
                ClientWritePolicy.RobloxParity, _ledger, "actor-a", _part.Id,
                WriteGrantActions.WriteProperty);

            Assert.AreEqual(ClientWriteDisposition.ApplyLocallyOnly, disposition,
                "Roblox's own behaviour: the client sees its change until the server disagrees");
        }

        [Test]
        public void WithoutAGrant_StrictRefusesWhereTheMistakeIs()
        {
            ClientWriteDisposition disposition = ClientWriteAuthority.Resolve(
                ClientWritePolicy.Strict, _ledger, "actor-a", _part.Id,
                WriteGrantActions.WriteProperty);

            Assert.AreEqual(ClientWriteDisposition.Reject, disposition);
        }

        [Test]
        public void WithAGrant_BothPoliciesForwardAndPredictNothing()
        {
            // Applying AND forwarding would mean predicting, and a prediction the server later
            // refuses leaves the client showing a world that never existed.
            _ledger.Issue("host", true, "actor-a", WriteGrantScope.Instance, _part.Id,
                WriteGrantActions.WriteProperty);

            foreach (ClientWritePolicy policy in Enum.GetValues(typeof(ClientWritePolicy)))
            {
                Assert.AreEqual(ClientWriteDisposition.ForwardAsIntent,
                    ClientWriteAuthority.Resolve(policy, _ledger, "actor-a", _part.Id,
                        WriteGrantActions.WriteProperty),
                    policy.ToString());
            }
        }

        [Test]
        public void Negative_AGrantForOneActionDoesNotForwardAnother()
        {
            _ledger.Issue("host", true, "actor-a", WriteGrantScope.Instance, _part.Id,
                WriteGrantActions.WriteProperty);

            Assert.AreEqual(ClientWriteDisposition.Reject,
                ClientWriteAuthority.Resolve(ClientWritePolicy.Strict, _ledger, "actor-a",
                    _part.Id, WriteGrantActions.Destroy),
                "a grant to change a door's colour is not a grant to delete the door");
        }

        [Test]
        public void Negative_NoLedgerAtAll_FallsBackToThePolicy()
        {
            // A world with no ledger is a world where the host granted nothing, not one where
            // everything is allowed.
            Assert.AreEqual(ClientWriteDisposition.Reject,
                ClientWriteAuthority.Resolve(ClientWritePolicy.Strict, null, "actor-a",
                    _part.Id, WriteGrantActions.WriteProperty));
            Assert.AreEqual(ClientWriteDisposition.ApplyLocallyOnly,
                ClientWriteAuthority.Resolve(ClientWritePolicy.RobloxParity, null, "actor-a",
                    _part.Id, WriteGrantActions.WriteProperty));
        }

        [Test]
        public void Intent_CarriesNoIdentityFields()
        {
            // The absence IS the design: a field a client can set is a field the server must not
            // trust, and one that does not exist cannot be forgotten in a validation pass.
            string[] properties = typeof(MutationIntent).GetProperties()
                .Select(property => property.Name).ToArray();

            foreach (string forbidden in new[]
                     { "ActorId", "SenderActorId", "OwnerActorId", "RoleId", "GrantId", "Capability" })
            {
                CollectionAssert.DoesNotContain(properties, forbidden,
                    "MutationIntent must not carry " + forbidden + "; the server stamps the sender "
                    + "from its own connection map");
            }
        }

        [Test]
        public void Intent_MapsEveryActionToAGrant()
        {
            // A new action with no mapping must not fall through to "allowed" — so the mapping is
            // asserted exhaustively rather than sampled.
            foreach (MutationIntentAction action in
                     Enum.GetValues(typeof(MutationIntentAction)))
            {
                MutationIntent intent = new("op-1", _part.Id, 1L, action, "Name",
                    Array.Empty<byte>());

                Assert.AreNotEqual(WriteGrantActions.None, intent.RequiredGrant(), action.ToString());
            }
        }

        [Test]
        public void Negative_AnIntentWithoutAnOperationIdIsRefused()
        {
            // Without it a replayed intent is applied twice, which for "destroy" or "add a coin" is
            // the difference between one action and two.
            Assert.Throws<ArgumentException>(() => new MutationIntent(
                "", _part.Id, 1L, MutationIntentAction.WriteProperty, "Name",
                Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => new MutationIntent(
                null, _part.Id, 1L, MutationIntentAction.Destroy, "", Array.Empty<byte>()));
        }

        [Test]
        public void Intent_NeverHandsBackANullPayload()
        {
            MutationIntent intent = new("op-1", _part.Id, 1L, MutationIntentAction.Destroy,
                null, null);

            Assert.IsNotNull(intent.Member);
            Assert.IsNotNull(intent.EncodedValue);
            Assert.AreEqual(0, intent.EncodedValue.Length);
        }
    }
}
