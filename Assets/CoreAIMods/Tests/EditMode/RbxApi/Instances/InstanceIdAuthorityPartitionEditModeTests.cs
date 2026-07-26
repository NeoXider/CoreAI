using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>§5.1.8 item 15 / roadmap §3.3: the id space is partitioned by the authority
    /// bit; the two spaces never collide; the wire-marshal guard rejects locally-assigned ids.</summary>
    [TestFixture]
    public sealed class InstanceIdAuthorityPartitionEditModeTests
    {
        [Test]
        public void ServerAndLocalIds_AreDistinguishableByTheAuthorityBit()
        {
            InstanceIdAllocator allocator = new();
            InstanceId server = allocator.Next(InstanceIdAuthority.Server);
            InstanceId local = allocator.Next(InstanceIdAuthority.Local);

            Assert.IsTrue(server.IsServerAssigned);
            Assert.IsFalse(server.IsLocallyAssigned);
            Assert.IsTrue(local.IsLocallyAssigned);
            Assert.IsFalse(local.IsServerAssigned);
            Assert.AreEqual(0UL, server.Value & InstanceId.AuthorityBit);
            Assert.AreEqual(InstanceId.AuthorityBit, local.Value & InstanceId.AuthorityBit);
        }

        [Test]
        public void TheTwoSpaces_NeverCollide()
        {
            InstanceIdAllocator allocator = new();
            HashSet<ulong> seen = new();
            for (int i = 0; i < 1000; i++)
            {
                Assert.IsTrue(seen.Add(allocator.Next(InstanceIdAuthority.Server).Value));
                Assert.IsTrue(seen.Add(allocator.Next(InstanceIdAuthority.Local).Value));
            }
        }

        [Test]
        public void NoneIsInvalidAndNeitherSpace()
        {
            InstanceId none = InstanceId.None;
            Assert.IsFalse(none.IsValid);
            Assert.IsFalse(none.IsServerAssigned);
            Assert.IsFalse(none.IsLocallyAssigned);
        }

        [Test]
        public void WireContract_RejectsLocallyAssignedIds()
        {
            InstanceIdAllocator allocator = new();
            InstanceId server = allocator.Next(InstanceIdAuthority.Server);
            InstanceId local = allocator.Next(InstanceIdAuthority.Local);

            Assert.DoesNotThrow(() => InstanceIdWireContract.EnsureWireSafe(server));
            RbxError error = Assert.Throws<RbxError>(() => InstanceIdWireContract.EnsureWireSafe(local));
            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            Assert.Throws<RbxError>(() => InstanceIdWireContract.EnsureWireSafe(InstanceId.None));
        }

        [Test]
        public void BindNetId_RefusesLocallyAssignedInstances()
        {
            InstanceRegistry registry = new();
            RbxInstance localPart = registry.Create("Part", null, null, InstanceIdAuthority.Local);
            RbxError error = Assert.Throws<RbxError>(() => registry.BindNetId(localPart.Id, 7u));
            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
        }

        [Test]
        public void EnsureNotBelow_AdvancesOnlyTheMatchingSpace()
        {
            InstanceIdAllocator allocator = new();
            allocator.EnsureNotBelow(new InstanceId(500UL));
            Assert.AreEqual(501UL, allocator.Next(InstanceIdAuthority.Server).Value);
            Assert.AreEqual(InstanceId.AuthorityBit | 1UL,
                allocator.Next(InstanceIdAuthority.Local).Value);

            allocator.EnsureNotBelow(new InstanceId(InstanceId.AuthorityBit | 90UL));
            Assert.AreEqual(InstanceId.AuthorityBit | 91UL,
                allocator.Next(InstanceIdAuthority.Local).Value);
        }
    }
}
