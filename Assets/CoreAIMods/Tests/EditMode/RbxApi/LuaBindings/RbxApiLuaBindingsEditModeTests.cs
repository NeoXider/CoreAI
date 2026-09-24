using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// End-to-end proof of the Roblox MVP1 Lua surface (roadmap §5.1.3) through the REAL mod
    /// runtime: corpus-style snippets loaded via <see cref="LuaCsModRuntimeFactory"/> exercising
    /// datatype constructors/operators, Enum access, Instance.new over the registry whitelist,
    /// game/workspace navigation, §5.2.7 error texts, ownership/origin attribution, and
    /// capability gating. Test names cite rule ids where one applies (§6.6).
    /// </summary>
    /// <remarks>
    /// WHY no VContainer or GameObject test here: this file runs whole on Linux in
    /// tools/portable/LuaTests. The tests that build the production container are
    /// RbxApiLuaBindingsProductionContainerEditModeTests, and the Lua position golden through the
    /// GameObject binder is in InstanceGameObjectBinderCrossLayerEditModeTests.
    /// </remarks>
    [TestFixture]
    public sealed class RbxApiLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as LuaCsModRuntimeEditModeTests: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class TrackingNetworkBridge : INetworkBridge
        {
            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            private readonly List<string> _actors = new();
            private Action<RbxNetworkEventMessage> _eventReceived;
            private Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> _requestReceived;

            public RbxNetworkTopology Topology => RbxNetworkTopology.Solo;

            public IReadOnlyList<string> ActorIds => _actors;

            public int EventSubscriberCount => _eventReceived?.GetInvocationList().Length ?? 0;

            public int RequestSubscriberCount => _requestReceived?.GetInvocationList().Length ?? 0;

            public bool DropRequests { get; set; }

            public Action<RbxNetworkResponse> LastResponse { get; private set; }

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add => _eventReceived += value;
                remove => _eventReceived -= value;
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add => _requestReceived += value;
                remove => _requestReceived -= value;
            }

            public void RegisterActor(string actorId)
            {
                if (!_actors.Contains(actorId))
                {
                    _actors.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actors.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
                _eventReceived?.Invoke(message);
            }

            public void SendRequest(RbxNetworkRequestMessage message,
                Action<RbxNetworkResponse> response)
            {
                LastResponse = response;
                if (DropRequests)
                {
                    return;
                }

                Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> receiver =
                    _requestReceived;
                if (receiver != null)
                {
                    receiver(message, new RbxNetworkRequestResponder(response));
                }
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox,
            MemoryStore store = null, LuaCapabilities caps = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store ?? new MemoryStore(),
                Capabilities = caps,
                OneOffCapabilities = caps,
                RbxApi = roblox
            });
        }

        private static Exception LoadFails(LuaCsModStack stack, string modId, string code)
        {
            Exception ex = Assert.Catch(() => stack.Runtime.LoadMod(modId, code));
            return ex;
        }

        private static string FullText(Exception ex)
        {
            return ex.ToString();
        }

        private static ActorContext Actor(string actorId)
        {
            return new LocalActorIdentityProvider(actorId)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static LuaCsRbxApiBindings StrictWorld(out InstanceRegistry instanceRegistry)
        {
            instanceRegistry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            return new LuaCsRbxApiBindings(registry: instanceRegistry);
        }

        private static RbxInstance CreateActorInstance(InstanceRegistry registry,
            ActorContext actorContext, string modId, string className)
        {
            string originTag = OriginTag.FromMod(modId);
            registry.BindActorAttribution(modId, originTag, actorContext.ActorId);
            return registry.CreateScripted(className, modId, originTag);
        }

        private static InstanceRecord Record(InstanceRegistry registry, RbxInstance instance)
        {
            Assert.IsTrue(registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record;
        }

        private static LuaTool.LuaResult ExecuteMutation(LuaCsModStack stack,
            ActorContext actorContext, MutationEnvelope envelope, string source)
        {
            return stack.ToolExecutor.ExecuteAsync(
                    source, actorContext, envelope, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static void RunActorLua(LuaCsRbxApiBindings bindings, ActorContext actorContext,
            string modId, string source,
            params (string Name, RbxInstance Instance)[] instanceGlobals)
        {
            LuaCsRbxModContext context = new(
                bindings, LuaCapabilities.All, modId, OriginTag.FromMod(modId), actorContext);
            LuaCsScriptEngine engine = new();
            LuaCsApiRegistry registry = (LuaCsApiRegistry)engine.CreateFunctionRegistry();
            registry.RegisterValue("CFrame", LuaCsRbxDatatypeBindings.BuildCFrameGlobal);
            for (int index = 0; index < instanceGlobals.Length; index++)
            {
                (string Name, RbxInstance Instance) global = instanceGlobals[index];
                registry.RegisterValue(global.Name, () => context.WrapInstance(global.Instance));
            }

            IScriptState state = engine.CreateState();
            registry.ApplyTo(state);
            engine.RunChunk(state, source);
        }

        [Test]
        public void MutationEnvelope_ResultRetention_IsBoundedPerActor()
        {
            const int expectedPerActorLimit =
                InstanceRegistry.DefaultMutationReplayCapacityPerActor;
            const string actorId = "bounded-cache-actor";
            InstanceRegistry registry = new();
            RbxInstance target = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            long revision = Record(registry, target).Revision;

            for (int index = 0; index <= expectedPerActorLimit; index++)
            {
                MutationEnvelope envelope = new(
                    actorId,
                    target.Id,
                    "bounded-operation-" + index,
                    revision);
                registry.ApplyMutation(envelope, () =>
                {
                    revision = registry.AdvanceRevision(target.Id);
                    return index;
                });
            }

            Assert.LessOrEqual(registry.RetainedMutationOperationCount, expectedPerActorLimit);
        }

        [Test]
        public void MutationEnvelope_UnparentedInstanceNew_AdvancesCreationAnchorRevision()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            RbxInstance anchor = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            anchor.Name = "CreationAnchor";
            anchor.Parent = registry.WorldRoot;
            ActorContext actor = Actor("create-actor");
            long initialRevision = Record(registry, anchor).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-1", initialRevision);

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope,
                "local created=Instance.new('Folder'); return created.ClassName");

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("Folder", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L, Record(registry, anchor).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, anchor.Id, "create-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope,
                "Instance.new('Folder'); return 'unexpected'");

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [Test]
        public void MutationEnvelope_Clone_AdvancesSourceRevision()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            RbxInstance source = registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            source.Name = "CloneRevisionSource";
            source.Parent = registry.WorldRoot;
            ActorContext actor = Actor("clone-actor");
            long initialRevision = Record(registry, source).Revision;
            int initialCount = registry.Count;
            MutationEnvelope firstEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-1", initialRevision);
            const string cloneSource = @"
                local source = workspace:FindFirstChild('CloneRevisionSource')
                local copy = source:Clone()
                return copy.Name";

            LuaTool.LuaResult first = ExecuteMutation(
                stack, actor, firstEnvelope, cloneSource);

            Assert.IsTrue(first.Success, first.Error);
            Assert.AreEqual("CloneRevisionSource", first.Output);
            Assert.Greater(registry.Count, initialCount);
            int countAfterFirst = registry.Count;
            Assert.AreEqual(initialRevision + 1L, Record(registry, source).Revision);

            MutationEnvelope staleEnvelope = new(
                actor.ActorId, source.Id, "clone-operation-2", initialRevision);
            LuaTool.LuaResult stale = ExecuteMutation(
                stack, actor, staleEnvelope, cloneSource);

            Assert.IsFalse(stale.Success);
            StringAssert.Contains("stale expected revision", stale.Error);
            Assert.AreEqual(countAfterFirst, registry.Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Lua_HeadlessPartSink_DestroyingHandlerReadsLastValuesAndLiveStateIsReleased(
            bool hostSinkWithoutRegistry)
        {
            InMemoryPartPropertySink hostSink = hostSinkWithoutRegistry
                ? new InMemoryPartPropertySink()
                : null;
            LuaCsRbxApiBindings bindings = new(partSink: hostSink);
            InMemoryPartPropertySink sink = (InMemoryPartPropertySink)bindings.PartSink;
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            int liveBefore = sink.LivePartCount;

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Size = Vector3.new(3, 5, 7)
                part.Position = Vector3.new(10, 20, 30)
                part.Parent = workspace
                part.Destroying:Connect(function()
                    store_set('size', tostring(part.Size))
                    store_set('position', tostring(part.Position))
                end)
                part:Destroy()");

            // WHY both sinks: the headless default hears destruction from the registry itself, and a sink
            // built without the registry (a host sink, a fresh restore's) hears it from the bindings.
            Assert.AreEqual(liveBefore, sink.LivePartCount, "the destroyed part's live state is released");
            Assert.AreEqual(1, sink.RetainedDestroyedPartCount);

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("3, 5, 7", store.Get("m", "size"),
                "a Destroying handler reads the part's last Size, not the default");
            Assert.AreEqual("10, 20, 30", store.Get("m", "position"));
        }

        [Test]
        public void NetworkActorRegistration_PlayerFailureDoesNotLeaveBridgeActor()
        {
            InstanceRegistry registry = new(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            TrackingNetworkBridge bridge = new();
            LuaCsRbxApiBindings bindings = new(
                registry: registry, game: game, networkBridge: bridge);
            ActorContext actor = Actor("rejected-player-actor");
            LuaCsRbxModContext context = new(
                bindings, LuaCapabilities.All, "rejected-player-mod",
                OriginTag.FromMod("rejected-player-mod"), actor);
            registry.Registered += record =>
            {
                if (record.Instance is RbxPlayer)
                {
                    record.Instance.Destroy();
                    throw new InvalidOperationException("synthetic Player registration rejected");
                }
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => bindings.GetLocalPlayer(context));

            StringAssert.Contains("registration rejected", error.Message);
            Assert.IsEmpty(bridge.ActorIds);
            Assert.IsEmpty(bindings.Players.GetPlayers());
        }

        [Test]
        public void Lua_NetworkProductionPath_RemoteFunctionDroppedRequestTimesOutAndIgnoresLateResponse()
        {
            const string modId = "network-dropped-request";
            const string actorId = "dropped-request-actor";
            TrackingNetworkBridge bridge = new() { DropRequests = true };
            LuaCsRbxApiBindings bindings = new(networkBridge: bridge);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            ActorContext actorContext = Actor(actorId);

            stack.Runtime.LoadMod(actorContext, modId, @"
                local remote = Instance.new('RemoteFunction')
                remote.Name = 'DroppedRequestRemote'
                remote.Parent = workspace
                local ok, failure = pcall(function()
                    return remote:InvokeServer()
                end)
                store_set('ok', tostring(ok))
                store_set('failure', tostring(failure))", persistToStore: false);

            Assert.AreEqual("", store.Get(modId, "ok"));
            Assert.AreEqual(1, bindings.CountRemoteFunctionWaitsOwnedBy(modId));

            bindings.Scheduler.Advance(
                LuaCsRbxApiBindings.RemoteFunctionInvokeTimeoutSeconds);

            Assert.AreEqual("false", store.Get(modId, "ok"));
            StringAssert.Contains(actorId, store.Get(modId, "failure"));
            StringAssert.Contains("DroppedRequestRemote", store.Get(modId, "failure"));
            StringAssert.Contains("timed out after 30 seconds", store.Get(modId, "failure"));
            Assert.AreEqual(0, bindings.CountRemoteFunctionWaitsOwnedBy(modId));
            Assert.AreEqual(0, bindings.Scheduler.LiveThreadCount);

            bridge.LastResponse(RbxNetworkResponse.Failure("late response"));

            Assert.AreEqual("false", store.Get(modId, "ok"));
            StringAssert.DoesNotContain("late response", store.Get(modId, "failure"));
            Assert.AreEqual(0, bindings.CountRemoteFunctionWaitsOwnedBy(modId));
        }

        // ---- Datatypes ----------------------------------------------------------------------

        [Test]
        public void Lua_Vector3_ConstructorsOperatorsAndTostring()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Vector3.new(1, 2, 3)
                assert(p.X == 1 and p.Y == 2 and p.Z == 3)
                assert(tostring(p) == '1, 2, 3')
                assert(p == Vector3.new(1, 2, 3))
                assert(p ~= Vector3.new(3, 2, 1))
                local q = p + Vector3.new(1, 1, 1)
                assert(tostring(q) == '2, 3, 4')
                assert((p - p) == Vector3.zero)
                assert((p * 2).Y == 4)
                assert((2 * p).Z == 6)
                assert((p * Vector3.new(2, 2, 2)).X == 2)
                assert((p / 2).X == 0.5)
                assert((-p).X == -1)
                assert(p:Dot(Vector3.new(1, 0, 0)) == 1)
                assert(Vector3.xAxis:Cross(Vector3.yAxis) == Vector3.zAxis)
                assert(Vector3.new(3, 4, 0).Magnitude == 5)
                assert(Vector3.new(10, 0, 0).Unit == Vector3.xAxis)
                assert(Vector3.zero:Lerp(Vector3.one, 0.5) == Vector3.new(0.5, 0.5, 0.5))
                assert(Vector3.FromNormalId(Enum.NormalId.Front) == Vector3.new(0, 0, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_CFrame_MathMatchesPureSpec()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local cf = CFrame.new(0, 5, 0) * CFrame.Angles(0, math.pi / 2, 0)
                assert(cf.Position == Vector3.new(0, 5, 0))
                -- WHY: right-handed spec — yaw of +90deg turns LookVector (-Z) onto -X.
                assert(near(cf.LookVector.X, -1) and near(cf.LookVector.Z, 0))
                local moved = CFrame.new(1, 2, 3) * Vector3.new(0, 0, -1)
                assert(moved == Vector3.new(1, 2, 2))
                local roundtrip = cf:ToObjectSpace(cf:ToWorldSpace(CFrame.new(7, 8, 9)))
                assert(near(roundtrip.X, 7) and near(roundtrip.Y, 8) and near(roundtrip.Z, 9))
                local look = CFrame.lookAt(Vector3.zero, Vector3.new(0, 0, -10))
                assert(near(look.LookVector.Z, -1))
                local x, y, z, r00 = CFrame.identity:GetComponents()
                assert(x == 0 and y == 0 and z == 0 and r00 == 1)
                assert(CFrame.new() == CFrame.identity)
                assert((CFrame.new(1, 1, 1) + Vector3.new(0, 1, 0)).Y == 2)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Color3_UDim2_Vector2_ConstructorsAndMembers()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local c = Color3.fromRGB(255, 0, 0)
                assert(c.R == 1 and c.G == 0 and c.B == 0)
                assert(Color3.new(0.5, 0.25, 1).B == 1)
                assert(Color3.fromHex('#FF0000') == Color3.fromRGB(255, 0, 0))
                local h, s, v = Color3.fromRGB(255, 0, 0):ToHSV()
                assert(h == 0 and s == 1 and v == 1)
                local u = UDim.new(0.5, 10) + UDim.new(0.25, 5)
                assert(u.Scale == 0.75 and u.Offset == 15)
                local u2 = UDim2.fromScale(1, 0.5)
                assert(u2.X.Scale == 1 and u2.Y.Scale == 0.5 and u2.X.Offset == 0)
                assert(UDim2.fromOffset(200, 100).Y.Offset == 100)
                assert(UDim2.new(1, 0, 0.5, 20) == UDim2.new(UDim.new(1, 0), UDim.new(0.5, 20)))
                local v = Vector2.new(3, 4)
                assert(v.Magnitude == 5)
                assert((v + Vector2.one).X == 4)
                assert(tostring(Vector2.new(1, 2)) == '1, 2')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Enum_AccessIdentityAndGetEnumItems()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                assert(Enum.Material.Wood.Value == 512)
                assert(Enum.Material.Wood.Name == 'Wood')
                assert(tostring(Enum.PartType.Ball) == 'Enum.PartType.Ball')
                assert(Enum.Material.Wood == Enum.Material.Wood)
                assert(Enum.Material.Wood ~= Enum.Material.Metal)
                assert(Enum.Material.Wood.EnumType == Enum.Material)
                assert(tostring(Enum) == 'Enum')
                local items = Enum.Axis:GetEnumItems()
                assert(#items == 3 and items[1] == Enum.Axis.X)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Enum_UnknownEnum_RaisesLoudStub()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            // WHY: KeyCode shipped with the MVP1 input slice; EasingStyle landed with
            // TweenService (MVP8 slice 8.4), RaycastFilterType with Raycast (8.5) and
            // HumanoidStateType with Humanoid (8.6), so SurfaceType — the vocabulary of the legacy
            // surface members, which are explicitly not scheduled — is the loud-stub probe now.
            Exception ex = LoadFails(stack, "m", "local k = Enum.SurfaceType");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("Enum.SurfaceType", FullText(ex));
        }

        [Test]
        public void Lua_Enum_UnknownItem_RaisesBadArgument()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local k = Enum.Material.Bogus");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("'Bogus' is not a valid member of Enum.Material", FullText(ex));
        }

        [Test]
        public void Lua_Random_DeterministicFromSeed()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local a = Random.new(42)
                local b = Random.new(42)
                for _ = 1, 8 do
                    assert(a:NextNumber() == b:NextNumber())
                end
                local c = Random.new(7)
                for _ = 1, 32 do
                    local n = c:NextInteger(1, 6)
                    assert(n >= 1 and n <= 6)
                end
                local d = Random.new(5)
                local clone = d:Clone()
                assert(d:NextNumber() == clone:NextNumber())
                assert(Random.new(1):NextUnitVector():FuzzyEq(Random.new(1):NextUnitVector()))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        /// <summary>Lua helper that stores every value it is given as one comma-joined string.</summary>
        private const string PutNumbersLua = @"
                local function put(key, ...)
                    local count = select('#', ...)
                    local values = { ... }
                    local parts = {}
                    for index = 1, count do
                        parts[index] = tostring(values[index])
                    end
                    store_set(key, table.concat(parts, ','))
                end
";

        private static double[] StoredNumbers(MemoryStore store, string modId, string key)
        {
            string text = store.Get(modId, key);
            Assert.IsNotEmpty(text, "mod " + modId + " stored nothing under '" + key + "'");
            string[] parts = text.Split(',');
            double[] values = new double[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                values[index] = double.Parse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            return values;
        }

        private static void AssertNumbers(double[] expected, double[] actual, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + ": value count");
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(expected[index], actual[index], 1e-5, what + " value " + (index + 1));
            }
        }

        [Test]
        public void Lua_CFrame_ToOrientation_ToEulerAngles_ToAxisAngle_AngleBetween_AreReachable()
        {
            const string modId = "cframe-members";
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod(modId, PutNumbersLua + @"
                put('orientation', CFrame.Angles(0.3, 0.4, 0):ToOrientation())
                put('xyz', CFrame.fromOrientation(0.3, 0.4, 0):ToEulerAnglesXYZ())
                put('yxz', CFrame.fromOrientation(0.3, 0.4, 0):ToEulerAnglesYXZ())
                put('zyx', CFrame.Angles(0.3, 0.4, 0):ToEulerAngles(Enum.RotationOrder.ZYX))
                put('default', CFrame.Angles(0.1, 0.2, 0.3):ToEulerAngles())
                local _, yaw = CFrame.lookAt(Vector3.zero, Vector3.new(-5, 0, 0)):ToOrientation()
                put('yaw', yaw)
                local axis, angle = CFrame.fromAxisAngle(Vector3.new(0, 0, 2), 0.5):ToAxisAngle()
                put('axisAngle', axis.X, axis.Y, axis.Z, angle)
                put('between', CFrame.Angles(0, 0.25, 0):AngleBetween(
                    CFrame.Angles(0, 1, 0) + Vector3.new(5, 0, 0)))
                local carried = CFrame.fromRotationBetweenVectors(Vector3.new(0, 1, 0), Vector3.new(1, 0, 0))
                    * Vector3.new(0, 1, 0)
                put('carried', carried.X, carried.Y, carried.Z)
                local cf = CFrame.new(1, 2, 3) * CFrame.Angles(0, 0.5, 0)
                put('components', cf:components())
                put('getComponents', cf:GetComponents())");

            const double a = 0.3;
            const double b = 0.4;
            // WHY these closed forms: CFrame.Angles(a, b, 0) = Rx(a) * Ry(b)
            // = [[cb, 0, sb], [sa sb, ca, -sa cb], [-ca sb, sa, ca cb]] and ToOrientation reads YXZ
            // (rx = asin(-R12), ry = atan2(R02, R22), rz = atan2(R10, R11)); fromOrientation(a, b, 0)
            // = Ry(b) * Rx(a) = [[cb, sb sa, sb ca], [0, ca, -sa], [-sb, cb sa, cb ca]] read in XYZ
            // (ry = asin(R02), rx = atan2(-R12, R22), rz = atan2(-R01, R00)); the same Rx(a) * Ry(b)
            // read in ZYX gives ry = asin(-R20), rz = atan2(R10, R00), rx = atan2(R21, R22).
            AssertNumbers(new[]
            {
                Math.Asin(Math.Sin(a) * Math.Cos(b)),
                Math.Atan2(Math.Sin(b), Math.Cos(a) * Math.Cos(b)),
                Math.Atan2(Math.Sin(a) * Math.Sin(b), Math.Cos(a))
            }, StoredNumbers(store, modId, "orientation"), "Angles(0.3, 0.4, 0):ToOrientation()");
            AssertNumbers(new[]
            {
                Math.Atan2(Math.Sin(a), Math.Cos(b) * Math.Cos(a)),
                Math.Asin(Math.Sin(b) * Math.Cos(a)),
                Math.Atan2(-Math.Sin(b) * Math.Sin(a), Math.Cos(b))
            }, StoredNumbers(store, modId, "xyz"), "fromOrientation(0.3, 0.4, 0):ToEulerAnglesXYZ()");
            AssertNumbers(new[] { a, b, 0d }, StoredNumbers(store, modId, "yxz"),
                "fromOrientation(0.3, 0.4, 0):ToEulerAnglesYXZ()");
            AssertNumbers(new[]
            {
                Math.Atan2(Math.Sin(a), Math.Cos(a) * Math.Cos(b)),
                Math.Asin(Math.Cos(a) * Math.Sin(b)),
                Math.Atan2(Math.Sin(a) * Math.Sin(b), Math.Cos(b))
            }, StoredNumbers(store, modId, "zyx"), "Angles(0.3, 0.4, 0):ToEulerAngles(ZYX)");
            AssertNumbers(new[] { 0.1, 0.2, 0.3 }, StoredNumbers(store, modId, "default"),
                "ToEulerAngles() defaults to XYZ, the order CFrame.Angles builds");
            AssertNumbers(new[] { Math.PI / 2d }, StoredNumbers(store, modId, "yaw"),
                "positive yaw turns left (D1), so looking down -X is yaw +90 degrees");
            AssertNumbers(new[] { 0d, 0d, 1d, 0.5 }, StoredNumbers(store, modId, "axisAngle"),
                "ToAxisAngle returns the unit axis and the angle");
            AssertNumbers(new[] { 0.75 }, StoredNumbers(store, modId, "between"),
                "AngleBetween compares orientation only");
            AssertNumbers(new[] { 1d, 0d, 0d }, StoredNumbers(store, modId, "carried"),
                "the mirror's own fromRotationBetweenVectors example");
            double[] components = StoredNumbers(store, modId, "components");
            Assert.AreEqual(12, components.Length);
            Assert.AreEqual(StoredNumbers(store, modId, "getComponents"), components,
                "components is the mirror's alias of GetComponents");
        }

        [Test]
        public void Lua_Vector2Angle_Color3ToHSV_EnumFromNameFromValue_AreReachable()
        {
            const string modId = "misc-members";
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod(modId, PutNumbersLua + @"
                put('angles', Vector2.new(1, 0):Angle(Vector2.new(0, 1)),
                    Vector2.new(0, 1):Angle(Vector2.new(1, 0), true),
                    Vector2.new(0, 1):Angle(Vector2.new(1, 0)),
                    Vector2.new(1, 0):Angle(Vector2.new(-1, 1)))
                put('hsv', Color3.toHSV(Color3.fromRGB(0, 255, 0)))
                put('hsvBlue', Color3.toHSV(Color3.fromRGB(0, 0, 255)))
                assert(Enum.Material:FromName('Wood') == Enum.Material.Wood)
                assert(Enum.Material:FromName('NoSuchMaterial') == nil)
                assert(Enum.KeyCode:FromValue(97) == Enum.KeyCode.A)
                assert(Enum.KeyCode:FromValue(99999) == nil)
                assert(Enum.KeyCode:FromValue(97.5) == nil)
                assert(Enum.KeyCode:FromValue(0) == Enum.KeyCode.None)
                assert(rawequal(Enum.KeyCode.Unknown, Enum.KeyCode.None))
                store_set('unknownAlias', tostring(Enum.KeyCode.Unknown))
                local ok, err = pcall(function() return Enum.Material:FromName(5) end)
                store_set('fromNameError', tostring(ok) .. '|' .. tostring(err))");

            AssertNumbers(new[] { Math.PI / 2d, -Math.PI / 2d, Math.PI / 2d, 3d * Math.PI / 4d },
                StoredNumbers(store, modId, "angles"),
                "Vector2:Angle is unsigned by default and negative clockwise when signed");
            AssertNumbers(new[] { 1d / 3d, 1d, 1d }, StoredNumbers(store, modId, "hsv"),
                "the mirror's Color3 example: green is 0.3333333 1 1");
            AssertNumbers(new[] { 2d / 3d, 1d, 1d }, StoredNumbers(store, modId, "hsvBlue"),
                "Color3.toHSV(blue)");
            Assert.AreEqual("Enum.KeyCode.None", store.Get(modId, "unknownAlias"));
            string fromNameError = store.Get(modId, "fromNameError");
            StringAssert.StartsWith("false|", fromNameError);
            StringAssert.Contains("Enum:FromName expects a string at argument 1", fromNameError);
        }

        [Test]
        public void Lua_SignalConnectParallel_RunsLikeConnect_AndNotesDev5OncePerMod()
        {
            const string modId = "parallel-connect";
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod(modId, @"
                local count = 0
                workspace.ChildAdded:ConnectParallel(function(child)
                    count = count + 1
                    store_set('fired', child.Name .. ':' .. count)
                end)
                local second = workspace.ChildAdded:ConnectParallel(function() end)
                store_set('connected', tostring(second.Connected))
                local marker = Instance.new('Folder')
                marker.Name = 'ParallelChild'
                marker.Parent = workspace");
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("ParallelChild:1", store.Get(modId, "fired"),
                "DEV-5: ConnectParallel must run the handler exactly like Connect");
            Assert.AreEqual("true", store.Get(modId, "connected"));
            Assert.AreEqual(2, roblox.Connections.GetOwnedBy(modId).Count,
                "parallel connections are tracked for teardown like any other");
            int notes = 0;
            foreach (string line in log)
            {
                if (line.Contains("ConnectParallel"))
                {
                    notes++;
                }
            }

            Assert.AreEqual(1, notes, "the DEV-5 note is logged once per mod, not once per call");
        }

        [Test]
        public void Lua_BadArgument_MethodPositionsExcludeSelf()
        {
            const string modId = "argument-positions";
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod(modId, @"
                local function capture(key, call)
                    local ok, err = pcall(call)
                    store_set(key, tostring(ok) .. '|' .. tostring(err))
                end
                capture('dot', function() return Vector3.new(1, 2, 3):Dot('x') end)
                capture('lerp', function() return Vector3.new():Lerp(Vector3.new(), 'a') end)
                capture('toWorld', function() return CFrame.new():ToWorldSpace(5) end)
                capture('colorLerp', function() return Color3.new():Lerp(Color3.new(), {}) end)
                capture('v2dot', function() return Vector2.new():Dot(5) end)
                capture('nextInteger', function() return Random.new(1):NextInteger(1, 'x') end)
                capture('lookAt', function() return CFrame.lookAt(Vector3.new(), 5) end)");

            (string Key, string Expected)[] cases =
            {
                ("dot", "Vector3:Dot expects a Vector3 at argument 1"),
                ("lerp", "Vector3:Lerp expects a number at argument 2"),
                ("toWorld", "CFrame:ToWorldSpace expects a CFrame at argument 1"),
                ("colorLerp", "Color3:Lerp expects a number at argument 2"),
                ("v2dot", "Vector2:Dot expects a Vector2 at argument 1"),
                ("nextInteger", "Random:NextInteger expects a number at argument 2"),
                ("lookAt", "CFrame.lookAt expects a Vector3 at argument 2")
            };
            foreach ((string key, string expected) in cases)
            {
                string failure = store.Get(modId, key);
                StringAssert.StartsWith("false|", failure, key);
                StringAssert.Contains(expected, failure,
                    key + ": Roblox numbers method arguments without self; static functions count from 1");
            }

            StringAssert.DoesNotContain("at argument 2", store.Get(modId, "dot"),
                "the old reader counted self and blamed argument 2 for v:Dot('x')");
        }

        [Test]
        public void PropertyAssignmentError_NamesThePropertyInsteadOfAPosition()
        {
            LuaValue number = 5d;
            RbxError error = LuaCsRbxLua.PropertyAssignmentError("Part", "Name", "a string", number);

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            Assert.AreEqual("Part.Name expects a string, got number", error.RawMessage);
            Assert.AreEqual("assign a string to Part.Name", error.Fix);
            StringAssert.DoesNotContain("argument", error.Message,
                "a property write has no argument list to point into");

            LuaValue falseText = "false";
            RbxError anchored = Assert.Throws<RbxError>(
                () => LuaCsRbxLua.ReadAssignedBoolean(falseText, "Part", "Anchored"));
            Assert.AreEqual("Part.Anchored expects a boolean, got string", anchored.RawMessage);
            Assert.IsTrue(LuaCsRbxLua.ReadAssignedBoolean(true, "Part", "Anchored"));
            Assert.AreEqual("Crate", LuaCsRbxLua.ReadAssignedString("Crate", "Part", "Name"));
            LuaValue numericText = "7";
            Assert.AreEqual(7d, LuaCsRbxLua.ReadAssignedNumber(numericText, "Part", "Transparency"));
            Assert.Throws<RbxError>(() => LuaCsRbxLua.ReadAssignedString(number, "Part", "Name"));
        }

        [Test]
        public void Lua_DatatypeConstructor_NonNumberArgument_RaisesBadArgument_AndNumericStringsCoerce()
        {
            const string modId = "constructor-coercion";
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            Exception ex = LoadFails(stack, "constructor-mistake", @"
                local pos = Vector3.new(1, 2, 3)
                local copy = Vector3.new(pos.X, pos.Y, pos)");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("Vector3.new expects a number at argument 3", FullText(ex),
                "a Vector3 passed where Z belongs used to become a silent 0");
            StringAssert.Contains("got Vector3", FullText(ex));

            stack.Runtime.LoadMod(modId, @"
                local v = Vector3.new('5', ' 2 ', nil)
                store_set('vector', tostring(v))
                store_set('color', tostring(Color3.new('0.5', 0, 1).R))
                local ok, err = pcall(function() return Color3.fromRGB(255, true, 0) end)
                store_set('boolean', tostring(ok) .. '|' .. tostring(err))");

            Assert.AreEqual("5, 2, 0", store.Get(modId, "vector"),
                "numeric strings coerce like tonumber and nil keeps the default, as in Luau");
            Assert.AreEqual("0.5", store.Get(modId, "color"));
            string boolean = store.Get(modId, "boolean");
            StringAssert.StartsWith("false|", boolean);
            StringAssert.Contains("Color3.fromRGB expects a number at argument 2", boolean);
        }

        [Test]
        public void Lua_DatatypeBadArgument_CarriesModAndLinePrefix()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            Exception method = LoadFails(stack, "datatype-context", @"
                local v = Vector3.new(1, 2, 3)
                local d = v:Dot('x')");
            StringAssert.Contains("[mod:datatype-context script:main.lua line:3]", FullText(method),
                "datatype errors must carry the same prefix instance errors carry");
            StringAssert.Contains("Vector3:Dot expects a Vector3 at argument 1", FullText(method));

            Exception stub = LoadFails(stack, "enum-context", @"
                local first = 1
                local wood = Enum.Material.Wod");
            StringAssert.Contains("[mod:enum-context script:main.lua line:3]", FullText(stub),
                "datatype-layer stub exceptions take the prefix too");
            StringAssert.Contains("BAD_ARGUMENT: 'Wod' is not a valid member of Enum.Material.",
                FullText(stub));

            Exception multi = LoadFails(stack, "multi-context", @"
                local cf = CFrame.new()
                local rx, ry, rz = cf:ToEulerAngles('XYZ')");
            StringAssert.Contains("[mod:multi-context script:main.lua line:3]", FullText(multi),
                "multi-return host functions take the prefix as well");
        }

        [Test]
        public void Lua_UDimOffsetAndRandomBounds_OutOfRange_RaiseBadArgument()
        {
            const string modId = "integer-conversions";
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);

            stack.Runtime.LoadMod(modId, @"
                local function capture(key, call)
                    local ok, err = pcall(call)
                    store_set(key, tostring(ok) .. '|' .. tostring(err))
                end
                capture('fromOffset', function() return UDim2.fromOffset(1e10, 0) end)
                capture('nanOffset', function() return UDim.new(0, 0 / 0) end)
                capture('nextInteger', function() return Random.new(1):NextInteger(0, 1e300) end)
                store_set('truncated', tostring(UDim.new(0, 10.9).Offset) .. ','
                    .. tostring(UDim.new(0, -10.9).Offset) .. ','
                    .. tostring(UDim2.fromOffset(2147483647, -2147483648).Y.Offset))
                store_set('bound', tostring(Random.new(7):NextInteger(2.9, 2.9)))");

            string fromOffset = store.Get(modId, "fromOffset");
            StringAssert.StartsWith("false|", fromOffset,
                "casting 1e10 to int is platform-dependent (int.MinValue on x64, saturated on ARM64)");
            StringAssert.Contains("UDim2.fromOffset expects a finite offset in the 32-bit integer range at argument 1",
                fromOffset);
            string nanOffset = store.Get(modId, "nanOffset");
            StringAssert.StartsWith("false|", nanOffset);
            StringAssert.Contains("UDim.new expects a finite offset in the 32-bit integer range at argument 2",
                nanOffset);
            string nextInteger = store.Get(modId, "nextInteger");
            StringAssert.StartsWith("false|", nextInteger);
            StringAssert.Contains("Random:NextInteger expects a finite whole number", nextInteger);
            StringAssert.Contains("at argument 2", nextInteger);
            Assert.AreEqual("10,-10,-2147483648", store.Get(modId, "truncated"),
                "in-range offsets still truncate toward zero");
            Assert.AreEqual("2", store.Get(modId, "bound"),
                "the mirror truncates NextInteger bounds toward zero");
        }

        // ---- Instance surface ---------------------------------------------------------------

        [Test]
        public void Lua_R1_1_ProductionPath_ScriptGlobalIsItsExecutingRegistryInstance()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod(Actor("script-global-actor-a"), "script-global-a", @"
                assert(script ~= nil)
                assert(script.ClassName == 'Script')
                assert(script:IsA('BaseScript'))
                assert(script:IsA('LuaSourceContainer'))
                assert(script.Name == 'script-global-a')
                assert(script.Parent.ClassName == 'Folder')
                assert(script.Parent.Parent == game:GetService('ServerScriptService'))
                script:SetAttribute('Executing', true)
                local child = Instance.new('Folder', script.Parent)
                child.Name = 'AuthoredChild'", persistToStore: false);
            stack.Runtime.LoadMod(Actor("script-global-actor-b"), "script-global-b", @"
                assert(script.Name == 'script-global-b')
                assert(script:GetAttribute('Executing') == nil)", persistToStore: false);

            RbxInstance serverScripts = roblox.Game.FindFirstChildOfClass("ServerScriptService");
            RbxInstance firstContainer = serverScripts.FindFirstChild("script-global-a");
            RbxInstance secondContainer = serverScripts.FindFirstChild("script-global-b");
            RbxInstance first = firstContainer?.FindFirstChildOfClass("Script");
            RbxInstance second = secondContainer?.FindFirstChildOfClass("Script");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second);
            Assert.AreEqual(true, first.GetAttribute("Executing"));
            Assert.AreEqual(1, roblox.Registry.GetOwnedBy("script-global-a").Count,
                "runtime infrastructure must not appear as mod-authored content");
            Assert.IsTrue(roblox.Registry.TryGetRecord(first.Id, out InstanceRecord firstRecord));
            Assert.IsTrue(firstRecord.IsRuntimeInfrastructure);
            Assert.IsNull(firstRecord.OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.SharedWritable, firstRecord.AccessScope,
                "the proxy must not become a foreign ACL container for its actor");
        }

        [Test]
        public void Lua_ProductionPath_Tac015ScriptParentFixturePassesUnmodified()
        {
            string fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(), "Assets", "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "CompatibilityCorpus", "Fixtures",
                "TAC-015-script-parent-property-signal.lua");
            string source = File.ReadAllText(fixturePath);
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("TAC-015-script-parent-property-signal", source,
                persistToStore: false);
            roblox.Scheduler.Advance(0d);

            RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
            Assert.AreEqual("TAC-015-script-parent-property-signal",
                workspace.GetAttribute("TierACorpusResult"));
        }

        [Test]
        public void Lua_ProductionPath_Tac016GeneralizedIterationFixturePassesUnmodified()
        {
            string fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(), "Assets", "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "CompatibilityCorpus", "Fixtures",
                "TAC-016-generic-for-descendants.lua");
            string source = File.ReadAllText(fixturePath);
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("TAC-016-generic-for-descendants", source,
                persistToStore: false);

            RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
            Assert.AreEqual("TAC-016-generic-for-descendants",
                workspace.GetAttribute("TierACorpusResult"));
        }

        [Test]
        public void Lua_ProductionPath_GeneralizedIterationHonorsIterMetamethod()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("generalized-iteration", @"
                local values = { 3, 5, 7 }
                setmetatable(values, { __iter = function(t)
                    local i = #t + 1
                    return function()
                        i = i - 1
                        if i > 0 then return i, t[i] end
                    end
                end })
                local result = ''
                for i, value in values do
                    result = result .. i .. ':' .. value .. ';'
                end
                store_set('result', result)");

            Assert.AreEqual("3:7;2:5;1:3;",
                store.Get("generalized-iteration", "result"));
        }

        [Test]
        public void Lua_InstanceNew_CreatesParentsAndNavigates()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder')
                f.Name = 'F'
                assert(f.Parent == nil)
                f.Parent = workspace
                assert(f.Parent == workspace)
                assert(workspace:FindFirstChild('F') == f)
                assert(workspace.F == f)
                assert(f:GetFullName() == 'Workspace.F')
                assert(f.ClassName == 'Folder')
                assert(f:IsA('Folder') and f:IsA('Instance') and not f:IsA('BasePart'))
                assert(f:IsDescendantOf(workspace) and workspace:IsAncestorOf(f))
                local kids = workspace:GetChildren()
                assert(kids[#kids] == f)
                assert(tostring(f) == 'F')
                local m = Instance.new('Model')
                m.Parent = f
                assert(workspace:FindFirstChildWhichIsA('Model', true) == m)
                assert(#f:GetChildren() == 1)
                assert(game.Workspace == workspace)
                assert(game.ReplicatedStorage.ClassName == 'ReplicatedStorage')
                assert(f:WaitForChild('Model') == m)");

            Assert.IsTrue(roblox.Registry.TryGetByWorldName("missing", out _) == false);
            RbxInstance folder = roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("F");
            Assert.IsNotNull(folder);
        }

        [Test]
        public void Lua_InstanceNew_DeprecatedParentArgument_WorksAndLogsOnce()
        {
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local a = Instance.new('Folder', workspace)
                local b = Instance.new('Folder', workspace)
                assert(a.Parent == workspace and b.Parent == workspace)");

            int deprecationNotes = 0;
            foreach (string line in log)
            {
                if (line.Contains("deprecated"))
                {
                    deprecationNotes++;
                }
            }

            Assert.AreEqual(1, deprecationNotes,
                "the Instance.new(className, parent) deprecation note must fire once per mod");
        }

        [Test]
        public void Lua_InstanceNew_NonCreatableClass_RaisesRobloxErrorShape()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "Instance.new('Workspace')");
            StringAssert.Contains("Unable to create an Instance of type 'Workspace'", FullText(ex));
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
        }

        [Test]
        public void Lua_GetService_PlannedService_DefersPhaseNamingStubUntilMemberAccess()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            // WHY: top-of-file GetService calls must not block unrelated script initialization;
            // the loud failure belongs to the line that first uses the missing service surface.
            // TweenService landed in MVP8 slice 8.4, so DataStoreService (MVP9) is the
            // deferred-stub probe now.
            stack.Runtime.LoadMod("resolve-only", @"
                local DataStoreService = game:GetService('DataStoreService')
                assert(DataStoreService ~= nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("resolve-only"));

            Exception ex = LoadFails(stack, "member-access", @"
                local DataStoreService = game:GetService('DataStoreService')
                DataStoreService:GetDataStore()");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("DataStoreService:GetDataStore", FullText(ex));
            StringAssert.Contains("MVP9", FullText(ex));
        }

        [Test]
        public void Lua_PlannedStub_ProductionPathCarriesModIdAndSourceLine()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "stub-context", @"
                local missing = 'NeverThere'
                Instance.fromExisting(missing)");
            string fullText = FullText(ex);
            StringAssert.Contains("[mod:stub-context script:main.lua line:3]", fullText);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            // WHY the catalog's backlog wording: the old text read "is planned for no planned MVP
            // (backlog)", a planned-rung sentence with no rung in it.
            StringAssert.Contains("Instance.fromExisting is a known Rbx member, but no roadmap rung is assigned",
                fullText);
            StringAssert.DoesNotContain("planned for", fullText);
        }

        [Test]
        public void Lua_GetService_UnknownService_RaisesExactRobloxText()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "game:GetService('Bogus')");
            StringAssert.Contains("Bogus is not a valid Service name", FullText(ex));
            StringAssert.Contains("UNKNOWN_SERVICE", FullText(ex));
        }

        [Test]
        public void Lua_UnknownMember_RaisesValidMemberError()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local x = workspace.NoSuchChildHere");
            StringAssert.Contains(
                "NoSuchChildHere is not a valid member of Workspace \"Workspace\"", FullText(ex));
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", FullText(ex));
        }

        [TestCase("GetService")]
        [TestCase("FindService")]
        [TestCase("BindToClose")]
        public void Lua_Folder_ServiceProviderMember_RemainsInvalidMember(string member)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "local folder = Instance.new('Folder'); local value = folder." + member);
            string fullText = FullText(ex);
            StringAssert.Contains(
                member + " is not a valid member of Folder \"Folder\"", fullText);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", fullText);
        }

        [Test]
        public void Lua_ModOwnership_OriginTagAndOwnerRecorded()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("mymod", @"
                local f = Instance.new('Folder')
                f.Name = 'Owned'
                f.Parent = workspace");

            RbxInstance owned = null;
            foreach (RbxInstance candidate in roblox.Registry.GetOwnedBy("mymod"))
            {
                owned = candidate;
            }

            Assert.IsNotNull(owned, "instances created from a mod must be owner-attributed");
            Assert.AreEqual("Owned", owned.Name);
            Assert.IsTrue(roblox.Registry.TryGetRecord(owned.Id, out InstanceRecord record));
            Assert.AreEqual("mymod", record.OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("mymod"), record.OriginTag);
        }

        [Test]
        public void Lua_WorldAcl_OwnerCanMutate_ButCrossActorWritesAndDestroyAreDenied()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("actor-a");
            ActorContext actorB = Actor("actor-b");
            RbxInstance ownedA = CreateActorInstance(registry, actorA, "mod-a", "Folder");
            RbxInstance ownedB = CreateActorInstance(registry, actorB, "mod-b", "Folder");
            RbxInstance shared = registry.Create("Folder");

            Assert.AreEqual("actor-a", Record(registry, ownedA).OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, ownedA).AccessScope);
            Assert.AreEqual(InstanceAccessScope.SharedWritable, Record(registry, shared).AccessScope);

            RunActorLua(bindings, actorA, "mod-a", "own.Name = 'OwnedByA'",
                ("own", ownedA));
            Assert.AreEqual("OwnedByA", ownedA.Name);

            Exception writeError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "foreign.Name = 'stolen'", ("foreign", ownedB)));
            StringAssert.Contains("actor 'actor-a'", FullText(writeError));
            StringAssert.Contains("Owned by actor 'actor-b'", FullText(writeError));
            Assert.AreEqual("Folder", ownedB.Name);

            Exception destroyError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "foreign:Destroy()", ("foreign", ownedB)));
            StringAssert.Contains("actor 'actor-a'", FullText(destroyError));
            StringAssert.Contains("Owned by actor 'actor-b'", FullText(destroyError));
            Assert.IsFalse(ownedB.IsDestroyed);

            RunActorLua(bindings, actorA, "mod-a", "shared.Name = 'Writable'", ("shared", shared));
            Exception sharedDestroyError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "mod-a", "shared:Destroy()", ("shared", shared)));
            StringAssert.Contains("SharedWritable destruction", FullText(sharedDestroyError));
            Assert.IsFalse(shared.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_ProductionRuntimeInstanceNewAndMutationUseActorAcl()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            ActorContext actorA = Actor("runtime-actor-a");
            ActorContext actorB = Actor("runtime-actor-b");
            registry.BindActorAttribution(
                "runtime-owner-b", OriginTag.FromMod("runtime-owner-b"), actorB.ActorId);

            stack.Runtime.LoadMod(actorB, "runtime-owner-b", @"
                local owned = Instance.new('Folder')
                owned.Name = 'RuntimeOwnedByB'
                owned.Parent = workspace", persistToStore: false);

            RbxInstance ownedB = bindings.Registry.WorldRoot.FindFirstChild("RuntimeOwnedByB");
            Assert.IsNotNull(ownedB);
            Assert.AreEqual(actorB.ActorId, Record(registry, ownedB).OwnerActorId);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, ownedB).AccessScope);

            registry.BindActorAttribution(
                "runtime-write-a", OriginTag.FromMod("runtime-write-a"), actorA.ActorId);
            Exception writeError = Assert.Catch(() => stack.Runtime.LoadMod(
                actorA,
                "runtime-write-a",
                "workspace:FindFirstChild('RuntimeOwnedByB').Name = 'stolen'",
                persistToStore: false));
            StringAssert.Contains("Owned by actor 'runtime-actor-b'", FullText(writeError));

            registry.BindActorAttribution(
                "runtime-destroy-a", OriginTag.FromMod("runtime-destroy-a"), actorA.ActorId);
            Exception destroyError = Assert.Catch(() => stack.Runtime.LoadMod(
                actorA,
                "runtime-destroy-a",
                "workspace:FindFirstChild('RuntimeOwnedByB'):Destroy()",
                persistToStore: false));
            StringAssert.Contains("actor 'runtime-actor-a'", FullText(destroyError));
            Assert.IsFalse(ownedB.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_HostProtectedCameraIsWritable_ButSingletonLifecycleIsHostOnly()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actor = Actor("camera-mod-owner");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance camera = workspace.FindFirstChildOfClass("Camera");
            RbxInstance lighting = bindings.Game.FindFirstChildOfClass("Lighting");

            Assert.AreEqual(InstanceAccessScope.HostProtected, Record(registry, camera).AccessScope);
            Assert.AreEqual(InstanceAccessScope.HostProtected, Record(registry, lighting).AccessScope);
            RunActorLua(bindings, actor, "camera-mod",
                "camera.CFrame = CFrame.new(3, 4, 5)", ("camera", camera));
            Assert.AreEqual(3f, bindings.CameraRig.GetCFrame().Position.X, 0.0001f);

            Exception actorDestroyError = Assert.Catch(() => RunActorLua(
                bindings, actor, "camera-mod", "service:Destroy()", ("service", lighting)));
            StringAssert.Contains("HostProtected", FullText(actorDestroyError));
            Assert.IsFalse(lighting.IsDestroyed);

            ActorContext host = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            Exception hostDestroyError = Assert.Catch(() => RunActorLua(
                bindings, host, "host", "service:Destroy()", ("service", lighting)));
            StringAssert.Contains("including for unrestricted actors", FullText(hostDestroyError));
            Assert.IsFalse(lighting.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_CloneSubtreeBelongsToCloningActor()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("clone-source-owner");
            ActorContext actorB = Actor("clone-caller");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance source = CreateActorInstance(registry, actorA, "source-mod", "Folder");
            RbxInstance sourceChild = CreateActorInstance(registry, actorA, "source-mod", "Part");
            sourceChild.Parent = source;
            source.Parent = workspace;

            RunActorLua(bindings, actorB, "clone-mod", @"
                local copy = source:Clone()
                copy.Name = 'CloneByB'
                copy.Parent = workspace",
                ("source", source), ("workspace", workspace));

            RbxInstance clone = workspace.FindFirstChild("CloneByB");
            Assert.IsNotNull(clone);
            RbxInstance cloneChild = clone.FindFirstChildOfClass("Part");
            Assert.IsNotNull(cloneChild);
            Assert.AreEqual("clone-caller", Record(registry, clone).OwnerActorId);
            Assert.AreEqual("clone-caller", Record(registry, cloneChild).OwnerActorId);
            Assert.AreEqual("clone-mod", Record(registry, clone).OwnerModId);
            Assert.AreEqual("clone-mod", Record(registry, cloneChild).OwnerModId);
            Assert.AreEqual(OriginTag.FromMod("clone-mod"), Record(registry, clone).OriginTag);
            Assert.AreEqual(OriginTag.FromMod("clone-mod"), Record(registry, cloneChild).OriginTag);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, clone).AccessScope);
            Assert.AreEqual(InstanceAccessScope.Owned, Record(registry, cloneChild).AccessScope);
        }

        [Test]
        public void Lua_WorldAcl_ReparentChecksMovedObjectAndDestinationBeforeMutation()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("reparent-a");
            ActorContext actorB = Actor("reparent-b");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance childA = CreateActorInstance(registry, actorA, "reparent-mod-a", "Folder");
            RbxInstance parentB = CreateActorInstance(registry, actorB, "reparent-mod-b", "Folder");

            Exception sourceError = Assert.Catch(() => RunActorLua(
                bindings, actorB, "reparent-mod-b", "child.Parent = workspace",
                ("child", childA), ("workspace", workspace)));
            StringAssert.Contains("reparent source", FullText(sourceError));
            Assert.IsNull(childA.Parent);

            Exception destinationError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "reparent-mod-a", "child.Parent = destination",
                ("child", childA), ("destination", parentB)));
            StringAssert.Contains("reparent destination", FullText(destinationError));
            StringAssert.Contains("Owned by actor 'reparent-b'", FullText(destinationError));
            Assert.IsNull(childA.Parent);

            childA.Parent = parentB;
            Exception sourceContainerError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "reparent-mod-a", "child.Parent = workspace",
                ("child", childA), ("workspace", workspace)));
            StringAssert.Contains("reparent source container", FullText(sourceContainerError));
            StringAssert.Contains("Owned by actor 'reparent-b'", FullText(sourceContainerError));
            Assert.AreSame(parentB, childA.Parent);
        }

        [Test]
        public void Lua_WorldAcl_InstanceNewParentChecksForeignContainerBeforeMutation()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            LuaCsModStack stack = BuildStack(bindings);
            ActorContext actorA = Actor("instance-new-parent-a");
            ActorContext actorB = Actor("instance-new-parent-b");
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            RbxInstance containerA = CreateActorInstance(
                registry, actorA, "instance-new-container-a", "Folder");
            containerA.Name = "ForeignContainer";
            containerA.Parent = workspace;
            registry.BindActorAttribution(
                "instance-new-mod-b", OriginTag.FromMod("instance-new-mod-b"), actorB.ActorId);

            Exception error = null;
            try
            {
                stack.Runtime.LoadMod(
                    actorB,
                    "instance-new-mod-b",
                    "Instance.new('Folder', workspace:FindFirstChild('ForeignContainer'))",
                    persistToStore: false);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            Assert.AreEqual(0, containerA.GetChildren().Count);
            Assert.AreEqual(0, registry.GetOwnedBy("instance-new-mod-b").Count);
            Assert.IsNotNull(error, "foreign-container creation must be denied");
            StringAssert.Contains(
                "cannot create child on Folder 'Workspace.ForeignContainer'", FullText(error));
            StringAssert.Contains("Owned by actor 'instance-new-parent-a'", FullText(error));
        }

        [Test]
        public void Lua_WorldAcl_RecursiveDestroyAndClearPreflightEveryDescendantAtomically()
        {
            LuaCsRbxApiBindings bindings = StrictWorld(out InstanceRegistry registry);
            ActorContext actorA = Actor("destroy-a");
            ActorContext actorB = Actor("destroy-b");
            RbxInstance rootA = CreateActorInstance(registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance descendantB = CreateActorInstance(registry, actorB, "destroy-mod-b", "Folder");
            descendantB.Parent = rootA;

            Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "root:Destroy()", ("root", rootA)));
            Assert.IsFalse(rootA.IsDestroyed);
            Assert.IsFalse(descendantB.IsDestroyed);
            Assert.AreSame(rootA, descendantB.Parent);

            RbxInstance containerA = CreateActorInstance(
                registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance childA = CreateActorInstance(registry, actorA, "destroy-mod-a", "Folder");
            RbxInstance childB = CreateActorInstance(registry, actorB, "destroy-mod-b", "Folder");
            childA.Parent = containerA;
            childB.Parent = containerA;

            Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "container:ClearAllChildren()",
                ("container", containerA)));
            Assert.AreSame(containerA, childA.Parent);
            Assert.AreSame(containerA, childB.Parent);
            Assert.IsFalse(childA.IsDestroyed);
            Assert.IsFalse(childB.IsDestroyed);

            RbxInstance containerB = CreateActorInstance(
                registry, actorB, "destroy-mod-b", "Folder");
            RbxInstance callerOwnedChild = CreateActorInstance(
                registry, actorA, "destroy-mod-a", "Folder");
            callerOwnedChild.Parent = containerB;
            Exception containerError = Assert.Catch(() => RunActorLua(
                bindings, actorA, "destroy-mod-a", "container:ClearAllChildren()",
                ("container", containerB)));
            StringAssert.Contains("clear descendants container", FullText(containerError));
            StringAssert.Contains("Owned by actor 'destroy-b'", FullText(containerError));
            Assert.AreSame(containerB, callerOwnedChild.Parent);
            Assert.IsFalse(callerOwnedChild.IsDestroyed);
        }

        [Test]
        public void Lua_WorldAcl_LegacyWorldKeepsExistingCrossActorDestroyBehavior()
        {
            InstanceRegistry registry = new();
            LuaCsRbxApiBindings bindings = new(registry: registry);
            ActorContext actorA = Actor("legacy-a");
            ActorContext actorB = Actor("legacy-b");
            RbxInstance ownedB = CreateActorInstance(registry, actorB, "legacy-mod-b", "Folder");

            Assert.IsNull(registry.WorldAclVersion);
            RunActorLua(bindings, actorA, "legacy-mod-a",
                "target.Name = 'LegacyWritable'; target:Destroy()", ("target", ownedB));
            Assert.IsTrue(ownedB.IsDestroyed);
        }

        [Test]
        public void Lua_OneOffExecutor_GetsConsoleOrigin()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("local f = Instance.new('Folder', workspace) f.Name = 'FromConsole'",
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            RbxInstance created = roblox.Game.FindFirstChildOfClass("Workspace")
                .FindFirstChild("FromConsole");
            Assert.IsNotNull(created);
            Assert.IsTrue(roblox.Registry.TryGetRecord(created.Id, out InstanceRecord record));
            Assert.IsNull(record.OwnerModId, "console instances are world-owned (no teardown owner)");
            StringAssert.StartsWith(OriginTag.ConsolePrefix, record.OriginTag);
        }

        [Test]
        public void Lua_CapabilityGating_ReadTierInstanceNewNamesWorldEditAndCannotMutate()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCapabilities readOnly =
                LuaCapabilities.Read | LuaCapabilities.Gameplay | LuaCapabilities.LogicOverride;
            LuaCsModStack stack = BuildStack(roblox, caps: readOnly);
            RbxInstance workspace = roblox.Game.FindFirstChildOfClass("Workspace");
            int workspaceChildren = workspace.GetChildren().Count;
            int detachedFolders = CountDetachedFolders(roblox);

            // WHY Instance is present on the Read tier (M1-34): an absent global failed with
            // "attempt to index a nil value", which names neither the capability nor the fix.
            stack.Runtime.LoadMod("reader", @"
                assert(Instance ~= nil, 'Instance is registered on every tier')
                local ok, err = pcall(Instance.new, 'Folder')
                assert(not ok, 'Instance.new must be refused without WorldEdit')
                assert(string.find(tostring(err),
                    'Instance.new requires the WorldEdit capability', 1, true), tostring(err))
                local okParented, errParented = pcall(Instance.new, 'Folder', workspace)
                assert(not okParented, 'the parent overload is refused too')
                assert(string.find(tostring(errParented), 'WorldEdit', 1, true), tostring(errParented))
                assert(workspace.ClassName == 'Workspace', 'navigation stays available on Read tier')");
            Assert.IsTrue(stack.Runtime.IsLoaded("reader"));
            Assert.AreEqual(workspaceChildren, workspace.GetChildren().Count,
                "a refused Instance.new parents nothing");
            Assert.AreEqual(detachedFolders, CountDetachedFolders(roblox),
                "a refused Instance.new leaves no detached instance behind");

            Exception ex = LoadFails(stack, "writer", "workspace.Name = 'Hacked'");
            StringAssert.Contains("WorldEdit", FullText(ex));
            Assert.AreEqual("Workspace", roblox.Game.FindFirstChildOfClass("Workspace").Name);
        }

        private static int CountDetachedFolders(LuaCsRbxApiBindings roblox)
        {
            int count = 0;
            foreach (RbxInstance live in roblox.Registry.GetLiveInstances())
            {
                if (live.ClassName == "Folder" && live.Parent == null)
                {
                    count++;
                }
            }

            return count;
        }

        [Test]
        public void Lua_InstanceNew_WithoutWorldEdit_RaisesCapabilityError()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox, caps: LuaCapabilities.Read);

            Exception ex = LoadFails(stack, "reader", "local part = Instance.new('Part')");

            string fullText = FullText(ex);
            StringAssert.Contains("BAD_ARGUMENT", fullText);
            StringAssert.Contains(
                "Instance.new requires the WorldEdit capability, which was not granted to this script",
                fullText);
            StringAssert.DoesNotContain("attempt to index a nil value", fullText);
        }

        [Test]
        public void Lua_R6_7_R6_8_AttributesAndTags_RoundTrip()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:SetAttribute('Health', 100)
                f:SetAttribute('Label', 'boss')
                f:SetAttribute('Alive', true)
                assert(f:GetAttribute('Health') == 100)
                assert(f:GetAttribute('Label') == 'boss')
                assert(f:GetAttribute('Alive') == true)
                assert(f:GetAttribute('Missing') == nil)
                local attrs = f:GetAttributes()
                assert(attrs.Health == 100 and attrs.Label == 'boss')
                f:SetAttribute('Health', nil)
                assert(f:GetAttribute('Health') == nil)
                f:AddTag('Enemy')
                assert(f:HasTag('Enemy'))
                assert(not f:HasTag('Friend'))
                assert(f:GetTags()[1] == 'Enemy')
                f:RemoveTag('Enemy')
                assert(not f:HasTag('Enemy'))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_AttributeTable_RejectedWithBadArgument()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "Instance.new('Folder'):SetAttribute('Data', { x = 1 })");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("table", FullText(ex));
        }

        [Test]
        public void Lua_R6_2_DestroyedInstance_MemberAccessAndReparentRaiseContractErrors()
        {
            // WHY: the mod's own store is the read-back channel, matching the runtime harness style.
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), store);
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:Destroy()
                local ok, err = pcall(function() return f.Name end)
                store_set('nameErr', tostring(err))
                local ok2, err2 = pcall(function() f.Parent = workspace end)
                store_set('parentErr', tostring(err2))");
            StringAssert.Contains("INSTANCE_DESTROYED", store.Get("m", "nameErr"));
            StringAssert.Contains("PARENT_LOCKED", store.Get("m", "parentErr"));
            StringAssert.Contains("The Parent property of Folder is locked",
                store.Get("m", "parentErr"));
        }

        [Test]
        public void Lua_R6_5_Clone_DeepCopiesWithFreshIdentity()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local src = Instance.new('Folder', workspace)
                src.Name = 'Src'
                src:SetAttribute('Level', 3)
                local child = Instance.new('Model')
                child.Name = 'Child'
                child.Parent = src
                local copy = src:Clone()
                assert(copy ~= src)
                assert(copy.Parent == nil)
                assert(copy.Name == 'Src')
                assert(copy:GetAttribute('Level') == 3)
                assert(copy:FindFirstChild('Child') ~= nil)
                assert(copy:FindFirstChild('Child') ~= src:FindFirstChild('Child'))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        // ---- Loud stubs (§5.1.6) ------------------------------------------------------------

        [Test]
        public void Lua_BasePartSpatialWrites_ReflectInPartProperties()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Name = 'Spatial'
                p.Parent = workspace
                p.Position = Vector3.new(1, 2, 3)
                p.Size = Vector3.new(5, 6, 7)
                p.Color = Color3.fromRGB(255, 128, 0)
                p.Transparency = 0.25
                p.Anchored = true
                p.CanCollide = false");

            RbxInstance part = roblox.Game.FindFirstChildOfClass("Workspace").FindFirstChild("Spatial");
            Assert.IsNotNull(part);
            PartProperties props = roblox.PartSink.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual(new RbxVector3(1f, 2f, 3f), props.Position);
            Assert.AreEqual(new RbxVector3(5f, 6f, 7f), props.Size);
            Assert.AreEqual(RbxColor3.FromRGB(255f, 128f, 0f), props.Color);
            Assert.AreEqual(0.25f, props.Transparency, 1e-5f);
            Assert.IsTrue(props.Anchored);
            Assert.IsFalse(props.CanCollide);
        }

        [Test]
        public void Lua_BasePartCFrame_SetsBoth_Position_SetKeepsOrientation()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Name = 'Oriented'
                p.Parent = workspace
                p.CFrame = CFrame.new(0, 5, 0) * CFrame.Angles(0, math.pi / 2, 0)
                assert(p.CFrame.Position == Vector3.new(0, 5, 0))
                assert(near(p.CFrame.LookVector.X, -1))
                -- setting Position preserves rotation (Roblox Part semantics)
                p.Position = Vector3.new(9, 9, 9)
                assert(p.Position == Vector3.new(9, 9, 9))
                assert(near(p.CFrame.LookVector.X, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_RoundTripsDegreesYxz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-3 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(20, 30, 40)
                local value = p.Orientation
                assert(near(value.X, 20) and near(value.Y, 30) and near(value.Z, 40))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_SetMatchesCFrameFromOrientationYxz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(20, 30, 40)
                local actual = { p.CFrame:GetComponents() }
                local expected = {
                    CFrame.fromOrientation(math.rad(20), math.rad(30), math.rad(40)):GetComponents()
                }
                for i = 1, 12 do
                    assert(near(actual[i], expected[i]))
                end");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_SetPreservesPosition()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Position = Vector3.new(7, 8, 9)
                p.Orientation = Vector3.new(15, 25, 35)
                assert(p.Position == Vector3.new(7, 8, 9))
                assert(p.CFrame.Position == Vector3.new(7, 8, 9))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartOrientation_Yaw90_MatchesDocumentedAxes()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                p.Orientation = Vector3.new(0, 90, 0)
                local look = p.CFrame.LookVector
                local right = p.CFrame.RightVector
                assert(near(look.X, -1) and near(look.Y, 0) and near(look.Z, 0))
                assert(near(right.X, 0) and near(right.Y, 0) and near(right.Z, -1))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_RoundTripsDegreesXyz_AndMatchesOrientationOnSingleAxis()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-3 end
                local p = Instance.new('Part')
                p.Rotation = Vector3.new(10, 20, 30)
                local value = p.Rotation
                assert(near(value.X, 10) and near(value.Y, 20) and near(value.Z, 30))
                p.Orientation = Vector3.new(30, 0, 0)
                local orientationCFrame = p.CFrame
                p.Rotation = Vector3.new(30, 0, 0)
                assert(p.CFrame == orientationCFrame)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_SetPreservesPosition()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Position = Vector3.new(7, 8, 9)
                p.Rotation = Vector3.new(15, 25, 35)
                assert(p.Position == Vector3.new(7, 8, 9))
                assert(p.CFrame.Position == Vector3.new(7, 8, 9))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartRotation_MultiAxisDiffersFromOrientation_AndMatchesCFrameAnglesXyz()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-4 end
                local p = Instance.new('Part')
                local rotation = Vector3.new(20, 30, 40)
                p.Rotation = rotation
                local orientation = p.Orientation
                assert(not (
                    near(orientation.X, rotation.X)
                    and near(orientation.Y, rotation.Y)
                    and near(orientation.Z, rotation.Z)))
                local actual = { p.CFrame:GetComponents() }
                local expected = {
                    CFrame.Angles(math.rad(20), math.rad(30), math.rad(40)):GetComponents()
                }
                for i = 1, 12 do
                    assert(near(actual[i], expected[i]))
                end");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartPreset_SetInCSharp_ReadableFromLua()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            RbxInstance part = roblox.Registry.Create("Part");
            part.Name = "Preset";
            part.Parent = roblox.Registry.WorldRoot;
            roblox.PartSink.SetSize(part.Id, new RbxVector3(8f, 9f, 10f));
            roblox.PartSink.SetAnchored(part.Id, true);

            stack.Runtime.LoadMod("m", @"
                local p = workspace:FindFirstChild('Preset')
                assert(p.Size == Vector3.new(8, 9, 10))
                assert(p.Anchored == true)
                -- an untouched fresh Part reads Roblox defaults
                local q = Instance.new('Part')
                assert(q.Size == Vector3.new(4, 1, 2))
                assert(q.Transparency == 0)
                assert(q.CanCollide == true)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_BasePartMaterial_SetAndReadBackEnumMaterialThroughProductionPath()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Material = Enum.Material.Wood
                assert(p.Material == Enum.Material.Wood)
                assert(p.Material.Name == 'Wood')
                assert(p.Material.Value == 512)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        // WHY these two and not workspace.Gravity/Raycast any more: both shipped in MVP8 slice 8.5,
        // and a probe that asserts a shipped member still fails is a test that fails on success.
        // Named render-step binding is the nearest surface that is still only planned and needs no
        // world setup to reach.
        [TestCase(
            "local value = game:GetService('RunService').BindToRenderStep",
            "RunService:BindToRenderStep", "MVP2")]
        [TestCase(
            "local value = game:GetService('RunService').UnbindFromRenderStep",
            "RunService:UnbindFromRenderStep", "MVP2")]
        public void Lua_PlannedUnimplementedMember_RaisesExactPhaseNamingStub(
            string code, string feature, string phase)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", code);
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(feature + " is planned for " + phase + ".", fullText);
            StringAssert.Contains("| fix: ", fullText);
        }

        [TestCase(
            "local part = Instance.new('Part'); local value = part.AssemblyLinearVelocity",
            "BasePart.AssemblyLinearVelocity")]
        [TestCase(
            "local part = Instance.new('Part'); part.Massless = true",
            "BasePart.Massless")]
        [TestCase(
            "local value = game:GetService('Lighting').ClockTime",
            "Lighting.ClockTime")]
        public void Lua_BacklogMember_RaisesUnassignedRungStatus(string code, string feature)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", code);
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                feature + " is a known Rbx member, but no roadmap rung is assigned.", fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [Test]
        public void Lua_UnsupportedTerrain_RaisesDistinctUnsupportedStatus()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "local value = workspace.Terrain");
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                "Workspace.Terrain is a known Rbx member deliberately unsupported by CoreAI.",
                fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [Test]
        public void Lua_WorkspaceSignalBehavior_ReadsDeferred()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                assert(workspace.SignalBehavior == Enum.SignalBehavior.Deferred)
                assert(tostring(workspace.SignalBehavior) == 'Enum.SignalBehavior.Deferred')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_WorkspaceSignalBehavior_WriteIsUnsupported()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(
                stack, "m", "workspace.SignalBehavior = Enum.SignalBehavior.Immediate");
            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                "Workspace.SignalBehavior is a known Rbx member deliberately unsupported by CoreAI.",
                fullText);
            StringAssert.Contains("signal mode is Deferred-only", fullText);
            StringAssert.DoesNotContain("is planned for", fullText);
        }

        [TestCase("PivotTo")]
        [TestCase("GetPivot")]
        public void Lua_Folder_PvInstanceMember_RemainsInvalidMember(string member)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "local folder = Instance.new('Folder'); local value = folder." + member);
            string fullText = FullText(ex);
            StringAssert.Contains(
                member + " is not a valid member of Folder \"Folder\"", fullText);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", fullText);
        }

        [Test]
        public void Lua_ModelPivotTo_PreservesEveryDescendantPvInstanceOffset()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function nearCFrame(a, b)
                    local ac = { a:GetComponents() }
                    local bc = { b:GetComponents() }
                    for i = 1, 12 do
                        if math.abs(ac[i] - bc[i]) >= 1e-4 then
                            return false
                        end
                    end
                    return true
                end

                local model = Instance.new('Model')
                model.Parent = workspace
                local direct = Instance.new('Part')
                direct.CFrame = CFrame.new(-4, 1, 3) * CFrame.Angles(0.1, 0.2, 0.3)
                direct.Parent = model
                local folder = Instance.new('Folder')
                folder.Parent = model
                local throughFolder = Instance.new('Part')
                throughFolder.CFrame = CFrame.new(5, -2, 7) * CFrame.Angles(-0.2, 0.4, 0.1)
                throughFolder.Parent = folder
                local nested = Instance.new('Model')
                nested.WorldPivot = CFrame.new(8, 3, -6) * CFrame.Angles(0.3, -0.1, 0.2)
                nested.Parent = folder
                local deep = Instance.new('Part')
                deep.CFrame = CFrame.new(11, 4, -9) * CFrame.Angles(0.5, 0.25, -0.15)
                deep.Parent = nested
                model.WorldPivot = CFrame.new(1, 2, 3) * CFrame.Angles(0.2, -0.3, 0.4)

                local oldPivot = model:GetPivot()
                local directOffset = oldPivot:ToObjectSpace(direct.CFrame)
                local folderOffset = oldPivot:ToObjectSpace(throughFolder.CFrame)
                local deepOffset = oldPivot:ToObjectSpace(deep.CFrame)
                local nestedOffset = oldPivot:ToObjectSpace(nested:GetPivot())
                local target = CFrame.new(30, -7, 12) * CFrame.Angles(-0.4, 0.6, 0.25)
                model:PivotTo(target)

                assert(nearCFrame(model:GetPivot(), target), 'model pivot missed target')
                assert(nearCFrame(target:ToObjectSpace(direct.CFrame), directOffset),
                    'direct descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(throughFolder.CFrame), folderOffset),
                    'folder-nested descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(deep.CFrame), deepOffset),
                    'nested-model descendant offset changed')
                assert(nearCFrame(target:ToObjectSpace(nested:GetPivot()), nestedOffset),
                    'descendant Model pivot offset changed')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_ModelGetPivot_UsesBoundingBoxWithoutPrimaryPart_AndPrimaryPartCFrameWhenSet()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local model = Instance.new('Model')
                local left = Instance.new('Part')
                left.Size = Vector3.new(2, 2, 2)
                left.CFrame = CFrame.new(0, 0, 0)
                left.Parent = model
                local right = Instance.new('Part')
                right.Size = Vector3.new(4, 2, 2)
                right.CFrame = CFrame.new(10, 0, 0)
                right.Parent = model

                assert(model.PrimaryPart == nil)
                assert(model:GetPivot().Position == Vector3.new(5.5, 0, 0),
                    'fresh Model pivot must be its world-axis bounding-box center')
                model.PrimaryPart = right
                assert(model.PrimaryPart == right)
                assert(model:GetPivot() == right.CFrame)
                right.CFrame = CFrame.new(14, 3, -2) * CFrame.Angles(0.2, 0.4, 0.6)
                assert(model:GetPivot() == right.CFrame,
                    'PrimaryPart-driven pivot must follow the part')
                model.PrimaryPart = nil
                assert(model.PrimaryPart == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_ModelPrimaryPart_NonDescendantSurvivesAssignmentThenClearsAtSimulationStep()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("create", @"
                local model = Instance.new('Model')
                model.Name = 'PivotModel'
                model.Parent = workspace
                local child = Instance.new('Part')
                child.Name = 'Child'
                child.Parent = model
                local external = Instance.new('Part')
                external.Name = 'ExternalPrimary'
                external.Parent = workspace

                assert(model.PrimaryPart == nil)
                model.PrimaryPart = external
                assert(model.PrimaryPart == external,
                    'legacy assignment must remain visible until simulation')");
            roblox.PumpPreSimulation(0.016f);
            stack.Runtime.LoadMod("verify", @"
                local model = workspace.PivotModel
                assert(model.PrimaryPart == nil,
                    'non-descendant PrimaryPart must clear at the next simulation step')
                model.PrimaryPart = model.Child
                assert(model.PrimaryPart == model.Child)
                model.PrimaryPart = nil
                assert(model.PrimaryPart == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("create"));
            Assert.IsTrue(stack.Runtime.IsLoaded("verify"));
        }

        [Test]
        public void Lua_ModelWorldPivot_RoundTripsAndStaysFixedWhenPartsMove()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local function nearCFrame(a, b)
                    local ac = { a:GetComponents() }
                    local bc = { b:GetComponents() }
                    for i = 1, 12 do
                        if math.abs(ac[i] - bc[i]) >= 1e-4 then
                            return false
                        end
                    end
                    return true
                end

                local model = Instance.new('Model')
                local part = Instance.new('Part')
                part.CFrame = CFrame.new(2, 3, 4)
                part.Parent = model
                local pivot = CFrame.new(-8, 6, 12) * CFrame.Angles(0.3, -0.5, 0.7)
                model.WorldPivot = pivot
                assert(nearCFrame(model.WorldPivot, pivot))
                assert(nearCFrame(model:GetPivot(), pivot))
                local partBefore = part.CFrame
                local replacement = CFrame.new(9, -4, 2) * CFrame.Angles(-0.2, 0.1, 0.8)
                model.WorldPivot = replacement
                assert(nearCFrame(model.WorldPivot, replacement))
                assert(nearCFrame(model:GetPivot(), replacement))
                assert(part.CFrame == partBefore, 'setting WorldPivot must not move descendants')
                part.CFrame = CFrame.new(100, 200, 300)
                assert(nearCFrame(model:GetPivot(), replacement),
                    'explicit WorldPivot must stay fixed when descendants move')");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_DataModelBindToClose_ValidatesFunctionBeforeMvp5Stub()
        {
            LuaCsModStack badArgumentStack = BuildStack(new LuaCsRbxApiBindings());
            Exception badArgument = LoadFails(
                badArgumentStack, "bad", "game:BindToClose('not a function')");
            StringAssert.Contains("BAD_ARGUMENT", FullText(badArgument));
            StringAssert.Contains(
                "game:BindToClose expects a function at argument 1",
                FullText(badArgument));
            StringAssert.Contains("pass a function, got string at argument 1", FullText(badArgument));

            LuaCsModStack notImplementedStack = BuildStack(new LuaCsRbxApiBindings());
            Exception notImplemented = LoadFails(
                notImplementedStack, "stub", "game:BindToClose(function() end)");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(notImplemented));
            StringAssert.Contains("game:BindToClose", FullText(notImplemented));
            StringAssert.Contains("MVP5", FullText(notImplemented));
        }

        [Test]
        public void Lua_R6_7_DatatypeAttribute_Vector3RoundTrip()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", @"
                local f = Instance.new('Folder', workspace)
                f:SetAttribute('Spawn', Vector3.new(1, 2, 3))
                f:SetAttribute('Tint', Color3.fromRGB(255, 0, 0))
                local v = f:GetAttribute('Spawn')
                assert(v == Vector3.new(1, 2, 3))
                assert(v.X == 1 and v.Y == 2 and v.Z == 3)
                assert(f:GetAttribute('Tint') == Color3.fromRGB(255, 0, 0))
                local attrs = f:GetAttributes()
                assert(attrs.Spawn == Vector3.new(1, 2, 3))");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_UnsupportedDatatypeAttribute_RejectedWithSupportedList()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "Instance.new('Folder'):SetAttribute('Bad', CFrame.new(1, 2, 3))");
            StringAssert.Contains("BAD_ARGUMENT", FullText(ex));
            StringAssert.Contains("CFrame", FullText(ex));
            StringAssert.Contains("Vector3, Vector2, Color3, or UDim", FullText(ex));
        }

        [Test]
        public void Lua_SignalConnect_UsesGeneralDeferredSurface()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m", "workspace.ChildAdded:Connect(function() end)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_TaskWait_TopLevelSuspendsAndResumes_AndParallelSwitchesAreNoOps()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            stack.Runtime.LoadMod("noop", @"
                task.synchronize()
                task.desynchronize()");
            Assert.IsTrue(stack.Runtime.IsLoaded("noop"), "DEV-5: parallel switches must be no-ops");

            stack.Runtime.LoadMod("waiter", @"
                store_set('phase', 'waiting')
                task.wait(1)
                store_set('phase', 'resumed')");
            Assert.IsTrue(stack.Runtime.IsLoaded("waiter"));
            Assert.AreEqual("waiting", store.Get("waiter", "phase"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("waiting", store.Get("waiter", "phase"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("resumed", store.Get("waiter", "phase"));
        }

        [Test]
        public void Lua_WaitForChild_TimeoutZeroReturnsNilImmediately()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("m",
                "assert(workspace:WaitForChild('NeverThere', 0) == nil)");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Typeof_ReturnsRobloxTypeNames()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            stack.Runtime.LoadMod("m", @"
                local function expect(value, name)
                    local got = typeof(value)
                    assert(got == name, 'typeof: expected ' .. name .. ', got ' .. tostring(got))
                end
                expect(workspace, 'Instance')
                expect(Vector3.zero, 'Vector3')
                expect(Vector2.new(1, 2), 'Vector2')
                expect(CFrame.new(), 'CFrame')
                expect(Color3.new(), 'Color3')
                expect(UDim.new(0, 1), 'UDim')
                expect(UDim2.new(), 'UDim2')
                expect(Enum.KeyCode.A, 'EnumItem')
                expect(Enum.KeyCode, 'Enum')
                expect(Enum, 'Enums')
                expect(Random.new(1), 'Random')
                expect(TweenInfo.new(1), 'TweenInfo')
                expect(RaycastParams.new(), 'RaycastParams')
                expect(workspace.ChildAdded, 'RBXScriptSignal')
                local connection = workspace.ChildAdded:Connect(function() end)
                expect(connection, 'RBXScriptConnection')
                connection:Disconnect()
                expect(task.spawn(function() task.wait(1) end), 'thread')
                expect(coroutine.create(function() end), 'thread')
                expect(nil, 'nil')
                expect(true, 'boolean')
                expect(1, 'number')
                expect('s', 'string')
                expect({}, 'table')
                expect(print, 'function')
                assert(type(workspace) == 'userdata', 'type keeps the plain Lua name')
                local ok, err = pcall(typeof)
                assert(not ok and string.find(tostring(err), 'typeof expects a value', 1, true),
                    tostring(err))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_Warn_LogsAsWarning()
        {
            List<string> log = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(log: log.Add));

            stack.Runtime.LoadMod("m", "warn('careful', 3, Vector3.new(1, 2, 3), nil)");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"), "warn is a registered global");
            List<string> lines = log.FindAll(line => line.Contains("warn from"));
            Assert.AreEqual(1, lines.Count, string.Join(" | ", log));
            StringAssert.Contains("mod 'm'", lines[0]);
            StringAssert.Contains("careful 3 1, 2, 3 nil", lines[0],
                "arguments are converted like tostring and joined by spaces");
        }

        [Test]
        public void Lua_Warn_WritesTheAttachedModLogAtWarnLevel()
        {
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            CoreAI.Ai.Logging.LuaLogService modLog = new();
            roblox.AttachModLog(modLog);
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("m", "warn('low fuel', 2)");

            IReadOnlyList<CoreAI.Ai.Logging.LuaLogEntry> entries = modLog.Query(
                new CoreAI.Ai.Logging.LuaLogQuery
                {
                    ModId = "m",
                    MinLevel = CoreAI.Ai.Logging.LuaLogLevel.Warn
                });
            Assert.AreEqual(1, entries.Count, "one warn entry in the mod's own log");
            Assert.AreEqual(CoreAI.Ai.Logging.LuaLogLevel.Warn, entries[0].Level);
            Assert.AreEqual("low fuel 2", entries[0].Message);
            Assert.IsFalse(log.Exists(line => line.Contains("warn from")),
                "with a mod log attached the warning is not duplicated into the host log");
        }

        [Test]
        public void Negative_Lua_Warn_AFloodIsCappedPerWindowAndTheRestAreSummed()
        {
            List<string> log = new();
            SteppedClockSource clock = new() { ProcessTimeSeconds = 5d };
            LuaCsRbxApiBindings roblox = new(log: log.Add, clockSource: clock);
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("chatty", @"
                for i = 1, 25 do warn('tick', i) end
                task.wait(1)
                warn('after the window')");

            Assert.AreEqual(LuaCsRbxApiBindings.MaxWarnLinesPerWindow,
                log.FindAll(line => line.Contains("warn from mod 'chatty'")).Count,
                "a script warning in a loop reaches the host log a bounded number of times");

            clock.ProcessTimeSeconds += LuaCsRbxApiBindings.WarnLogWindowSeconds;
            roblox.Scheduler.Advance(1d);

            Assert.IsTrue(log.Exists(line => line.Contains("5 more warn lines from mod 'chatty'")),
                string.Join(" | ", log));
            Assert.IsTrue(log.Exists(line => line.Contains("warn from mod 'chatty': after the window")),
                "the next window logs again");
        }

        [Test]
        public void Negative_Lua_Warn_ATostringThatFailsRaisesInsteadOfLogging()
        {
            List<string> log = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(log: log.Add));

            Exception ex = LoadFails(stack, "m",
                "warn(setmetatable({}, {__tostring = function() error('tostring broke') end}))");

            StringAssert.Contains("tostring broke", FullText(ex));
            Assert.IsFalse(log.Exists(line => line.Contains("warn from")));
        }

#if COREAI_LUA
        /// <summary>
        /// A mod whose global <c>tostring</c> calls <c>warn</c> again, with its own recursion guard failing at
        /// <paramref name="modCap"/> levels: stores whether <c>pcall(warn, 1)</c> succeeded, its error and the
        /// deepest level the mod's <c>tostring</c> reached.
        /// </summary>
        private static string WarnReenteredFromTheModsTostring(int modCap)
        {
            return "local nativeToString = tostring\n" +
                   "local depth, deepest = 0, 0\n" +
                   "tostring = function(x)\n" +
                   "  depth = depth + 1\n" +
                   "  if depth > deepest then deepest = depth end\n" +
                   "  if depth >= " + modCap + " then error('mod cap', 0) end\n" +
                   "  warn(x)\n" +
                   "  depth = depth - 1\n" +
                   "  return ''\n" +
                   "end\n" +
                   "local ok, err = pcall(warn, 1)\n" +
                   "tostring = nativeToString\n" +
                   "store_set('ok', tostring(ok))\n" +
                   "store_set('err', tostring(err))\n" +
                   "store_set('deepest', string.format('%d', deepest))";
        }

        [Test]
        [Timeout(120000)]
        public void Negative_Lua_Warn_ReenteredFromTheModsTostring_StopsAtTheCStackLimitWithOneCatchableLine()
        {
            // WHY (A2-05): warn converts each argument with the global tostring, which a mod may replace with a
            // Lua function that calls warn again. Each such call is a nested VM run on the .NET stack, and it was
            // not counted against the C-call limit, so the error at the bottom unwound in about N squared with no
            // instruction running and no budget hook firing: 11 s at a mod-imposed depth of 1,000, 32 s unbounded
            // under a 10 s budget.
            // WHY the reference run: the unwind left at the limit is intrinsic to Lua-CSharp and its wall time
            // depends on the host, so the capped run is timed against the same shape failing on the mod's own
            // cap at 150 levels, as CallsBackIntoLua_NestedPastTheCStackLimit_FailFastWithOneCatchableLine does,
            // through the same comparison of the fastest of several runs of each.
            MemoryStore store = new();
            List<string> log = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(log: log.Add), store);
            long referenceMs = LuaCsSecureSandboxEditModeTests.ElapsedMs(
                () => stack.Runtime.LoadMod("reference", WarnReenteredFromTheModsTostring(150)));
            Assert.AreEqual("false", store.Get("reference", "ok"));
            Assert.AreEqual("mod cap", store.Get("reference", "err"),
                "the reference run fails on the mod's own cap, below the C-stack limit");

            long cappedMs = LuaCsSecureSandboxEditModeTests.ElapsedMs(
                () => stack.Runtime.LoadMod("deep", WarnReenteredFromTheModsTostring(1000)));

            Assert.IsTrue(stack.Runtime.IsLoaded("deep"), "pcall catches the error, so the mod loads");
            Assert.AreEqual("false", store.Get("deep", "ok"));
            string expectedLine = LuaCsSecureEnvironment.CStackOverflowMessage + " (warn: more than "
                                  + LuaCsSecureEnvironment.MaxCCallDepth
                                  + " nested calls from library functions back into Lua)";
            string error = store.Get("deep", "err");
            Assert.AreEqual(expectedLine, error);
            LuaCsSecureSandboxEditModeTests.AssertIsOnlyTheErrorLine(error);
            Assert.AreEqual(LuaCsSecureEnvironment.MaxCCallDepth.ToString(CultureInfo.InvariantCulture),
                store.Get("deep", "deepest"), "the nesting stops at the limit, not at the mod's own cap of 1,000");
            Assert.IsFalse(log.Exists(line => line.Contains("warn from")),
                "no nested warn finished, so none of them logged: " + string.Join(" | ", log));
            int rerun = 0;
            LuaCsSecureSandboxEditModeTests.AssertCappedNestingUnwindsLikeItsReference(referenceMs, cappedMs,
                () => LoadWarnReentryAgain(stack, store, "reference", 150, "reference-" + ++rerun),
                () => LoadWarnReentryAgain(stack, store, "deep", 1000, "deep-" + ++rerun));
            Assert.IsFalse(log.Exists(line => line.Contains("warn from")),
                "no nested warn of a timed run finished either: " + string.Join(" | ", log));
        }

        /// <summary>
        /// Loads <see cref="WarnReenteredFromTheModsTostring"/> with <paramref name="modCap"/> once more as the new mod
        /// <paramref name="modId"/> and fails unless it stored exactly what <paramref name="firstModId"/>, the first
        /// mod of that shape, stored.
        /// </summary>
        private static void LoadWarnReentryAgain(LuaCsModStack stack, MemoryStore store, string firstModId, int modCap,
            string modId)
        {
            stack.Runtime.LoadMod(modId, WarnReenteredFromTheModsTostring(modCap));
            foreach (string key in new[] { "ok", "err", "deepest" })
            {
                Assert.AreEqual(store.Get(firstModId, key), store.Get(modId, key),
                    "every timed run of a shape must end the same way (" + key + ")");
            }
        }

        [Test]
        [Timeout(60000)]
        public void Negative_Lua_Warn_UsedAsTostring_StopsAtTheCStackLimitWithOneCatchableLine()
        {
            // WHY: with tostring = warn, warn converts its argument by calling itself, with no Lua instruction
            // in between for a budget hook to stop, so only the C-call limit ends the recursion. Uncounted, it
            // overflowed the .NET stack: the test host process crashed instead of this test failing.
            MemoryStore store = new();
            List<string> log = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(log: log.Add), store);

            stack.Runtime.LoadMod("m", @"
                local nativeToString = tostring
                tostring = warn
                local ok, err = pcall(warn, 1)
                tostring = nativeToString
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))
                warn('after the refusal')");

            Assert.AreEqual("false", store.Get("m", "ok"));
            string error = store.Get("m", "err");
            Assert.AreEqual(LuaCsSecureEnvironment.CStackOverflowMessage + " (warn: more than "
                            + LuaCsSecureEnvironment.MaxCCallDepth
                            + " nested calls from library functions back into Lua)", error);
            LuaCsSecureSandboxEditModeTests.AssertIsOnlyTheErrorLine(error);
            CollectionAssert.AreEqual(new[] { "[RbxApi] warn from mod 'm': after the refusal" },
                log.FindAll(line => line.Contains("warn from")),
                "no nested warn finished, and the refusal gave back every level it held: " + string.Join(" | ", log));
        }
#endif

        [Test]
        public void Lua_Warn_TostringMetamethodsAndAReplacedTostring_StillConvertTheArguments()
        {
            // WHY the negative twin: counting warn's call into tostring must not change what warn logs, for a
            // __tostring, for a __tostring that warns once itself, or for a mod's own replacement of tostring.
            List<string> log = new();
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(log: log.Add));

            stack.Runtime.LoadMod("m", @"
                local labelled = setmetatable({}, {__tostring = function() return 'custom text' end})
                warn('plain', labelled, 2)
                local noisy = setmetatable({}, {__tostring = function() warn('inner') return 'outer' end})
                warn(noisy)
                local nativeToString = tostring
                tostring = function(x) return '<' .. nativeToString(x) .. '>' end
                warn('wrapped', 3)
                tostring = nativeToString");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            CollectionAssert.AreEqual(new[]
            {
                "[RbxApi] warn from mod 'm': plain custom text 2",
                "[RbxApi] warn from mod 'm': inner",
                "[RbxApi] warn from mod 'm': outer",
                "[RbxApi] warn from mod 'm': <wrapped> <3>"
            }, log.FindAll(line => line.Contains("warn from")), string.Join(" | ", log));
        }

        [TestCase("BrickColor")]
        [TestCase("NumberSequence")]
        [TestCase("ColorSequence")]
        [TestCase("NumberRange")]
        [TestCase("Ray")]
        [TestCase("Region3")]
        [TestCase("Rect")]
        [TestCase("PhysicalProperties")]
        [TestCase("OverlapParams")]
        [TestCase("DateTime")]
        public void Lua_UnimplementedDatatypeGlobal_RaisesLoudBacklogStub(string name)
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            Exception ex = LoadFails(stack, "m", "local value = " + name + ".new()");

            string fullText = FullText(ex);
            StringAssert.Contains("NOT_IMPLEMENTED", fullText);
            StringAssert.Contains(
                name + ".new is a known Rbx member, but no roadmap rung is assigned.", fullText);
            StringAssert.Contains("| fix: ", fullText);
            StringAssert.DoesNotContain("attempt to index a nil value", fullText);
        }

        [Test]
        public void Lua_BrickColorGlobal_RaisesNotImplemented_OnCallWriteAndRead()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            string called = FullText(LoadFails(stack, "call", "local value = BrickColor('Bright red')"));
            string written = FullText(LoadFails(stack, "write", "DateTime.now = 1"));

            StringAssert.Contains(
                "NOT_IMPLEMENTED: BrickColor is a known Rbx member, but no roadmap rung is assigned.",
                called);
            StringAssert.Contains("fix: use Color3.fromRGB(r, g, b)", called);
            StringAssert.Contains("DateTime.now is a known Rbx member", written);
            stack.Runtime.LoadMod("read", @"
                assert(tostring(BrickColor) == 'BrickColor')
                assert(typeof(BrickColor) == 'table')");
            Assert.IsTrue(stack.Runtime.IsLoaded("read"), "naming the stub is not an access");
        }

        [Test]
        public void Lua_SharedGlobal_IsADeliberatelyUnsupportedStub()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());

            string written = FullText(LoadFails(stack, "writer", "shared.score = 1"));
            string read = FullText(LoadFails(stack, "reader", "local score = shared.score"));

            foreach (string fullText in new[] { written, read })
            {
                StringAssert.Contains("NOT_IMPLEMENTED", fullText);
                StringAssert.Contains(
                    "shared.score is a known Rbx member deliberately unsupported by CoreAI.", fullText);
                StringAssert.Contains("mods_export", fullText);
            }
        }

        [Test]
        public void Lua_TypeofWarnAndStubs_AreRegisteredOnTheReadTier()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings(), caps: LuaCapabilities.Read);

            stack.Runtime.LoadMod("reader", @"
                assert(typeof(workspace) == 'Instance')
                assert(type(warn) == 'function')
                local ok, err = pcall(function() return BrickColor.new end)
                assert(not ok and string.find(tostring(err), 'NOT_IMPLEMENTED', 1, true), tostring(err))");

            Assert.IsTrue(stack.Runtime.IsLoaded("reader"));
        }

        [Test]
        public void Lua_CameraGlobals_FireCameraPropertyChanged_LikeThePropertyWrites()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local camera = workspace.CurrentCamera
                local moves, follows = 0, 0
                camera:GetPropertyChangedSignal('CFrame'):Connect(function()
                    moves = moves + 1
                    store_set('moves', tostring(moves))
                end)
                camera:GetPropertyChangedSignal('CameraSubject'):Connect(function()
                    follows = follows + 1
                    store_set('follows', tostring(follows))
                end)
                local part = Instance.new('Part')
                part.Parent = workspace
                camera_set_cframe(CFrame.new(1, 2, 3))
                camera_follow(part)
                camera_follow(part)
                task.wait()
                camera_follow(nil)");
            roblox.Scheduler.Advance(0d);
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("1", store.Get("m", "moves"), "camera_set_cframe fires Changed(CFrame)");
            Assert.AreEqual("2", store.Get("m", "follows"),
                "camera_follow fires Changed(CameraSubject) on each real change and not on a repeat");
        }

        private sealed class SteppedClockSource : IRbxClockSource
        {
            public double GameTimeSeconds { get; set; }

            public long UnixTimeSeconds { get; set; }

            public double ProcessTimeSeconds { get; set; }

            public double UnixTimeSecondsFractional { get; set; }
        }

        private static RbxNetworkEventMessage UnknownRemoteEvent(ulong remoteId, string senderActorId)
        {
            return new RbxNetworkEventMessage(new InstanceId(remoteId), RbxNetworkDirection.ClientToServer,
                RbxNetworkReliability.ReliableOrdered, senderActorId, null, Array.Empty<byte>());
        }

        [Test]
        public void Network_UnknownRemoteFlood_LogsOncePerSenderAndWindow_AndCountsEveryPacket()
        {
            List<string> log = new();
            SteppedClockSource clock = new() { ProcessTimeSeconds = 5d };
            TrackingNetworkBridge bridge = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add, networkBridge: bridge, clockSource: clock);
            roblox.ConnectActor(Actor("flooder"));
            roblox.ConnectActor(Actor("bystander"));

            for (int index = 0; index < 1000; index++)
            {
                bridge.SendEvent(UnknownRemoteEvent(900000UL + (ulong)index, "flooder"));
            }

            Assert.AreEqual(1, log.FindAll(line => line.Contains("unknown RemoteEvent")).Count,
                "1,000 packets produce one line");
            Assert.AreEqual(1000, roblox.RejectedNetworkEventCount, "every packet is counted");

            bridge.SendEvent(UnknownRemoteEvent(99UL, "bystander"));
            Assert.AreEqual(2, log.FindAll(line => line.Contains("unknown RemoteEvent")).Count,
                "a second sender's first warning is not hidden by the first sender's flood");

            clock.ProcessTimeSeconds += LuaCsRbxApiBindings.NetworkWarningWindowSeconds;
            bridge.SendEvent(UnknownRemoteEvent(99UL, "flooder"));
            List<string> lines = log.FindAll(line => line.Contains("unknown RemoteEvent"));
            Assert.AreEqual(3, lines.Count, "the next window logs again");
            StringAssert.Contains("'flooder'", lines[2]);
            StringAssert.Contains("999 more like it", lines[2], "the suppressed packets are summed");
            Assert.AreEqual(1002, roblox.RejectedNetworkEventCount);
        }

        [Test]
        public void Negative_Network_ADisconnectedSendersWindowIsForgotten()
        {
            List<string> log = new();
            SteppedClockSource clock = new() { ProcessTimeSeconds = 5d };
            TrackingNetworkBridge bridge = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add, networkBridge: bridge, clockSource: clock);
            ActorContext sender = Actor("returning");
            roblox.ConnectActor(sender);
            bridge.SendEvent(UnknownRemoteEvent(900001UL, "returning"));

            Assert.IsTrue(roblox.DisconnectActor(sender));
            roblox.ConnectActor(sender);
            bridge.SendEvent(UnknownRemoteEvent(900002UL, "returning"));

            Assert.AreEqual(2, log.FindAll(line => line.Contains("unknown RemoteEvent")).Count,
                "a reconnected sender starts a fresh window instead of inheriting a stale one");
        }

        [Test]
        public void SchedulerResume_ModOriginTagIsInterned_AndReleasedWhenTheModsThreadsAreKilled()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            for (int index = 0; index < 3; index++)
            {
                stack.Runtime.LoadMod("cached-" + index, "task.spawn(function() task.wait(0.1) end)");
            }

            roblox.Scheduler.Advance(0.2d);

            Assert.AreEqual(3, roblox.ResumeCacheModCount, "every resuming mod interned its labels");
            Assert.AreSame(roblox.ModOriginTag("cached-0"), roblox.ModOriginTag("cached-0"),
                "a resume reuses the mod's origin tag instead of building one");
            for (int index = 0; index < 3; index++)
            {
                roblox.KillAllScheduledOwnedBy("cached-" + index);
            }

            Assert.AreEqual(0, roblox.ResumeCacheModCount,
                "the unload path releases both interned strings of every mod");
        }

        // ---- Property-changed notifications (A3-01, A3-02) ----------------------------------

        /// <summary>A grounded motor whose direction a test sets; every jump is taken.</summary>
        private sealed class SteerableMotor : IRbxCharacterMotor
        {
            public RbxVector3 Direction { get; set; } = RbxVector3.Zero;

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => Direction;

            public bool IsGrounded => true;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }
        }

        /// <summary>BoundProperties rows whose value cannot change once the instance exists.</summary>
        private static readonly Dictionary<string, string> ImmutableBoundProperties =
            new(StringComparer.Ordinal)
            {
                { "Instance.ClassName", "fixed by the class" },
                { "Workspace.CurrentCamera", "the one world camera; assigning another is the loud stub" },
                { "Workspace.SignalBehavior", "Deferred is the only mode" },
                { "Player.UserId", "fixed when the player is admitted" },
                { "Players.LocalPlayer", "fixed for the reading mod context" },
                { "Tween.Instance", "fixed by TweenService:Create" },
                { "Tween.TweenInfo", "fixed by TweenService:Create" }
            };

        private const string AnchoredPartLua =
            "(function() local p = Instance.new('Part'); p.Anchored = true; p.Parent = workspace; "
            + "return p end)()";

        /// <summary>
        /// One driver per mutable BoundProperties row: <c>Target</c> is a Lua expression for the
        /// watched instance (bound to <c>t</c>), <c>Drive</c> makes one real change and then
        /// assigns the same value again, which must fire nothing, and <c>Changes</c> is how many
        /// real changes that causes. A null drive is made from C# (the motor's direction, the
        /// host's MaxPlayers). Jump reads "in the Jumping state", so the jump enters that state and
        /// the next Heartbeat's landing leaves it: two changes.
        /// </summary>
        private static readonly (string Row, string Target, string Drive, int Changes)[]
            BoundPropertyDrivers =
            {
                ("Instance.Name", "Instance.new('Folder')", "t.Name = 'Renamed'; t.Name = 'Renamed'", 1),
                ("Instance.Parent", "Instance.new('Folder')", "t.Parent = workspace; t.Parent = workspace", 1),
                ("Instance.Archivable", "Instance.new('Folder')", "t.Archivable = false; t.Archivable = false", 1),
                ("Workspace.Gravity", "workspace", "t.Gravity = 50; t.Gravity = 50", 1),
                ("Model.PrimaryPart",
                    "(function() local m = Instance.new('Model'); local p = Instance.new('Part'); "
                    + "p.Anchored = true; p.Parent = m; m.Parent = workspace; return m end)()",
                    "local p = t:FindFirstChildOfClass('Part'); t.PrimaryPart = p; t.PrimaryPart = p", 1),
                ("Model.WorldPivot", "Instance.new('Model')",
                    "t.WorldPivot = CFrame.new(1, 2, 3); t.WorldPivot = CFrame.new(1, 2, 3)", 1),
                ("BasePart.Shape", AnchoredPartLua,
                    "t.Shape = Enum.PartType.Ball; t.Shape = Enum.PartType.Ball", 1),
                ("BasePart.Material", AnchoredPartLua,
                    "t.Material = Enum.Material.Wood; t.Material = Enum.Material.Wood", 1),
                ("BasePart.MaterialVariant", AnchoredPartLua,
                    "t.MaterialVariant = 'Mossy'; t.MaterialVariant = 'Mossy'", 1),
                ("BasePart.Position", AnchoredPartLua,
                    "t.Position = Vector3.new(1, 2, 3); t.Position = Vector3.new(1, 2, 3)", 1),
                ("BasePart.Size", AnchoredPartLua,
                    "t.Size = Vector3.new(2, 3, 4); t.Size = Vector3.new(2, 3, 4)", 1),
                ("BasePart.CFrame", AnchoredPartLua,
                    "t.CFrame = CFrame.new(4, 5, 6); t.CFrame = CFrame.new(4, 5, 6)", 1),
                ("BasePart.Orientation", AnchoredPartLua,
                    "t.Orientation = Vector3.new(0, 90, 0); t.Orientation = Vector3.new(0, 90, 0)", 1),
                ("BasePart.Rotation", AnchoredPartLua,
                    "t.Rotation = Vector3.new(0, 45, 0); t.Rotation = Vector3.new(0, 45, 0)", 1),
                ("BasePart.Color", AnchoredPartLua,
                    "t.Color = Color3.new(1, 0, 0); t.Color = Color3.new(1, 0, 0)", 1),
                ("BasePart.Transparency", AnchoredPartLua,
                    "t.Transparency = 0.5; t.Transparency = 0.5", 1),
                ("BasePart.Anchored", AnchoredPartLua, "t.Anchored = false; t.Anchored = false", 1),
                ("BasePart.CanCollide", AnchoredPartLua, "t.CanCollide = false; t.CanCollide = false", 1),
                ("Camera.CFrame", "workspace.CurrentCamera",
                    "t.CFrame = CFrame.new(7, 8, 9); t.CFrame = CFrame.new(7, 8, 9)", 1),
                ("Camera.CameraType", "workspace.CurrentCamera",
                    "t.CameraType = Enum.CameraType.Scriptable; t.CameraType = Enum.CameraType.Scriptable", 1),
                ("Camera.CameraSubject", "workspace.CurrentCamera",
                    "local s = " + AnchoredPartLua + "; t.CameraSubject = s; t.CameraSubject = s", 1),
                ("Humanoid.Health", "Instance.new('Humanoid')", "t.Health = 50; t.Health = 50", 1),
                ("Humanoid.MaxHealth", "Instance.new('Humanoid')", "t.MaxHealth = 150; t.MaxHealth = 150", 1),
                ("Humanoid.WalkSpeed", "Instance.new('Humanoid')", "t.WalkSpeed = 20; t.WalkSpeed = 20", 1),
                ("Humanoid.JumpPower", "Instance.new('Humanoid')", "t.JumpPower = 60; t.JumpPower = 60", 1),
                ("Humanoid.JumpHeight", "Instance.new('Humanoid')", "t.JumpHeight = 9; t.JumpHeight = 9", 1),
                ("Humanoid.UseJumpPower", "Instance.new('Humanoid')",
                    "t.UseJumpPower = false; t.UseJumpPower = false", 1),
                ("Humanoid.DisplayName", "Instance.new('Humanoid')",
                    "t.DisplayName = 'Hero'; t.DisplayName = 'Hero'", 1),
                ("Humanoid.MoveDirection",
                    "(function() local h = Instance.new('Humanoid'); h.Name = 'MoveProbe'; "
                    + "h.Parent = workspace; return h end)()", null, 1),
                ("Humanoid.RootPart",
                    "(function() local m = Instance.new('Model'); m.Parent = workspace; "
                    + "local h = Instance.new('Humanoid'); h.Parent = m; return h end)()",
                    "local r = Instance.new('Part'); r.Name = 'HumanoidRootPart'; r.Anchored = true; "
                    + "r.Parent = t.Parent", 1),
                ("Humanoid.Jump", "Instance.new('Humanoid')", "t.Jump = true; t.Jump = true", 2),
                ("Player.DisplayName", "game:GetService('Players'):GetPlayers()[1]",
                    "t.DisplayName = 'Renamed'; t.DisplayName = 'Renamed'", 1),
                ("Player.Character", "game:GetService('Players'):GetPlayers()[1]",
                    "local m = Instance.new('Model'); t.Character = m; t.Character = m", 1),
                ("Players.CharacterAutoLoads", "game:GetService('Players')",
                    "t.CharacterAutoLoads = false; t.CharacterAutoLoads = false", 1),
                ("Players.RespawnTime", "game:GetService('Players')",
                    "t.RespawnTime = 1; t.RespawnTime = 1", 1),
                ("Players.MaxPlayers", "game:GetService('Players')", null, 1),
                ("UserInputService.MouseBehavior", "game:GetService('UserInputService')",
                    "t.MouseBehavior = Enum.MouseBehavior.LockCenter; "
                    + "t.MouseBehavior = Enum.MouseBehavior.LockCenter", 1),
                ("ClickDetector.MaxActivationDistance", "Instance.new('ClickDetector')",
                    "t.MaxActivationDistance = 10; t.MaxActivationDistance = 10", 1),
                ("MaterialVariant.BaseMaterial", "Instance.new('MaterialVariant')",
                    "t.BaseMaterial = Enum.Material.Wood; t.BaseMaterial = Enum.Material.Wood", 1),
                ("MaterialVariant.ColorMap", "Instance.new('MaterialVariant')",
                    "t.ColorMap = 'rbxassetid://1'; t.ColorMap = 'rbxassetid://1'", 1),
                ("MaterialVariant.NormalMap", "Instance.new('MaterialVariant')",
                    "t.NormalMap = 'rbxassetid://2'; t.NormalMap = 'rbxassetid://2'", 1),
                ("MaterialVariant.RoughnessMap", "Instance.new('MaterialVariant')",
                    "t.RoughnessMap = 'rbxassetid://3'; t.RoughnessMap = 'rbxassetid://3'", 1),
                ("MaterialVariant.MetalnessMap", "Instance.new('MaterialVariant')",
                    "t.MetalnessMap = 'rbxassetid://4'; t.MetalnessMap = 'rbxassetid://4'", 1),
                ("MaterialVariant.StudsPerTile", "Instance.new('MaterialVariant')",
                    "t.StudsPerTile = 4; t.StudsPerTile = 4", 1),
                ("ValueBase.Value", "Instance.new('IntValue')", "t.Value = 5; t.Value = 5", 1),
                ("Tween.PlaybackState",
                    "game:GetService('TweenService'):Create(" + AnchoredPartLua
                    + ", TweenInfo.new(1), {Transparency = 1})",
                    "t:Play(); t:Play()", 1)
            };

        /// <summary>
        /// WHY (A3-01): the BoundProperties drift guard only proved that every row is readable and
        /// accepted by GetPropertyChangedSignal, so fourteen rows — ClickDetector, Players,
        /// UserInputService, MaterialVariant, Tween.PlaybackState, and Humanoid's MoveDirection,
        /// RootPart and Jump — were accepted and never fired: a script watching them waited forever.
        /// This drives every mutable row and demands exactly one Changed and one property-signal
        /// fire per real change, none for the repeated assignment; a new row fails here until it
        /// has a driver or is listed as immutable.
        /// </summary>
        [Test]
        public void BoundProperties_EveryRowFiresChangedAndItsSignalOncePerRealChange()
        {
            HashSet<string> tableRows = new(StringComparer.Ordinal);
            foreach ((string className, string property) in
                     LuaCsRbxInstanceBindings.EnumerateBoundProperties())
            {
                tableRows.Add(className + "." + property);
            }

            HashSet<string> covered = new(ImmutableBoundProperties.Keys, StringComparer.Ordinal);
            foreach ((string Row, string Target, string Drive, int Changes) driver in BoundPropertyDrivers)
            {
                Assert.IsTrue(covered.Add(driver.Row), driver.Row + " is listed twice");
            }

            List<string> uncovered = new();
            foreach (string row in tableRows)
            {
                if (!covered.Contains(row))
                {
                    uncovered.Add(row);
                }
            }

            List<string> stale = new();
            foreach (string row in covered)
            {
                if (!tableRows.Contains(row))
                {
                    stale.Add(row);
                }
            }

            CollectionAssert.IsEmpty(uncovered,
                "every BoundProperties row needs a driver here, or an entry in ImmutableBoundProperties");
            CollectionAssert.IsEmpty(stale, "these rows are no longer in BoundProperties");

            LuaCsRbxApiBindings roblox = new();
            roblox.AttachCharacterMotorFactory(_ => new SteerableMotor());
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            roblox.Players.EnsureActor(roblox.Registry, "drift-actor");
            // WHY two frames before any watcher: the admitted player's join spawn lands on the next
            // Advance, and it must not count as a change of Player.Character.
            roblox.Scheduler.Advance(0d);
            roblox.Scheduler.Advance(0d);

            System.Text.StringBuilder script = new(@"
                local function watch(row, target, property)
                    local isValue = target:IsA('ValueBase')
                    local changed, signalled = 0, 0
                    target.Changed:Connect(function(name)
                        if isValue or name == property then
                            changed = changed + 1
                            store_set(row .. '|changed', tostring(changed))
                        end
                    end)
                    target:GetPropertyChangedSignal(property):Connect(function()
                        signalled = signalled + 1
                        store_set(row .. '|signal', tostring(signalled))
                    end)
                end
");
            foreach ((string Row, string Target, string Drive, int Changes) driver in BoundPropertyDrivers)
            {
                string property = driver.Row.Substring(driver.Row.IndexOf('.') + 1);
                script.Append("do local t = ").Append(driver.Target).Append("; watch('")
                    .Append(driver.Row).Append("', t, '").Append(property).Append("'); ")
                    .Append(driver.Drive ?? "").Append(" end\n");
            }

            stack.Runtime.LoadMod("drift", script.ToString());
            Assert.IsTrue(stack.Runtime.IsLoaded("drift"));

            RbxHumanoid moveProbe = (RbxHumanoid)roblox.Registry.WorldRoot.FindFirstChild("MoveProbe");
            Assert.IsNotNull(moveProbe, "the MoveDirection probe humanoid exists");
            SteerableMotor motor = moveProbe.Motor as SteerableMotor;
            Assert.IsNotNull(motor, "the probe humanoid is driven by the test motor");
            motor.Direction = new RbxVector3(1f, 0f, 0f);
            roblox.Players.MaxPlayers = 8;
            roblox.Players.MaxPlayers = 8;
            for (int frame = 0; frame < 3; frame++)
            {
                roblox.Scheduler.Advance(0d);
            }

            List<string> wrong = new();
            foreach ((string Row, string Target, string Drive, int Changes) driver in BoundPropertyDrivers)
            {
                string expected = driver.Changes.ToString(CultureInfo.InvariantCulture);
                string changed = store.Get("drift", driver.Row + "|changed");
                string signalled = store.Get("drift", driver.Row + "|signal");
                if (changed != expected || signalled != expected)
                {
                    wrong.Add(driver.Row + ": Changed fired " + (changed == "" ? "0" : changed)
                              + ", the property signal " + (signalled == "" ? "0" : signalled)
                              + ", expected " + expected);
                }
            }

            CollectionAssert.IsEmpty(wrong,
                "every real change fires Changed and the property signal exactly once");
        }

        [Test]
        public void GetPropertyChangedSignal_ARealPropertyCoreAIDoesNotModel_LoadsAndLogsOnce()
        {
            // WHY (A3-02, M1-03): only bound and catalogued names were accepted, so a script
            // watching a real property the catalog has not listed yet — the FloorMaterial idiom of
            // every footstep script — failed its whole load with "not a valid property name".
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("watcher", @"
                local h = Instance.new('Humanoid')
                h.Parent = workspace
                h:GetPropertyChangedSignal('FloorMaterial'):Connect(function()
                    store_set('fired', 'yes')
                end)
                h:GetPropertyChangedSignal('FloorMaterial')
                Instance.new('Humanoid'):GetPropertyChangedSignal('FloorMaterial')
                game:GetService('Players'):GetPropertyChangedSignal('NumPlayers')
                Instance.new('Part'):GetPropertyChangedSignal('LocalTransparencyModifier')
                store_set('loaded', 'true')");
            roblox.Scheduler.Advance(0d);

            Assert.IsTrue(stack.Runtime.IsLoaded("watcher"));
            Assert.AreEqual("true", store.Get("watcher", "loaded"));
            Assert.AreEqual("", store.Get("watcher", "fired"), "the unmodelled property's signal never fires");
            Assert.AreEqual(1, log.FindAll(line =>
                    line.Contains("Humanoid:GetPropertyChangedSignal(\"FloorMaterial\")")).Count,
                "one note per class and name, however many humanoids ask");
            Assert.AreEqual(1, log.FindAll(line => line.Contains("Players.NumPlayers")).Count);
            Assert.AreEqual(1, log.FindAll(line => line.Contains("Part.LocalTransparencyModifier")).Count);
        }

        [Test]
        public void Negative_GetPropertyChangedSignal_ATypoOrANonProperty_IsStillRefused()
        {
            // WHY: accepting unknown names must not bring back the silent typo M1-03 fixed — one
            // edit away from a known property is the typo class, and an event or a method is not a
            // property at all.
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("names", @"
                local p = Instance.new('Part')
                local function try(label, target, name)
                    local ok, err = pcall(function() return target:GetPropertyChangedSignal(name) end)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('case', p, 'position')
                try('swap', p, 'Positoin')
                try('extra', workspace.CurrentCamera, 'CFrameX')
                try('humanoidCase', Instance.new('Humanoid'), 'walkspeed')
                try('event', p, 'Touched')
                try('method', p, 'Destroy')
                try('long', p, string.rep('Q', 101))
                try('bound', p, 'Position')
                try('catalogued', p, 'BrickColor')
                try('unmodelled', p, 'LocalTransparencyModifier')");

            foreach (string label in new[] { "case", "swap", "extra", "humanoidCase", "event", "method", "long" })
            {
                string refused = store.Get("names", label);
                StringAssert.StartsWith("false|", refused, label + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", refused, label);
                StringAssert.Contains("is not a valid property name.", refused, label);
            }

            StringAssert.Contains("position is not a valid property name.", store.Get("names", "case"));
            StringAssert.Contains("\"Position\"", store.Get("names", "case"),
                "the refusal names the property the author meant");
            StringAssert.Contains("\"WalkSpeed\"", store.Get("names", "humanoidCase"));
            foreach (string label in new[] { "bound", "catalogued", "unmodelled" })
            {
                StringAssert.StartsWith("true|", store.Get("names", label), label + " must be accepted");
            }
        }

        [TestCase("position", "Position", true)]
        [TestCase("Positoin", "Position", true)]
        [TestCase("CFrameX", "CFrame", true)]
        [TestCase("Postion", "Position", true)]
        [TestCase("Position", "Position", false)]
        [TestCase("Attachment1", "Attachment0", false)]
        [TestCase("Color3", "Color", false)]
        [TestCase("FloorMaterial", "Material", false)]
        [TestCase("Sit", "Size", false)]
        public void GetPropertyChangedSignal_NearMissRule(string typed, string known, bool nearMiss)
        {
            Assert.AreEqual(nearMiss, LuaCsRbxInstanceBindings.IsOneEditAway(typed, known));
        }

        [Test]
        public void GetPropertyChangedSignal_UnmodelledNames_AreNotedAtMostABoundedNumberOfTimes()
        {
            // WHY: the names are the script's to invent, so the once-per-name note needs a ceiling
            // of its own, or a loop over generated names would grow the log and the note set.
            List<string> log = new();
            LuaCsRbxApiBindings roblox = new(log: log.Add);
            LuaCsModStack stack = BuildStack(roblox);

            stack.Runtime.LoadMod("many", @"
                local p = Instance.new('Part')
                for i = 1, 300 do
                    p:GetPropertyChangedSignal('Unmodelled' .. i)
                end");

            Assert.IsTrue(stack.Runtime.IsLoaded("many"));
            int notes = log.FindAll(line => line.Contains("GetPropertyChangedSignal")).Count;
            Assert.LessOrEqual(notes, 65, "at most 64 notes and one line saying the rest are not logged");
            Assert.Greater(notes, 1);
            Assert.AreEqual(1, log.FindAll(line => line.Contains("are not logged")).Count);
        }

        // ---- Script-keyed signal tables stay bounded (A3-03) --------------------------------

        [Test]
        public void TagAndAttributeSignals_TwentyThousandDistinctKeys_KeepTheTablesBounded_AndHeldOnesStillFire()
        {
            // WHY (A3-03): every distinct tag passed to GetInstanceAddedSignal/RemovedSignal and
            // every attribute name passed to GetAttributeChangedSignal kept its signal for the life
            // of the world, so a loop over generated names grew the host without bound. A signal
            // the script still holds must still fire once it connects (Roblox semantics).
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("keys", @"
                local cs = game:GetService('CollectionService')
                local heldAdded = cs:GetInstanceAddedSignal('Held')
                local heldAttribute = workspace:GetAttributeChangedSignal('Held')
                for i = 1, 20000 do
                    cs:GetInstanceAddedSignal('t' .. i)
                    cs:GetInstanceRemovedSignal('t' .. i)
                    workspace:GetAttributeChangedSignal('a' .. i)
                end
                heldAdded:Connect(function(instance) store_set('added', instance.Name) end)
                heldAttribute:Connect(function()
                    store_set('attribute', tostring(workspace:GetAttribute('Held')))
                end)
                local p = Instance.new('Part')
                p.Name = 'Tagged'
                p.Parent = workspace
                p:AddTag('Held')
                workspace:SetAttribute('Held', 7)");
            roblox.Scheduler.Advance(0d);

            Assert.IsTrue(stack.Runtime.IsLoaded("keys"));
            Assert.LessOrEqual(roblox.CollectionService.TagSignalStrongCount,
                2 * KeyedSignalTable.StrongBudget + 2,
                "40,000 tag signals nobody connected to must not all stay reachable");
            Assert.LessOrEqual(roblox.Registry.WorldRoot.AttributeSignalStrongCount,
                KeyedSignalTable.StrongBudget + 1);
            Assert.AreEqual("Tagged", store.Get("keys", "added"),
                "a tag signal taken before the loop and connected after it still fires");
            Assert.AreEqual("7", store.Get("keys", "attribute"),
                "an attribute signal taken before the loop and connected after it still fires");
        }

        [Test]
        public void Tags_AScriptCreatedTagLongerThan100Characters_IsRefused_ALegacyOneStillWorks()
        {
            // WHY (A3-03): a tag of any length was accepted and copied into the tag store, the
            // service's counts and signal tables, and the saved world. A longer tag a world saved
            // before the limit carries (restored through the C# domain) must stay usable.
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            RbxInstance legacy = roblox.Registry.Create("Folder");
            legacy.Name = "L";
            legacy.Parent = roblox.Registry.WorldRoot;
            string legacyTag = new('L', 150);
            legacy.AddTag(legacyTag);

            stack.Runtime.LoadMod("tags", @"
                local cs = game:GetService('CollectionService')
                local p = Instance.new('Part')
                p.Parent = workspace
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('long', function() p:AddTag(string.rep('x', 101)) end)
                try('serviceLong', function() cs:AddTag(p, string.rep('y', 101)) end)
                try('signalLong', function() cs:GetInstanceAddedSignal(string.rep('z', 101)) end)
                try('limit', function() p:AddTag(string.rep('x', 100)) end)
                local legacy = workspace:FindFirstChild('L')
                try('legacyHas', function() assert(legacy:HasTag(string.rep('L', 150))) end)
                try('legacySignal', function() cs:GetInstanceRemovedSignal(string.rep('L', 150)) end)
                try('legacyRemove', function() legacy:RemoveTag(string.rep('L', 150)) end)");

            foreach (string label in new[] { "long", "serviceLong", "signalLong" })
            {
                string refused = store.Get("tags", label);
                StringAssert.StartsWith("false|", refused, label + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", refused, label);
                StringAssert.Contains("at most 100 characters", refused, label);
            }

            foreach (string label in new[] { "limit", "legacyHas", "legacySignal", "legacyRemove" })
            {
                Assert.AreEqual("true|nil", store.Get("tags", label), label);
            }

            Assert.IsFalse(legacy.HasTag(legacyTag), "the legacy tag was removed by the script");
        }

        // ---- MaterialVariant repaint (A3-08) ------------------------------------------------

        /// <summary>An in-memory sink that counts variant repaints.</summary>
        private sealed class RepaintCountingSink : IPartPropertySink
        {
            private readonly InMemoryPartPropertySink _inner = new();

            public List<string> Refreshed { get; } = new();

            public void SetCFrame(InstanceId id, in RbxCFrame cframe) => _inner.SetCFrame(id, in cframe);

            public void SetPosition(InstanceId id, RbxVector3 position) => _inner.SetPosition(id, position);

            public void SetSize(InstanceId id, RbxVector3 size) => _inner.SetSize(id, size);

            public void SetColor(InstanceId id, RbxColor3 color) => _inner.SetColor(id, color);

            public void SetAnchored(InstanceId id, bool anchored) => _inner.SetAnchored(id, anchored);

            public void SetTransparency(InstanceId id, float transparency) =>
                _inner.SetTransparency(id, transparency);

            public void SetCanCollide(InstanceId id, bool canCollide) => _inner.SetCanCollide(id, canCollide);

            public void SetShape(InstanceId id, RbxPartShape shape) => _inner.SetShape(id, shape);

            public void SetMaterial(InstanceId id, in RbxMaterialId material) =>
                _inner.SetMaterial(id, in material);

            public void SetMaterialVariant(InstanceId id, string variantName) =>
                _inner.SetMaterialVariant(id, variantName);

            public void RefreshMaterialVariant(string variantName)
            {
                Refreshed.Add(variantName);
                _inner.RefreshMaterialVariant(variantName);
            }

            public void SetPartProperties(InstanceId id, in PartProperties properties) =>
                _inner.SetPartProperties(id, in properties);

            public bool TryGetPartProperties(InstanceId id, out PartProperties properties) =>
                _inner.TryGetPartProperties(id, out properties);

            public PartProperties GetPartPropertiesOrDefault(InstanceId id) =>
                _inner.GetPartPropertiesOrDefault(id);

            public void OnPartDestroyed(InstanceId id) => _inner.OnPartDestroyed(id);
        }

        [Test]
        public void MaterialVariant_AnEqualWrite_RepaintsNothing_ARealChangeRepaintsOnce()
        {
            // WHY (A3-08): every MaterialVariant write asked the sink to repaint the parts wearing
            // the variant — a walk over the whole world in the GameObject binder — even when the
            // value was the one the variant already held.
            RepaintCountingSink sink = new();
            LuaCsRbxApiBindings roblox = new(partSink: sink);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("variant", @"
                local v = Instance.new('MaterialVariant')
                v.Name = 'Worn'
                v.Parent = game:GetService('MaterialService')");
            sink.Refreshed.Clear();
            stack.Runtime.LoadMod("variant-equal", @"
                local v = game:GetService('MaterialService'):FindFirstChild('Worn')
                v.BaseMaterial = Enum.Material.Plastic
                v.ColorMap = ''
                v.NormalMap = ''
                v.RoughnessMap = ''
                v.MetalnessMap = ''
                v.StudsPerTile = 1");
            CollectionAssert.IsEmpty(sink.Refreshed, "assigning the values the variant holds repaints nothing");

            stack.Runtime.LoadMod("variant-change", @"
                local v = game:GetService('MaterialService'):FindFirstChild('Worn')
                v.StudsPerTile = 4
                v.StudsPerTile = 4");
            CollectionAssert.AreEqual(new[] { "Worn" }, sink.Refreshed, "one real change, one repaint");
        }

        // ---- Shared world -------------------------------------------------------------------

        [Test]
        public void Lua_TwoMods_ShareOneInstanceWorld()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            stack.Runtime.LoadMod("producer", @"
                local f = Instance.new('Folder', workspace)
                f.Name = 'SharedNode'");
            stack.Runtime.LoadMod("consumer", @"
                local f = workspace:FindFirstChild('SharedNode')
                assert(f ~= nil, 'mods must share one Roblox world')
                assert(f.Name == 'SharedNode')");
            Assert.IsTrue(stack.Runtime.IsLoaded("consumer"));
        }
    }
}
