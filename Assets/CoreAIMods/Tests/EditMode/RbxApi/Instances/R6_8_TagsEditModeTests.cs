using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Tags per R6.8 (§5.1.8 item 8): add/remove/has/list on Instance plus the
    /// CollectionService GetTagged substrate.</summary>
    [TestFixture]
    public sealed class R6_8_TagsEditModeTests
    {
        private InstanceRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
        }

        [Test]
        public void R6_8_AddHasRemoveListRoundTrip()
        {
            RbxInstance part = _registry.Create("Part");
            part.AddTag("KillBrick");
            part.AddTag("Aura");

            Assert.IsTrue(part.HasTag("KillBrick"));
            Assert.IsFalse(part.HasTag("Nope"));
            CollectionAssert.AreEqual(new[] { "Aura", "KillBrick" }, part.GetTags());

            part.RemoveTag("KillBrick");
            Assert.IsFalse(part.HasTag("KillBrick"));
            CollectionAssert.AreEqual(new[] { "Aura" }, part.GetTags());
        }

        [Test]
        public void R6_8_AddTagIsIdempotent()
        {
            RbxInstance part = _registry.Create("Part");
            part.AddTag("Tag");
            part.AddTag("Tag");
            Assert.AreEqual(1, part.GetTags().Count);
        }

        [Test]
        public void R6_8_GetTaggedSubstrateResolvesAllHolders()
        {
            RbxInstance a = _registry.Create("Part");
            RbxInstance b = _registry.Create("Folder");
            RbxInstance c = _registry.Create("Part");
            a.AddTag("Zone");
            b.AddTag("Zone");
            c.AddTag("Other");

            var tagged = _registry.Tags.GetTagged("Zone");
            Assert.AreEqual(2, tagged.Count);
            CollectionAssert.Contains(tagged, a.Id);
            CollectionAssert.Contains(tagged, b.Id);
        }

        [Test]
        public void R6_8_EmptyTagIsRejected()
        {
            RbxInstance part = _registry.Create("Part");
            RbxError error = Assert.Throws<RbxError>(() => part.AddTag(""));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
        }
    }
}
