using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Architecture guards for the general deferred RBXScriptSignal surface.</summary>
    [TestFixture]
    public sealed class RbxScriptSignalEditModeTests
    {
        [Test]
        public void GeneralSignal_HasNoSupportsDispatchSplit()
        {
            Assert.IsNull(typeof(RbxScriptSignal).GetProperty("SupportsDispatch"));
            Assert.IsNull(typeof(RbxScriptSignal).GetConstructor(
                new System.Type[] { typeof(string), typeof(bool) }));
        }

        [Test]
        public void DirectConnectWithoutScheduler_FailsLoudlyWithoutMvpStub()
        {
            RbxScriptSignal signal = new("Test.Signal");
            RbxError error = Assert.Throws<RbxError>(() =>
                signal.Connect((System.Action<object[]>)(_ => { })));

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("no scheduler", error.RawMessage);
        }
    }
}
