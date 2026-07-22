using CoreAI.RobloxApi.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>§5.1.8 item 15 / roadmap §3.3: the id space is partitioned by the authority
    /// bit; the two spaces never collide; the wire-marshal guard rejects locally-assigned ids.</summary>
    [TestFixture]
    public sealed class InstanceIdAuthorityPartitionEditModeTests
    {
        [Test]
        public void ServerAndLocalIds_AreDistinguishableByTheAuthorityBit()
        {
            var allocator = new InstanceIdAllocator();
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
            var allocator = new InstanceIdAllocator();
            var seen = new System.Collections.Generic.HashSet<ulong>();
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
            var allocator = new InstanceIdAllocator();
            InstanceId server = allocator.Next(InstanceIdAuthority.Server);
            InstanceId local = allocator.Next(InstanceIdAuthority.Local);

            Assert.DoesNotThrow(() => InstanceIdWireContract.EnsureWireSafe(server));
            RbxError error = Assert.Throws<RbxError>(
                () => InstanceIdWireContract.EnsureWireSafe(local));
            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            Assert.Throws<RbxError>(() => InstanceIdWireContract.EnsureWireSafe(InstanceId.None));
        }

        [Test]
        public void BindNetId_RefusesLocallyAssignedInstances()
        {
            var registry = new InstanceRegistry();
            RbxInstance localPart = registry.Create("Part", null, null, InstanceIdAuthority.Local);
            RbxError error = Assert.Throws<RbxError>(() => registry.BindNetId(localPart.Id, 7u));
            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
        }

        [Test]
        public void EnsureNotBelow_AdvancesOnlyTheMatchingSpace()
        {
            var allocator = new InstanceIdAllocator();
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
