using CoreAI.Mods.Roblox.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>The §5.2.7 error contract (stable wire names, format, fix hints) and the
    /// §5.1.6 loud-stub inventory that belongs to the registry slice (§5.1.8 item 13):
    /// signal Connect/Once/Wait stubs naming MVP2.</summary>
    [TestFixture]
    public sealed class RbxErrorAndLoudStubEditModeTests
    {
        [Test]
        public void WireNames_AreTheLockedScreamingSnakeSet()
        {
            Assert.AreEqual("NOT_IMPLEMENTED", RbxError.ToWireName(RbxErrorCode.NotImplemented));
            Assert.AreEqual("BAD_ARGUMENT", RbxError.ToWireName(RbxErrorCode.BadArgument));
            Assert.AreEqual("UNKNOWN_SERVICE", RbxError.ToWireName(RbxErrorCode.UnknownService));
            Assert.AreEqual("INSTANCE_DESTROYED", RbxError.ToWireName(RbxErrorCode.InstanceDestroyed));
            Assert.AreEqual("PARENT_LOCKED", RbxError.ToWireName(RbxErrorCode.ParentLocked));
            Assert.AreEqual("BUDGET_EXCEEDED", RbxError.ToWireName(RbxErrorCode.BudgetExceeded));
            Assert.AreEqual("SIGNAL_CASCADE", RbxError.ToWireName(RbxErrorCode.SignalCascade));
            Assert.AreEqual("THREAD_CAP", RbxError.ToWireName(RbxErrorCode.ThreadCap));
            Assert.AreEqual("CYCLIC_REQUIRE", RbxError.ToWireName(RbxErrorCode.CyclicRequire));
            Assert.AreEqual("API_VERSION_MISMATCH", RbxError.ToWireName(RbxErrorCode.ApiVersionMismatch));
            Assert.AreEqual("NOT_AUTHORITY", RbxError.ToWireName(RbxErrorCode.NotAuthority));
            Assert.AreEqual("PAYLOAD_TOO_LARGE", RbxError.ToWireName(RbxErrorCode.PayloadTooLarge));
            Assert.AreEqual("CONTEXT_VIOLATION", RbxError.ToWireName(RbxErrorCode.ContextViolation));
        }

        [Test]
        public void Format_MatchesTheSelfRepairContract()
        {
            var error = new RbxError(RbxErrorCode.NotImplemented,
                "TweenService:Create is planned for MVP8.",
                "animate manually with RunService.Heartbeat + lerp until then",
                "speed_pad", "server/main.lua", 12);

            Assert.AreEqual(
                "[mod:speed_pad script:server/main.lua line:12] NOT_IMPLEMENTED: " +
                "TweenService:Create is planned for MVP8. | fix: animate manually with " +
                "RunService.Heartbeat + lerp until then",
                error.Message);
        }

        [Test]
        public void WithContext_AttachesModContextWithoutChangingTheBody()
        {
            RbxError bare = RbxError.NotImplemented("X", "MVP2", "wait for MVP2");
            StringAssert.StartsWith("NOT_IMPLEMENTED:", bare.Message);

            RbxError contextual = bare.WithContext("my_mod", "shared/init.lua", 3);
            Assert.AreEqual(bare.Code, contextual.Code);
            Assert.AreEqual(bare.RawMessage, contextual.RawMessage);
            StringAssert.StartsWith("[mod:my_mod script:shared/init.lua line:3]", contextual.Message);
        }

        [Test]
        public void SignalConnect_IsALoudStubNamingMvp2()
        {
            var registry = new InstanceRegistry();
            RbxInstance part = registry.Create("Part");

            RbxError connect = Assert.Throws<RbxError>(() => part.ChildAdded.Connect(null));
            Assert.AreEqual(RbxErrorCode.NotImplemented, connect.Code);
            StringAssert.Contains("MVP2", connect.RawMessage);
            StringAssert.Contains("ChildAdded", connect.RawMessage);

            Assert.Throws<RbxError>(() => part.Destroying.Once(null));
            Assert.Throws<RbxError>(() => part.AttributeChanged.Wait());
            Assert.Throws<RbxError>(() => part.GetPropertyChangedSignal("Name").Connect(null));
            Assert.Throws<RbxError>(() => part.GetAttributeChangedSignal("Health").Connect(null));
        }

        [Test]
        public void SignalProperties_ExistAsInertHookPoints()
        {
            var registry = new InstanceRegistry();
            RbxInstance part = registry.Create("Part");

            Assert.IsNotNull(part.ChildAdded);
            Assert.IsNotNull(part.ChildRemoved);
            Assert.IsNotNull(part.DescendantAdded);
            Assert.IsNotNull(part.DescendantRemoving);
            Assert.IsNotNull(part.Destroying);
            Assert.IsNotNull(part.AncestryChanged);
            Assert.IsNotNull(part.AttributeChanged);
            Assert.AreSame(part.ChildAdded, part.ChildAdded, "signals are cached per instance");
        }
    }
}
