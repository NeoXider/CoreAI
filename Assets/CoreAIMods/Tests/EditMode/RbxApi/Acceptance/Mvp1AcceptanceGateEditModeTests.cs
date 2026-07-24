using System.Threading;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The MVP1 acceptance gate (ROBLOX_API_ROADMAP.md §5.1.8 headline): "a mod can build,
    /// query, clone, destroy" — driven end-to-end through the Lua surface against the REAL
    /// GameObject binder, with the §3.3 identity invariants (stable ids, authority bit)
    /// asserted on the C# side after every phase.
    /// </summary>
    [TestFixture]
    public sealed class Mvp1AcceptanceGateEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private SynchronizationContext _savedContext;
        private Mvp1AcceptanceWorld _world;

        /// <summary>Same sync-over-async hazard as LuaCsModRuntimeEditModeTests: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new Mvp1AcceptanceWorld();
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        // ---- Build --------------------------------------------------------------------------

        [Test]
        public void Gate_Build_LuaPartMaterializesWithRecordMarkedMaterialized()
        {
            _world.Stack.Runtime.LoadMod("builder", @"
                local p = Instance.new('Part')
                p.Name = 'GatePart'
                p.Position = Vector3.new(10, 5, -4)
                p.Parent = workspace");

            RbxInstance part = _world.Workspace.FindFirstChild("GatePart");
            Assert.IsNotNull(part, "the built Part must be queryable from C#");
            Assert.IsTrue(_world.Registry.TryGetRecord(part.Id, out InstanceRecord record));
            Assert.IsTrue(record.IsMaterialized, "a Part under Workspace must be materialized (D5)");
            Assert.AreEqual("builder", record.OwnerModId);

            GameObject partGo = _world.BoundObject(part);
            Assert.IsTrue(partGo.activeInHierarchy);
            Assert.AreEqual(2.8f, partGo.transform.position.x, Epsilon);
            Assert.AreEqual(1.4f, partGo.transform.position.y, Epsilon);
            Assert.AreEqual(1.12f, partGo.transform.position.z, Epsilon, "mod-space z = -Unity z (D2)");
        }

        // ---- Query --------------------------------------------------------------------------

        [Test]
        public void Gate_Query_LuaNavigationSeesTheSharedTree()
        {
            _world.Stack.Runtime.LoadMod("builder", @"
                local rig = Instance.new('Model')
                rig.Name = 'QueryRig'
                rig.Parent = workspace
                local body = Instance.new('Part')
                body.Name = 'Body'
                body.Parent = rig");

            _world.Stack.Runtime.LoadMod("query", @"
                local rig = workspace:FindFirstChild('QueryRig')
                assert(rig ~= nil, 'FindFirstChild must see content built by another mod')
                local body = rig:FindFirstChildWhichIsA('BasePart')
                assert(body ~= nil and body.Name == 'Body')
                assert(workspace:FindFirstChild('Body', true) == body, 'recursive query')
                assert(body:IsDescendantOf(workspace))
                assert(rig:GetChildren()[1] == body)
                local found = false
                for _, d in ipairs(workspace:GetDescendants()) do
                    if d == body then found = true end
                end
                assert(found, 'GetDescendants must include the part')
                store_set('fullName', body:GetFullName())");

            Assert.AreEqual("Workspace.QueryRig.Body", _world.Store.Get("query", "fullName"));
        }

        // ---- Clone --------------------------------------------------------------------------

        [Test]
        public void Gate_Clone_AllocatesFreshIdsInTheSameAuthoritySpace()
        {
            _world.Stack.Runtime.LoadMod("builder", @"
                local src = Instance.new('Model')
                src.Name = 'CloneSrc'
                src.Parent = workspace
                local part = Instance.new('Part')
                part.Name = 'Limb'
                part.Parent = src
                local copy = src:Clone()
                copy.Name = 'CloneDst'
                copy.Parent = workspace");

            RbxInstance source = _world.Workspace.FindFirstChild("CloneSrc");
            RbxInstance copy = _world.Workspace.FindFirstChild("CloneDst");
            Assert.IsNotNull(source);
            Assert.IsNotNull(copy);
            Assert.AreNotEqual(source.Id, copy.Id, "identity is never cloned (D8)");
            Assert.AreNotEqual(
                source.FindFirstChild("Limb").Id, copy.FindFirstChild("Limb").Id,
                "descendants get fresh ids too");
            Assert.AreEqual(source.Id.IsServerAssigned, copy.Id.IsServerAssigned,
                "a clone stays in its source's authority space (§3.3)");
            Assert.IsTrue(_world.Registry.TryGetRecord(copy.Id, out InstanceRecord record));
            Assert.AreEqual("builder", record.OwnerModId,
                "clone-created content stays attributed to the creating mod");
            Assert.IsTrue(record.IsMaterialized, "the parented clone materializes like any build");
            Assert.IsNotNull(_world.BoundObject(copy.FindFirstChild("Limb")));
        }

        [Test]
        public void Gate_Clone_CopiesPartPropertyValues()
        {
            // WHY: §5.1.8 headline — Clone must deep-copy with IDENTICAL property values.
            // BasePart spatial/visual state lives in the part sink, so a Roblox-faithful clone
            // must carry Size/CFrame/Color/Anchored across, not reset them to defaults.
            _world.Stack.Runtime.LoadMod("builder", @"
                local src = Instance.new('Part')
                src.Name = 'PropSrc'
                src.Parent = workspace
                src.Position = Vector3.new(7, 8, 9)
                src.Size = Vector3.new(2, 4, 6)
                src.Color = Color3.fromRGB(255, 128, 0)
                src.Anchored = true
                local copy = src:Clone()
                copy.Name = 'PropDst'
                copy.Parent = workspace");

            RbxInstance source = _world.Workspace.FindFirstChild("PropSrc");
            RbxInstance copy = _world.Workspace.FindFirstChild("PropDst");
            PartProperties sourceProps = _world.Binder.GetPartPropertiesOrDefault(source.Id);
            PartProperties copyProps = _world.Binder.GetPartPropertiesOrDefault(copy.Id);

            Assert.AreEqual(sourceProps.Size, copyProps.Size,
                "Clone must copy BasePart.Size (Roblox R6.5 deep copy)");
            Assert.AreEqual(sourceProps.CFrame.Position, copyProps.CFrame.Position,
                "Clone must copy BasePart.CFrame");
            Assert.AreEqual(sourceProps.Color, copyProps.Color, "Clone must copy BasePart.Color");
            Assert.AreEqual(sourceProps.Anchored, copyProps.Anchored,
                "Clone must copy BasePart.Anchored");
        }

        // ---- Destroy ------------------------------------------------------------------------

        [Test]
        public void Gate_Destroy_RemovesRecordAndReleasesTheBackingGameObject()
        {
            int baseline = _world.Registry.Count;
            _world.Stack.Runtime.LoadMod("builder", @"
                local rig = Instance.new('Model')
                rig.Name = 'DoomedRig'
                rig.Parent = workspace
                local part = Instance.new('Part')
                part.Name = 'DoomedPart'
                part.Parent = rig");

            RbxInstance rig = _world.Workspace.FindFirstChild("DoomedRig");
            RbxInstance part = rig.FindFirstChild("DoomedPart");
            InstanceId rigId = rig.Id;
            InstanceId partId = part.Id;
            GameObject rigGo = _world.BoundObject(rig);
            GameObject partGo = _world.BoundObject(part);
            Assert.AreEqual(baseline + 2, _world.Registry.Count);

            _world.Stack.Runtime.LoadMod("destroyer",
                "workspace:FindFirstChild('DoomedRig'):Destroy()");

            Assert.IsNull(_world.Workspace.FindFirstChild("DoomedRig"));
            Assert.IsFalse(_world.Registry.TryGetRecord(rigId, out _),
                "Destroy must unregister the record (R6.2 step 5)");
            Assert.IsFalse(_world.Registry.TryGetRecord(partId, out _),
                "Destroy recurses into children");
            Assert.IsFalse(_world.Binder.TryGetBoundObject(rigId, out _));
            Assert.IsFalse(_world.Binder.TryGetBoundObject(partId, out _));
            Assert.IsTrue(rigGo == null && partGo == null,
                "the backing GameObjects must be released (R6.2 step 6)");
            Assert.AreEqual(baseline, _world.Registry.Count,
                "a full build+destroy cycle leaves the registry at its baseline");
        }

        // ---- Identity (§3.3) ----------------------------------------------------------------

        [Test]
        public void Gate_Identity_IdIsStableAcrossReparentAndRename()
        {
            _world.Stack.Runtime.LoadMod("builder", @"
                local f = Instance.new('Folder')
                f.Name = 'Stable'
                f.Parent = workspace");

            RbxInstance folder = _world.Workspace.FindFirstChild("Stable");
            InstanceId id = folder.Id;

            _world.Stack.Runtime.LoadMod("mover", @"
                local f = workspace:FindFirstChild('Stable')
                f.Name = 'StillStable'
                f.Parent = nil
                f.Parent = workspace");

            RbxInstance after = _world.Workspace.FindFirstChild("StillStable");
            Assert.AreSame(folder, after, "same live instance across the moves");
            Assert.AreEqual(id, after.Id, "ids are stable in-session and never reused (§3.3)");
            Assert.IsTrue(_world.Registry.TryGet(id, out RbxInstance byId));
            Assert.AreSame(folder, byId);
        }

        [Test]
        public void Gate_Identity_SoloModeLuaCreationsAreServerAssigned()
        {
            // WHY: in solo the local runtime IS the server (§3.4), so every Lua-created id
            // must sit in the server partition — the wire-marshal guard (§3.3) would reject
            // locally-assigned ids on the MVP11+ spawn path.
            _world.Stack.Runtime.LoadMod("builder", @"
                Instance.new('Folder', workspace).Name = 'AuthA'
                Instance.new('Part', workspace).Name = 'AuthB'");

            foreach (RbxInstance created in _world.Registry.GetOwnedBy("builder"))
            {
                Assert.IsTrue(created.Id.IsServerAssigned,
                    created.Name + " must carry a server-partition id in solo mode");
                Assert.IsFalse(created.Id.IsLocallyAssigned);
                Assert.DoesNotThrow(() => InstanceIdWireContract.EnsureWireSafe(created.Id));
            }
        }
    }
}
