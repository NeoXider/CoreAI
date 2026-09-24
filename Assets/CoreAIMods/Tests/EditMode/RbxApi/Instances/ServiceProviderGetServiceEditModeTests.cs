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
            // WHY: TweenService landed as a live tree-backed service (MVP8 slice 8.4), so
            // DataStoreService (MVP9) stands in as the planned-service probe now.
            RbxInstance service = null;
            Assert.DoesNotThrow(() => service = _game.GetService("DataStoreService"));
            RbxStubService stub = service as RbxStubService;
            Assert.IsNotNull(stub);
            Assert.AreEqual("MVP9", stub.PlannedMvp);
            Assert.AreSame(stub, _game.GetService("DataStoreService"));
        }

        [Test]
        public void FindService_ReturnsNullForValidAbsentServicesAndThrowsForUnknown()
        {
            Assert.AreSame(_registry.WorldRoot, _game.FindService("Workspace"));
            // WHY: Debris landed as a live tree-backed service (MVP8 slice 8.0), and
            // TweenService followed in slice 8.4, so the still-unimplemented
            // DataStoreService stands in as the valid-but-absent example.
            Assert.IsNull(_game.FindService("DataStoreService"));
            Assert.IsNotNull(_game.FindService("TweenService"));

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

        /// <summary>
        /// M1-25: a blank service name is an unknown service with the Roblox text, not a bare .NET
        /// argument exception that carries no code and no fix for the self-repair loop.
        /// </summary>
        [Test]
        public void GetService_BlankName_RaisesUnknownServiceWithTheRobloxText()
        {
            RbxError empty = Assert.Throws<RbxError>(() => _game.GetService(""));
            Assert.AreEqual(RbxErrorCode.UnknownService, empty.Code);
            Assert.AreEqual(" is not a valid Service name", empty.RawMessage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(empty.Fix));

            RbxError blank = Assert.Throws<RbxError>(() => _game.GetService("   "));
            Assert.AreEqual(RbxErrorCode.UnknownService, blank.Code);

            RbxError find = Assert.Throws<RbxError>(() => _game.FindService(""));
            Assert.AreEqual(RbxErrorCode.UnknownService, find.Code);

            RbxError nullName = Assert.Throws<RbxError>(() => _game.Services.GetService(null));
            Assert.AreEqual(RbxErrorCode.UnknownService, nullName.Code);
            Assert.AreEqual(" is not a valid Service name", nullName.RawMessage);
        }

        /// <summary>
        /// M1-05 (service half): a real Roblox service CoreAI does not implement resolves to a loud
        /// stub, so the top-of-file lookup succeeds and the first member access names the verdict.
        /// "X is not a valid Service name" was a false statement for every one of them.
        /// </summary>
        [TestCase("StarterGui")]
        [TestCase("StarterPack")]
        [TestCase("ReplicatedFirst")]
        [TestCase("Teams")]
        [TestCase("PhysicsService")]
        [TestCase("ProximityPromptService")]
        [TestCase("TeleportService")]
        [TestCase("TextChatService")]
        [TestCase("BadgeService")]
        public void GetService_KnownRobloxService_ResolvesAsALoudStubNotAsUnknown(string serviceName)
        {
            RbxInstance service = null;
            Assert.DoesNotThrow(() => service = _game.GetService(serviceName));
            RbxStubService stub = service as RbxStubService;
            Assert.IsNotNull(stub, serviceName + " must resolve to a loud stub");
            Assert.AreEqual(serviceName, stub.ClassName);
            Assert.IsFalse(stub.IsImplementedFallback);
            Assert.AreSame(stub, _game.GetService(serviceName), "a stub is created once and cached");

            RbxError error = stub.MemberAccessError("AnyMember");
            Assert.AreEqual(RbxErrorCode.NotImplemented, error.Code);
            StringAssert.StartsWith(serviceName + ":AnyMember is ", error.RawMessage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.Fix));
        }

        /// <summary>
        /// M1-25: each stub status has its own wording. A planned stub names its rung; a backlog or
        /// unsupported one says so instead of "is planned for no planned MVP (not planned)".
        /// </summary>
        [Test]
        public void StubServices_UseTheWordingOfTheirOwnStatus()
        {
            RbxStubService planned = (RbxStubService)_game.GetService("DataStoreService");
            Assert.AreEqual(RbxKnownUnimplementedMemberStatus.Planned, planned.Status);
            Assert.AreEqual("DataStoreService:GetDataStore is planned for MVP9.",
                planned.MemberAccessError("GetDataStore").RawMessage);

            RbxStubService unsupported = (RbxStubService)_game.GetService("PathfindingService");
            Assert.AreEqual(RbxKnownUnimplementedMemberStatus.Unsupported, unsupported.Status);
            Assert.IsNull(unsupported.PlannedMvp, "an unsupported service names no rung");
            RbxError unsupportedError = unsupported.MemberAccessError("CreatePath");
            Assert.AreEqual(RbxErrorCode.NotImplemented, unsupportedError.Code);
            Assert.AreEqual(
                "PathfindingService:CreatePath is a known Rbx member deliberately unsupported by CoreAI.",
                unsupportedError.RawMessage);

            RbxStubService backlog = (RbxStubService)_game.GetService("Teams");
            Assert.AreEqual(RbxKnownUnimplementedMemberStatus.Backlog, backlog.Status);
            Assert.IsNull(backlog.PlannedMvp);
            Assert.AreEqual("Teams:GetTeams is a known Rbx member, but no roadmap rung is assigned.",
                backlog.MemberAccessError("GetTeams").RawMessage);

            foreach (RbxStubService stub in new[] { unsupported, backlog })
            {
                StringAssert.DoesNotContain("is planned for",
                    stub.MemberAccessError("Probe").RawMessage);
            }
        }

        /// <summary>
        /// M1-25: RunService and UserInputService are implemented. On a DataModel that was never
        /// given the real instances their placeholder still resolves, but it names the missing
        /// attachment instead of claiming the service is planned for a rung it already reached.
        /// </summary>
        [TestCase("RunService")]
        [TestCase("UserInputService")]
        public void ImplementedServiceFallback_NamesTheMissingAttachmentNotARung(string serviceName)
        {
            ServiceCatalog detached = ServiceCatalog.CreateMvp2();

            RbxStubService fallback = detached.GetService(serviceName) as RbxStubService;

            Assert.IsNotNull(fallback, "a bare catalog still resolves the placeholder");
            Assert.IsTrue(fallback.IsImplementedFallback);
            Assert.IsNull(fallback.PlannedMvp);
            RbxError error = fallback.MemberAccessError("Probe");
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains(
                serviceName + " is implemented but is not attached to this DataModel", error.RawMessage);
            StringAssert.DoesNotContain("planned for", error.RawMessage);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", error.Message);
        }

        /// <summary>
        /// Negative twin of the known-service stubs: a host that attaches the real service later
        /// replaces the stub, even one a script already resolved.
        /// </summary>
        [Test]
        public void KnownServiceStub_IsReplacedByTheRealServiceOnceAttached()
        {
            ClassCatalog classCatalog = ClassCatalog.CreateMvp1();
            classCatalog.Register(new ClassDescriptor("StarterGui", "Instance", false, false, true));
            InstanceRegistry registry = new(classCatalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            Assert.IsInstanceOf<RbxStubService>(game.GetService("StarterGui"),
                "precondition: StarterGui starts as the known-service stub");

            RbxInstance real = registry.Create("StarterGui");
            real.Parent = game;

            Assert.AreSame(real, game.GetService("StarterGui"));
            Assert.AreSame(real, game.FindService("StarterGui"));
        }
    }
}
