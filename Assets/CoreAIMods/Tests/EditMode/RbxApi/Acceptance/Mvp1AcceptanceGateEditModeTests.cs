using System;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
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

        // ---- Audit follow-ups, binding pass two (MVP1 M1-03/05/07/31, MVP2 M2-10/24) ---------

        [Test]
        public void PartChanged_FiresWithEachChangedPropertyName_AndPositionFollowsACFrameMove()
        {
            // WHY (M1-03): Instance.Changed did not exist on a Part and spatial writes fired no
            // property signal, so `part:GetPropertyChangedSignal("Position")` stayed silent while
            // another script moved the part by CFrame.
            _world.Stack.Runtime.LoadMod("watcher", @"
                local p = Instance.new('Part')
                p.Name = 'Watched'
                p.Anchored = true
                p.Parent = workspace
                local names = {}
                local positionFires = 0
                local transparencyFires = 0
                p.Changed:Connect(function(name)
                    table.insert(names, name)
                    store_set('names', table.concat(names, ','))
                end)
                p:GetPropertyChangedSignal('Position'):Connect(function()
                    positionFires = positionFires + 1
                    store_set('position', tostring(positionFires))
                end)
                p:GetPropertyChangedSignal('Transparency'):Connect(function()
                    transparencyFires = transparencyFires + 1
                    store_set('transparency', tostring(transparencyFires))
                end)
                p.Transparency = 0.5
                p.Transparency = 0.5
                p.CFrame = CFrame.new(1, 2, 3)
                local folder = Instance.new('Folder')
                folder.Changed:Connect(function(name) store_set('folder', name) end)
                folder.Name = 'Renamed'");

            Assert.AreEqual("", _world.Store.Get("watcher", "names"),
                "Changed is deferred: nothing runs before the scheduler resumes");
            _world.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("Transparency,CFrame,Position", _world.Store.Get("watcher", "names"),
                "the written member first, then what it moved; the equal write fires nothing and "
                + "an unrotated move leaves Orientation and Rotation alone");
            Assert.AreEqual("1", _world.Store.Get("watcher", "position"),
                "a CFrame move changes Position, so its property signal fires");
            Assert.AreEqual("1", _world.Store.Get("watcher", "transparency"),
                "assigning the value a property already holds is not a change");
            Assert.AreEqual("Name", _world.Store.Get("watcher", "folder"),
                "every Instance has Changed, not only parts and value objects");
        }

        [Test]
        public void GetPropertyChangedSignal_RefusesAnUnknownName_AcceptsBoundAndCataloguedOnes()
        {
            // WHY (M1-03): any string used to return a signal that never fired, so a typo such as
            // "Positoin" silently did nothing. A real property CoreAI has not bound yet (it is in
            // the known-member catalog) stays a valid name.
            _world.Stack.Runtime.LoadMod("names", @"
                local p = Instance.new('Part')
                local function try(label, target, name)
                    local ok, err = pcall(function() return target:GetPropertyChangedSignal(name) end)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('typo', p, 'Positoin')
                try('event', p, 'Touched')
                try('method', p, 'Destroy')
                try('wrongCase', p, 'position')
                try('bound', p, 'Position')
                try('inherited', p, 'Name')
                try('catalogued', p, 'BrickColor')
                try('camera', workspace.CurrentCamera, 'FieldOfView')
                try('value', Instance.new('IntValue'), 'Value')");

            foreach (string label in new[] { "typo", "event", "method", "wrongCase" })
            {
                string refused = _world.Store.Get("names", label);
                StringAssert.StartsWith("false|", refused, label + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", refused, label);
                StringAssert.Contains("is not a valid property name.", refused, label);
            }

            StringAssert.Contains("Positoin is not a valid property name.",
                _world.Store.Get("names", "typo"));
            foreach (string label in new[] { "bound", "inherited", "catalogued", "camera", "value" })
            {
                StringAssert.StartsWith("true|", _world.Store.Get("names", label),
                    label + " is a real property and must be accepted");
            }
        }

        [Test]
        public void GameIsLoaded_IsTrue_AndTheRobloxLoadingGuardRunsStraightThrough()
        {
            // WHY (M1-05): `if not game:IsLoaded() then game.Loaded:Wait() end` is the first line of
            // countless LocalScripts; it raised a stub, so the whole script never ran.
            _world.Stack.Runtime.LoadMod("loader", @"
                if not game:IsLoaded() then
                    game.Loaded:Wait()
                end
                store_set('loaded', tostring(game:IsLoaded()))
                local connection = game.Loaded:Connect(function() store_set('fired', 'yes') end)
                store_set('connected', tostring(connection.Connected))
                local ok, err = pcall(function() return workspace:IsLoaded() end)
                store_set('workspace', tostring(ok) .. '|' .. tostring(err))");
            _world.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual("true", _world.Store.Get("loader", "loaded"));
            Assert.AreEqual("true", _world.Store.Get("loader", "connected"));
            Assert.AreEqual("", _world.Store.Get("loader", "fired"),
                "the world loaded before the mod ran, so Loaded never fires for it");
            string workspace = _world.Store.Get("loader", "workspace");
            StringAssert.StartsWith("false|", workspace);
            StringAssert.Contains("IsLoaded is not a valid member of Workspace", workspace,
                "IsLoaded is declared on DataModel only");
        }

        [Test]
        public void ModelMoveTo_IsTheLoudStub_WhileHumanoidMoveToStaysBound()
        {
            // WHY (M1-05, M1-31): Model:MoveTo is real Roblox API CoreAI does not implement; it must
            // name itself as a stub rather than borrow Humanoid:MoveTo or read as a typo, and the
            // Humanoid's own MoveTo must keep resolving next to it.
            _world.Stack.Runtime.LoadMod("movers", @"
                local m = Instance.new('Model')
                m.Parent = workspace
                local h = Instance.new('Humanoid')
                local ok, err = pcall(function() m:MoveTo(Vector3.new(1, 2, 3)) end)
                store_set('model', tostring(ok) .. '|' .. tostring(err))
                store_set('humanoid', type(h.MoveTo))
                local folderOk, folderErr = pcall(function() return Instance.new('Folder').MoveTo end)
                store_set('folder', tostring(folderOk) .. '|' .. tostring(folderErr))");

            string model = _world.Store.Get("movers", "model");
            StringAssert.StartsWith("false|", model);
            StringAssert.Contains("NOT_IMPLEMENTED", model);
            StringAssert.Contains("Model:MoveTo", model);
            Assert.AreEqual("function", _world.Store.Get("movers", "humanoid"));
            string folder = _world.Store.Get("movers", "folder");
            StringAssert.Contains("MoveTo is not a valid member of Folder", folder);
            StringAssert.DoesNotContain("NOT_IMPLEMENTED", folder);
        }

        [Test]
        public void MethodTable_SameNameOnTwoClasses_BothResolve_NearestDeclarationWins()
        {
            // WHY (M1-31): the method table was keyed by name alone, so a second MoveTo (Model next
            // to Humanoid) silently replaced the first and broke every character script.
            InstanceRegistry registry = new();
            LuaCsRbxMethodTable table = new(registry.Catalog);
            table.Add("MoveTo", "humanoid-move", "Humanoid");
            table.Add("MoveTo", "model-move", "Model");
            table.Add("GetPivot", "pvinstance-pivot", "PVInstance");
            table.Add("GetPivot", "model-pivot", "Model");
            table.Add("GetFullName", "instance-name", null);
            RbxInstance humanoid = registry.Create("Humanoid");
            RbxInstance model = registry.Create("Model");
            RbxInstance part = registry.Create("Part");
            RbxInstance folder = registry.Create("Folder");

            Assert.AreEqual("humanoid-move", Resolve(table, humanoid, "MoveTo"));
            Assert.AreEqual("model-move", Resolve(table, model, "MoveTo"));
            Assert.IsNull(Resolve(table, part, "MoveTo"), "a Part declares no MoveTo");
            Assert.AreEqual("model-pivot", Resolve(table, model, "GetPivot"),
                "the nearest declaring class wins over an ancestor's");
            Assert.AreEqual("pvinstance-pivot", Resolve(table, part, "GetPivot"));
            Assert.IsNull(Resolve(table, folder, "GetPivot"), "a Folder is not a PVInstance");
            Assert.AreEqual("instance-name", Resolve(table, folder, "GetFullName"));
            Assert.AreEqual(5, table.Count);
            Assert.Throws<InvalidOperationException>(
                () => table.Add("MoveTo", "second-model-move", "Model"),
                "binding one name twice on one class is a build error, never a silent replace");
        }

        [Test]
        public void MethodArgumentErrors_NumberArgumentsWithoutSelf_PropertyWritesNameTheProperty()
        {
            // WHY (M1-07): readers named `index + 1`, counting self, so `p:SetAttribute(5, true)`
            // blamed argument 2 — the value — and a property write named an argument it has none of.
            _world.Stack.Runtime.LoadMod("positions", @"
                local p = Instance.new('Part')
                p.Parent = workspace
                local tags = game:GetService('CollectionService')
                local function try(label, action)
                    local ok, err = pcall(action)
                    store_set(label, tostring(ok) .. '|' .. tostring(err))
                end
                try('findFirstChild', function() return p:FindFirstChild(5) end)
                try('setAttribute', function() p:SetAttribute(5, true) end)
                try('addTagInstance', function() tags:AddTag(5, 'x') end)
                try('addTagName', function() tags:AddTag(p, 5) end)
                try('bindToClose', function() game:BindToClose('later') end)
                try('debris', function() game:GetService('Debris'):AddItem(p, 'soon') end)
                try('name', function() p.Name = 5 end)
                try('position', function() p.Position = 'up' end)");

            (string Label, string Expected)[] cases =
            {
                ("findFirstChild", "Instance:FindFirstChild expects a string at argument 1"),
                ("setAttribute", "Instance:SetAttribute expects a string at argument 1"),
                ("addTagInstance", "CollectionService:AddTag expects an Instance at argument 1"),
                ("addTagName", "CollectionService:AddTag expects a string at argument 2"),
                ("bindToClose", "game:BindToClose expects a function at argument 1"),
                ("debris", "Debris:AddItem expects a number at argument 2"),
                ("name", "Part.Name expects a string, got number"),
                ("position", "Part.Position expects a Vector3, got string")
            };
            foreach ((string label, string expected) in cases)
            {
                string failure = _world.Store.Get("positions", label);
                StringAssert.StartsWith("false|", failure, label);
                StringAssert.Contains("BAD_ARGUMENT", failure, label);
                StringAssert.Contains(expected, failure, label);
            }

            StringAssert.DoesNotContain("argument", _world.Store.Get("positions", "name"),
                "a property write has no argument list to point into");
            StringAssert.DoesNotContain("at argument 2", _world.Store.Get("positions", "findFirstChild"),
                "the old reader counted self and blamed argument 2");
        }

        [Test]
        public void Clone_CopiesPartStatePastASkippedServiceChild_AndDownADeepChain()
        {
            // WHY: Clone skips a service below the cloned root, but the part-sink copy walk did
            // not, so every later sibling was paired with the wrong copy and kept default state; the
            // walk was also recursive over a tree as deep as the snapshot cap. The headless sink
            // here is separate from the registry's binder, so this walk is the only copy path.
            using HeadlessWorld headless = new HeadlessWorld();
            InstanceRegistry registry = headless.Registry;
            IPartPropertySink sink = headless.Bindings.PartSink;
            RbxInstance rig = registry.Create("Model");
            rig.Name = "Rig";
            rig.Parent = registry.WorldRoot;
            RbxInstance service = registry.Create("ServerStorage");
            service.Parent = rig;
            RbxInstance limb = registry.Create("Part");
            limb.Name = "Limb";
            limb.Parent = rig;
            sink.SetPosition(limb.Id, new RbxVector3(7f, 8f, 9f));
            RbxInstance chainParent = limb;
            for (int depth = 0; depth < 64; depth++)
            {
                RbxInstance link = registry.Create("Folder");
                link.Name = "Link";
                link.Parent = chainParent;
                chainParent = link;
            }

            RbxInstance tip = registry.Create("Part");
            tip.Name = "Tip";
            tip.Parent = chainParent;
            sink.SetPosition(tip.Id, new RbxVector3(1f, 2f, 3f));

            headless.Stack.Runtime.LoadMod("cloner", @"
                local copy = workspace.Rig:Clone()
                copy.Name = 'RigCopy'
                copy.Parent = workspace", persistToStore: false);

            RbxInstance copy = registry.WorldRoot.FindFirstChild("RigCopy");
            Assert.IsNotNull(copy);
            Assert.IsNull(copy.FindFirstChildOfClass("ServerStorage"),
                "precondition: Clone skips the service child");
            RbxInstance limbCopy = copy.FindFirstChild("Limb");
            Assert.IsNotNull(limbCopy);
            AssertPosition(new RbxVector3(7f, 8f, 9f),
                sink.GetPartPropertiesOrDefault(limbCopy.Id).Position,
                "the part after the skipped service keeps its own state");
            RbxInstance tipCopy = copy.FindFirstChild("Tip", true);
            Assert.IsNotNull(tipCopy);
            Assert.AreNotEqual(tip.Id, tipCopy.Id);
            AssertPosition(new RbxVector3(1f, 2f, 3f),
                sink.GetPartPropertiesOrDefault(tipCopy.Id).Position,
                "the deepest part of the chain is paired with its own copy");
        }

        [Test]
        public void ResumeActor_IsCachedPerMod_AndNeverFallsBackToTheHostForAReleasedActor()
        {
            // WHY (M2-24): the disconnect seam releases an actor's attribution but leaves its mods
            // loaded, and resolving their actor then fell back to the HOST, opening a host envelope
            // around that actor's code. WHY (M2-10): every resolve built a new identity provider
            // with a fresh GUID session, two strings and a provider per scheduler resume.
            using HeadlessWorld headless = new HeadlessWorld();
            ActorContext actor = new LocalActorIdentityProvider(
                    "resume-actor", "session-resume-actor", headless.Registry.WorldId,
                    ActorGrantSet.None, AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            headless.Stack.Runtime.LoadMod(actor, "actor-mod", "store_set('loaded', 'actor')",
                persistToStore: false);
            Assert.AreEqual("actor", headless.Store.Get("actor-mod", "loaded"));

            ActorContext first = headless.Bindings.ResolveOwnerActorContext("actor-mod");
            ActorContext second = headless.Bindings.ResolveOwnerActorContext("actor-mod");
            Assert.AreEqual("resume-actor", first.ActorId);
            Assert.IsFalse(first.Grants.IsUnrestricted);
            Assert.AreEqual(first.SessionId, second.SessionId,
                "the resume context is built once per attributed actor, not once per resume");

            headless.Registry.BindActorAttribution("actor-mod", OriginTag.FromMod("actor-mod"),
                "other-actor");
            Assert.AreEqual("other-actor",
                headless.Bindings.ResolveOwnerActorContext("actor-mod").ActorId,
                "a re-attributed mod resolves to its new actor, never a stale cached one");

            headless.Registry.ClearActorAttribution("actor-mod", OriginTag.FromMod("actor-mod"));
            RbxError refusal = Assert.Throws<RbxError>(
                () => headless.Bindings.ResolveOwnerActorContext("actor-mod"));
            Assert.AreEqual(RbxErrorCode.NotAuthority, refusal.Code);
            StringAssert.Contains("resume-actor", refusal.Message);

            Assert.IsTrue(headless.Stack.Runtime.UnloadMod("actor-mod"));
            headless.Stack.Runtime.LoadMod("actor-mod", "store_set('loaded', 'host')",
                persistToStore: false);
            Assert.AreEqual("host", headless.Store.Get("actor-mod", "loaded"));
            Assert.IsTrue(
                headless.Bindings.ResolveOwnerActorContext("actor-mod").Grants.IsUnrestricted,
                "a new host load of the mod is the host's again");

            headless.Stack.Runtime.LoadMod("host-mod", "store_set('loaded', 'host')",
                persistToStore: false);
            Assert.IsTrue(
                headless.Bindings.ResolveOwnerActorContext("host-mod").Grants.IsUnrestricted,
                "a mod the host loaded keeps resolving to the host");
        }

        [Test]
        public void LoudStubWorkarounds_NeverOfferCanCollideAsATouchOrQueryFilter()
        {
            // WHY: since BINDER-A a CanCollide = false part still fires Touched and is still hit by
            // raycasts, so "use CanCollide" was advice that silently does not work for CanTouch,
            // CanQuery and collision groups.
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            foreach (string member in new[] { "CanQuery", "CanTouch", "CollisionGroup" })
            {
                Assert.IsTrue(catalog.TryGetKnownUnimplementedMember("Part", member,
                    RbxKnownUnimplementedMemberAccess.Read, out _,
                    out RbxKnownUnimplementedMemberDescriptor descriptor), member);
                AssertHonestCanCollideAdvice(member, descriptor.Workaround);
            }

            Assert.IsTrue(catalog.TryGetKnownUnimplementedMember("Workspace",
                "RegisterCollisionGroup", RbxKnownUnimplementedMemberAccess.Read, out _,
                out RbxKnownUnimplementedMemberDescriptor groups));
            AssertHonestCanCollideAdvice("RegisterCollisionGroup", groups.Workaround);
            RbxStubService physics =
                (RbxStubService)ServiceCatalog.CreateMvp2().GetService("PhysicsService");
            AssertHonestCanCollideAdvice("PhysicsService", physics.WorkaroundHint);
        }

        private static void AssertHonestCanCollideAdvice(string subject, string workaround)
        {
            StringAssert.DoesNotStartWith("use CanCollide", workaround, subject);
            if (workaround.IndexOf("CanCollide", StringComparison.Ordinal) >= 0)
            {
                StringAssert.Contains("Touched", workaround,
                    subject + ": CanCollide advice must say the part still fires Touched");
            }
        }

        /// <summary>
        /// A world with no scene: the part sink is separate from the registry's binder, and the
        /// headless defaults spawn nothing into a Unity scene.
        /// </summary>
        private sealed class HeadlessWorld : IDisposable
        {
            public HeadlessWorld()
            {
                Registry = new InstanceRegistry(worldId: "mvp1-headless");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game);
                Store = new Mvp1AcceptanceMemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new Mvp1AcceptanceNullLogger(),
                    ModStore = Store,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public Mvp1AcceptanceMemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public void Dispose()
            {
                Bindings.Dispose();
            }
        }

        private static string Resolve(LuaCsRbxMethodTable table, RbxInstance instance, string name)
        {
            return table.TryResolve(instance, name, out LuaValue value)
                ? value.Read<string>()
                : null;
        }

        private static void AssertPosition(RbxVector3 expected, RbxVector3 actual, string message)
        {
            Assert.AreEqual(expected.X, actual.X, Epsilon, message + " (X)");
            Assert.AreEqual(expected.Y, actual.Y, Epsilon, message + " (Y)");
            Assert.AreEqual(expected.Z, actual.Z, Epsilon, message + " (Z)");
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
