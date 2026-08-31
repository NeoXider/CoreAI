using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>ServiceProvider semantics on the DataModel: registered services resolve,
    /// planned services resolve as deferred loud stubs, and unknown names raise UNKNOWN_SERVICE
    /// with the exact Roblox text (roadmap §5.2.4).</summary>
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
        public void GetService_ResolvesAServiceRegisteredInTheCatalog()
        {
            ClassCatalog classCatalog = ClassCatalog.CreateMvp1();
            classCatalog.Register(new ClassDescriptor(
                "TestService", "Instance", false, false, true));
            InstanceRegistry registry = new(classCatalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance service = registry.Create("TestService");

            game.Services.Register("TestService", service);

            Assert.AreSame(service, game.GetService("TestService"));
        }

        [Test]
        public void GetService_ResolvesServicesPulledForwardFromLaterRungs()
        {
            Assert.IsInstanceOf<RbxRunService>(_game.GetService("RunService"));
            Assert.IsInstanceOf<RbxUserInputService>(_game.GetService("UserInputService"));
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
        public void GetService_PlannedService_ReturnsCachedStubWithoutThrowing()
        {
            RbxInstance service = null;
            Assert.DoesNotThrow(() => service = _game.GetService("TweenService"));
            RbxStubService stub = service as RbxStubService;
            Assert.IsNotNull(stub);
            Assert.AreEqual("MVP8", stub.PlannedMvp);
            Assert.AreSame(stub, _game.GetService("TweenService"));
        }

        [Test]
        public void FindService_ReturnsNullForValidAbsentServicesAndThrowsForUnknown()
        {
            Assert.AreSame(_registry.WorldRoot, _game.FindService("Workspace"));
            Assert.IsNull(_game.FindService("Debris"));

            // WHY: Players is no longer absent — the loopback networking rung pulled its minimum
            // surface forward so RemoteEvent.OnServerEvent can hand Lua a real Player, as Roblox does.
            Assert.IsNotNull(_game.FindService("Players"));

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
