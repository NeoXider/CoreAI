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
            int baseline = _world.Registry.AuthoredCount;
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
            Assert.AreEqual(baseline + 2, _world.Registry.AuthoredCount);

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
            Assert.AreEqual(baseline, _world.Registry.AuthoredCount,
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

        // ---- Audit follow-ups (MVP1 M1-17/19/20/22/33) --------------------------------------

        [Test]
        public void PivotTo_OnPart_MovesDescendantPartsAndModels()
        {
            // WHY (M1-17): the mirror's PVInstance:PivotTo "transforms the PVInstance along with all
            // of its descendant PVInstances"; a BasePart root used to move alone and leave its
            // children behind.
            _world.Stack.Runtime.LoadMod("pivoter", @"
                local handle = Instance.new('Part')
                handle.Name = 'Handle'
                handle.Anchored = true
                handle.Position = Vector3.new(0, 0, 0)
                handle.Parent = workspace
                local blade = Instance.new('Part')
                blade.Name = 'Blade'
                blade.Anchored = true
                blade.Position = Vector3.new(0, 3, 0)
                blade.Parent = handle
                local guard = Instance.new('Model')
                guard.Name = 'Guard'
                guard.Parent = handle
                local crossbar = Instance.new('Part')
                crossbar.Name = 'Crossbar'
                crossbar.Anchored = true
                crossbar.Position = Vector3.new(1, 1, 0)
                crossbar.Parent = guard
                handle:PivotTo(CFrame.new(10, 0, 0))");

            RbxInstance handle = _world.Workspace.FindFirstChild("Handle");
            Assert.IsNotNull(handle);
            AssertStoredPosition(new RbxVector3(10f, 0f, 0f), handle, "the pivoted part");
            AssertStoredPosition(new RbxVector3(10f, 3f, 0f), handle.FindFirstChild("Blade"),
                "a child part moves rigidly with the pivot");
            AssertStoredPosition(new RbxVector3(11f, 1f, 0f),
                handle.FindFirstChild("Guard").FindFirstChild("Crossbar"),
                "a part inside a descendant Model moves too");
        }

        [Test]
        public void BooleanProperties_RefuseNonBooleanAssignments()
        {
            // WHY (M1-19): Lua truthiness turned `Anchored = "false"` into true and `= nil` into
            // false without a word; Roblox refuses both.
            _world.Stack.Runtime.LoadMod("booleans", @"
                local p = Instance.new('Part')
                p.Name = 'BoolPart'
                p.Anchored = true
                p.Parent = workspace
                store_set('canCollideBefore', tostring(p.CanCollide))
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('anchored', function() p.Anchored = 'false' end)
                try('cancollide', function() p.CanCollide = nil end)
                try('archivable', function() p.Archivable = 0 end)
                store_set('anchoredAfter', tostring(p.Anchored))
                store_set('canCollideAfter', tostring(p.CanCollide))
                store_set('archivableAfter', tostring(p.Archivable))
                p.Anchored = false
                store_set('anchoredFalse', tostring(p.Anchored))");

            foreach (string label in new[] { "anchored", "cancollide", "archivable" })
            {
                string result = _world.Store.Get("booleans", label);
                StringAssert.StartsWith("false|", result, label + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", result, label);
                StringAssert.Contains("expects a boolean", result, label);
            }

            Assert.AreEqual("true", _world.Store.Get("booleans", "anchoredAfter"));
            Assert.AreEqual(_world.Store.Get("booleans", "canCollideBefore"),
                _world.Store.Get("booleans", "canCollideAfter"));
            Assert.AreEqual("true", _world.Store.Get("booleans", "archivableAfter"));
            Assert.AreEqual("false", _world.Store.Get("booleans", "anchoredFalse"),
                "a real boolean still assigns");
        }

        [Test]
        public void PartSize_ClampsEachAxisToTheMirrorRange()
        {
            // WHY (M1-20): the mirror bounds each Size axis to [0.001, 2048]; negative and zero
            // sizes reached the engine as mirrored meshes and zero-size colliders.
            _world.Stack.Runtime.LoadMod("sizer", @"
                local wild = Instance.new('Part')
                wild.Name = 'WildSize'
                wild.Anchored = true
                wild.Size = Vector3.new(-2, 0, 1e9)
                wild.Parent = workspace
                local fine = Instance.new('Part')
                fine.Name = 'FineSize'
                fine.Anchored = true
                fine.Size = Vector3.new(0.0005, 4, 2048)
                fine.Parent = workspace");

            RbxVector3 wild = _world.Binder.GetPartPropertiesOrDefault(
                _world.Workspace.FindFirstChild("WildSize").Id).Size;
            Assert.AreEqual(0.001f, wild.X, 1e-7f);
            Assert.AreEqual(0.001f, wild.Y, 1e-7f);
            Assert.AreEqual(2048f, wild.Z, 1e-3f);
            RbxVector3 fine = _world.Binder.GetPartPropertiesOrDefault(
                _world.Workspace.FindFirstChild("FineSize").Id).Size;
            Assert.AreEqual(0.001f, fine.X, 1e-7f);
            Assert.AreEqual(4f, fine.Y, Epsilon, "an in-range axis is kept");
            Assert.AreEqual(2048f, fine.Z, 1e-3f, "the upper bound itself is in range");
        }

        [Test]
        public void PartSpatialWrites_RefuseNonFiniteValues()
        {
            // WHY (M1-20): a NaN pose is refused by the engine while the registry kept answering
            // it, so the rendered part and `part.Position` disagreed from then on.
            _world.Stack.Runtime.LoadMod("finite", @"
                local p = Instance.new('Part')
                p.Name = 'FinitePart'
                p.Anchored = true
                p.Position = Vector3.new(1, 2, 3)
                p.Size = Vector3.new(4, 5, 6)
                p.Parent = workspace
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('position', function() p.Position = Vector3.new(0/0, 0, 0) end)
                try('size', function() p.Size = Vector3.new(math.huge, 1, 1) end)
                try('cframe', function() p.CFrame = CFrame.new(math.huge, 0, 0) end)
                try('orientation', function() p.Orientation = Vector3.new(0/0, 0, 0) end)
                try('pivot', function() p:PivotTo(CFrame.new(0/0, 0, 0)) end)");

            foreach (string label in new[] { "position", "size", "cframe", "orientation", "pivot" })
            {
                string result = _world.Store.Get("finite", label);
                StringAssert.StartsWith("false|", result, label + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", result, label);
                StringAssert.Contains("finite", result, label);
            }

            RbxInstance part = _world.Workspace.FindFirstChild("FinitePart");
            AssertStoredPosition(new RbxVector3(1f, 2f, 3f), part, "refused writes change nothing");
            RbxVector3 size = _world.Binder.GetPartPropertiesOrDefault(part.Id).Size;
            Assert.AreEqual(4f, size.X, Epsilon);
            Assert.AreEqual(5f, size.Y, Epsilon);
            Assert.AreEqual(6f, size.Z, Epsilon);
        }

        [Test]
        public void PartCFrameWrite_OrthonormalizesAScaledRotation_AndKeepsAnOrthonormalOne()
        {
            // WHY (M1-20): the mirror's BasePart.CFrame "automatically applies orthonormalization";
            // a scaled matrix used to be stored as is and read back with RightVector.Magnitude 2.
            _world.Stack.Runtime.LoadMod("orthonormal", @"
                local scaled = Instance.new('Part')
                scaled.Name = 'ScaledCFrame'
                scaled.Anchored = true
                scaled.Parent = workspace
                scaled.CFrame = CFrame.fromMatrix(
                    Vector3.new(1, 2, 3), Vector3.new(2, 0, 0), Vector3.new(0, 2, 0))
                local rotated = Instance.new('Part')
                rotated.Name = 'RotatedCFrame'
                rotated.Anchored = true
                rotated.Parent = workspace
                rotated.CFrame = CFrame.new(4, 5, 6) * CFrame.Angles(0, 0.5, 0)");

            RbxCFrame scaled = _world.Binder.GetPartPropertiesOrDefault(
                _world.Workspace.FindFirstChild("ScaledCFrame").Id).CFrame;
            Assert.AreEqual(1f, scaled.XVector.Magnitude, Epsilon, "RightVector is unit length");
            Assert.AreEqual(1f, scaled.YVector.Magnitude, Epsilon, "UpVector is unit length");
            Assert.AreEqual(1f, scaled.ZVector.Magnitude, Epsilon);
            Assert.AreEqual(0f, scaled.XVector.Dot(scaled.YVector), Epsilon);
            Assert.AreEqual(1f, scaled.Position.X, Epsilon, "orthonormalizing keeps the position");
            Assert.AreEqual(2f, scaled.Position.Y, Epsilon);
            Assert.AreEqual(3f, scaled.Position.Z, Epsilon);

            RbxCFrame expected = RbxCFrame.FromPosition(new RbxVector3(4f, 5f, 6f))
                                 * RbxCFrame.Angles(0f, 0.5f, 0f);
            RbxCFrame rotated = _world.Binder.GetPartPropertiesOrDefault(
                _world.Workspace.FindFirstChild("RotatedCFrame").Id).CFrame;
            float[] expectedComponents = expected.GetComponents();
            float[] rotatedComponents = rotated.GetComponents();
            for (int index = 0; index < expectedComponents.Length; index++)
            {
                Assert.AreEqual(expectedComponents[index], rotatedComponents[index], Epsilon,
                    "an already orthonormal CFrame is kept, component " + index);
            }
        }

        [Test]
        public void GameClone_ReturnsNil_AndCreatesNoSecondDataModel()
        {
            // WHY (M1-22): the DataModel is not a service, so the singleton guard missed it and
            // game:Clone() duplicated the whole world, every service included.
            _world.Stack.Runtime.LoadMod("cloner",
                "store_set('isNil', tostring(game:Clone() == nil))");

            Assert.AreEqual("true", _world.Store.Get("cloner", "isNil"));
            Assert.AreEqual(1, CountLive("DataModel"), "no second DataModel was registered");
            Assert.AreEqual(1, CountLive("Workspace"), "no second Workspace was registered");
        }

        [Test]
        public void LegacyWorld_GameDestroy_IsRefused()
        {
            // WHY (M1-22): with the ACL off, only the singleton guard stands between a script and
            // game:Destroy(), and the DataModel was not on its list.
            Assert.IsFalse(_world.Registry.IsWorldAclEnabled, "this gate runs a legacy world");
            _world.Stack.Runtime.LoadMod("destroyer-of-worlds", @"
                local ok, err = pcall(function() game:Destroy() end)
                store_set('result', tostring(ok) .. '|' .. tostring(err))");

            string result = _world.Store.Get("destroyer-of-worlds", "result");
            StringAssert.StartsWith("false|", result);
            StringAssert.Contains("singleton", result);
            Assert.IsFalse(_world.Game.IsDestroyed);
            Assert.IsFalse(_world.Workspace.IsDestroyed);
        }

        [Test]
        public void ReadOnlyMembers_RaiseTheReadOnlyError_NotAnUnknownMember()
        {
            // WHY (M1-33): `part.ClassName = "X"` answered "ClassName is not a valid member", which
            // reads like a typo rather than the read-only property it is.
            _world.Stack.Runtime.LoadMod("readonly", @"
                local p = Instance.new('Part')
                p.Name = 'ReadOnlyPart'
                p.Parent = workspace
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('classname', function() p.ClassName = 'WedgePart' end)
                try('unknown', function() p.NotARealMember = 1 end)
                try('camera', function() workspace.CurrentCamera = workspace.CurrentCamera end)");

            string className = _world.Store.Get("readonly", "classname");
            StringAssert.StartsWith("false|", className);
            StringAssert.Contains("Unable to assign property ClassName. Property is read only",
                className);
            StringAssert.DoesNotContain("not a valid member", className);
            string unknown = _world.Store.Get("readonly", "unknown");
            StringAssert.StartsWith("false|", unknown);
            StringAssert.Contains("not a valid member", unknown,
                "a member that does not exist still says so");
            string camera = _world.Store.Get("readonly", "camera");
            StringAssert.StartsWith("false|", camera);
            StringAssert.Contains("NOT_IMPLEMENTED", camera,
                "CurrentCamera is writable in Roblox, so its assignment is a loud stub");
            Assert.AreEqual("Part", _world.Workspace.FindFirstChild("ReadOnlyPart").ClassName);
        }

        private void AssertStoredPosition(RbxVector3 expected, RbxInstance part, string message)
        {
            Assert.IsNotNull(part, message);
            RbxVector3 actual = _world.Binder.GetPartPropertiesOrDefault(part.Id).CFrame.Position;
            Assert.AreEqual(expected.X, actual.X, Epsilon, message + " (X)");
            Assert.AreEqual(expected.Y, actual.Y, Epsilon, message + " (Y)");
            Assert.AreEqual(expected.Z, actual.Z, Epsilon, message + " (Z)");
        }

        private int CountLive(string className)
        {
            int count = 0;
            foreach (RbxInstance instance in _world.Registry.GetLiveInstances())
            {
                if (instance.ClassName == className)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
