using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>ServiceProvider semantics on the DataModel: registered container services
    /// resolve; planned services are loud NOT_IMPLEMENTED stubs naming their phase; unknown
    /// names raise UNKNOWN_SERVICE with the exact Roblox text (roadmap §5.2.4).</summary>
    [TestFixture]
    public sealed class ServiceProviderGetServiceEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [Test]
        public void GetService_ResolvesTheMvp1ContainerServices()
        {
            Assert.AreSame(_registry.WorldRoot, _game.GetService("Workspace"));
            Assert.AreEqual("ReplicatedStorage", _game.GetService("ReplicatedStorage").ClassName);
            Assert.AreEqual("ServerStorage", _game.GetService("ServerStorage").ClassName);
            Assert.AreEqual("ServerScriptService", _game.GetService("ServerScriptService").ClassName);
            Assert.AreEqual("StarterPlayer", _game.GetService("StarterPlayer").ClassName);
        }

        [Test]
        public void GetService_ResolvesLightingButLightingIsNotCreatable()
        {
            RbxInstance lighting = _game.GetService("Lighting");
            Assert.AreEqual("Lighting", lighting.ClassName);
            Assert.AreSame(_game, lighting.Parent);

            // WHY: Lighting is a service — GetService resolves it, but Instance.new must reject it
            // loudly (it is not a creatable class).
            RbxError rejected = Assert.Throws<RbxError>(() => _registry.CreateScripted("Lighting"));
            Assert.AreEqual(RbxErrorCode.BadArgument, rejected.Code);
            StringAssert.Contains("Unable to create an Instance of type 'Lighting'", rejected.RawMessage);
        }

        [Test]
        public void GetService_UnknownName_RaisesUnknownServiceWithExactText()
        {
            RbxError error = Assert.Throws<RbxError>(() => _game.GetService("Bogus"));
            Assert.AreEqual(RbxErrorCode.UnknownService, error.Code);
            Assert.AreEqual("Bogus is not a valid Service name", error.RawMessage);
            StringAssert.StartsWith("UNKNOWN_SERVICE: Bogus is not a valid Service name",
                error.Message);
        }

        [Test]
        public void GetService_PlannedService_RaisesLoudStubNamingThePhase()
        {
            RbxError runService = Assert.Throws<RbxError>(() => _game.GetService("RunService"));
            Assert.AreEqual(RbxErrorCode.NotImplemented, runService.Code);
            StringAssert.Contains("MVP2", runService.RawMessage);

            RbxError tween = Assert.Throws<RbxError>(() => _game.GetService("TweenService"));
            StringAssert.Contains("MVP8", tween.RawMessage);

            RbxError dataStore = Assert.Throws<RbxError>(() => _game.GetService("DataStoreService"));
            StringAssert.Contains("MVP9", dataStore.RawMessage);
        }

        [Test]
        public void FindService_ReturnsNullForValidAbsentServicesAndThrowsForUnknown()
        {
            Assert.AreSame(_registry.WorldRoot, _game.FindService("Workspace"));
            Assert.IsNull(_game.FindService("Players"));

            RbxError error = Assert.Throws<RbxError>(() => _game.FindService("Bogus"));
            Assert.AreEqual(RbxErrorCode.UnknownService, error.Code);
        }

        [Test]
        public void BindToClose_IsALoudStubUntilMvp5()
        {
            RbxError error = Assert.Throws<RbxError>(() => _game.BindToClose(null));
            Assert.AreEqual(RbxErrorCode.NotImplemented, error.Code);
            StringAssert.Contains("MVP5", error.RawMessage);
        }
    }
}
