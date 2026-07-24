using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// End-to-end proof of the Roblox MVP1 Lua surface (roadmap §5.1.3) through the REAL mod
    /// runtime: corpus-style snippets loaded via <see cref="LuaCsModRuntimeFactory"/> exercising
    /// datatype constructors/operators, Enum access, Instance.new over the registry whitelist,
    /// game/workspace navigation, §5.2.7 error texts, ownership/origin attribution, and
    /// capability gating. Test names cite rule ids where one applies (§6.6).
    /// </summary>
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
            // WHY: KeyCode shipped with the MVP1 input slice; EasingStyle stays unimplemented
            // until TweenService (MVP8), so it is the loud-stub probe now.
            Exception ex = LoadFails(stack, "m", "local k = Enum.EasingStyle");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("Enum.EasingStyle", FullText(ex));
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

        // ---- Instance surface ---------------------------------------------------------------

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
        public void Lua_GetService_PlannedService_RaisesPhaseNamingStub()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            // WHY: RunService is implemented now; HttpService is still an MVP2-gated planned service.
            Exception ex = LoadFails(stack, "m", "game:GetService('HttpService')");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("MVP2", FullText(ex));
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
        public void Lua_CapabilityGating_ReadTierHasNoInstanceNewAndCannotMutate()
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCapabilities readOnly =
                LuaCapabilities.Read | LuaCapabilities.Gameplay | LuaCapabilities.LogicOverride;
            LuaCsModStack stack = BuildStack(roblox, caps: readOnly);

            stack.Runtime.LoadMod("reader", @"
                assert(Instance == nil, 'Instance.new must be absent without WorldEdit')
                assert(workspace.ClassName == 'Workspace', 'navigation stays available on Read tier')");

            Exception ex = LoadFails(stack, "writer", "workspace.Name = 'Hacked'");
            StringAssert.Contains("WorldEdit", FullText(ex));
            Assert.AreEqual("Workspace", roblox.Game.FindFirstChildOfClass("Workspace").Name);
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
        public void Lua_BasePartUnwiredProperty_RaisesLoudStub()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", @"
                local p = Instance.new('Part')
                p.Material = Enum.Material.Wood");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("BasePart.Material", FullText(ex));
        }

        [Test]
        public void Lua_BasePartPosition_RoundTripsThroughBinder_NoScaleOrChiralityDistortion()
        {
            // WHY: the golden — a Lua Position write must survive Roblox→Unity→(read) with no
            // double-conversion: the GameObject lands at the 0.28-scaled, Z-mirrored pose while the
            // Lua/registry side keeps pure Roblox-space studs (mirrors PositionGolden in the binder
            // tests, driven end-to-end through the Lua surface).
            RbxSpace.ResetForTests(0.28f);
            var root = new GameObject("GoldenRoot");
            try
            {
                var binder = new InstanceGameObjectBinder(root.transform);
                var registry = new InstanceRegistry(null, binder);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                var roblox = new LuaCsRbxApiBindings(registry, game, partSink: binder);
                LuaCsModStack stack = BuildStack(roblox);

                RbxInstance part = registry.Create("Part");
                part.Name = "Golden";
                part.Parent = registry.WorldRoot;

                stack.Runtime.LoadMod("m", @"
                    local p = workspace:FindFirstChild('Golden')
                    p.Position = Vector3.new(10, 5, -4)
                    assert(p.Position == Vector3.new(10, 5, -4), 'Lua must read pure Roblox studs')");

                PartProperties props = binder.GetPartPropertiesOrDefault(part.Id);
                Assert.AreEqual(10f, props.Position.X, 1e-4f);
                Assert.AreEqual(5f, props.Position.Y, 1e-4f);
                Assert.AreEqual(-4f, props.Position.Z, 1e-4f);

                Assert.IsTrue(binder.TryGetBoundObject(part.Id, out GameObject go));
                Assert.AreEqual(2.8f, go.transform.position.x, 1e-4f);
                Assert.AreEqual(1.4f, go.transform.position.y, 1e-4f);
                Assert.AreEqual(1.12f, go.transform.position.z, 1e-4f, "mod-space z = -Unity z (D2)");

                game.Destroy();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                RbxSpace.ResetForTests();
            }
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
        public void Lua_SignalConnect_LoudStubNamesMvp2()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m",
                "workspace.ChildAdded:Connect(function() end)");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("MVP2", FullText(ex));
        }

        [Test]
        public void Lua_TaskWait_LoudStubNamesMvp2_AndParallelSwitchesAreNoOps()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            stack.Runtime.LoadMod("noop", @"
                task.synchronize()
                task.desynchronize()");
            Assert.IsTrue(stack.Runtime.IsLoaded("noop"), "DEV-5: parallel switches must be no-ops");

            Exception ex = LoadFails(stack, "waiter", "task.wait(1)");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("MVP2", FullText(ex));
        }

        [Test]
        public void Lua_WaitForChild_AbsentChild_LoudStubNamesMvp2()
        {
            LuaCsModStack stack = BuildStack(new LuaCsRbxApiBindings());
            Exception ex = LoadFails(stack, "m", "workspace:WaitForChild('NeverThere')");
            StringAssert.Contains("NOT_IMPLEMENTED", FullText(ex));
            StringAssert.Contains("MVP2", FullText(ex));
            StringAssert.Contains("FindFirstChild", FullText(ex));
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
