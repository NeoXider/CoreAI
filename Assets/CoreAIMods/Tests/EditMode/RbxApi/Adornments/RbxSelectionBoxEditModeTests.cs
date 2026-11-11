using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Adornments
{
    /// <summary>
    /// Engine-free <c>SelectionBox</c>: the mirror's defaults, and the reconciler that draws a box around a part or a
    /// model while it is visible, sits in the Workspace or a PlayerGui and adorns something in the Workspace, and
    /// calls its backend only on a change. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxSelectionBoxEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private Dictionary<InstanceId, (RbxCFrame CFrame, RbxVector3 Size)> _parts;
        private FakeBackend _backend;
        private RbxSelectionBoxReconciler _reconciler;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            _parts = new Dictionary<InstanceId, (RbxCFrame, RbxVector3)>();
            _backend = new FakeBackend();
            _reconciler = new RbxSelectionBoxReconciler(_registry, _backend,
                (InstanceId id, out RbxCFrame cframe, out RbxVector3 size) =>
                {
                    bool known = _parts.TryGetValue(id, out (RbxCFrame CFrame, RbxVector3 Size) box);
                    cframe = box.CFrame;
                    size = box.Size;
                    return known;
                });
        }

        [TearDown]
        public void TearDown()
        {
            _reconciler.Dispose();
        }

        private RbxInstance Part(RbxVector3 position, RbxVector3 size, RbxInstance parent = null)
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = parent ?? _registry.WorldRoot;
            _parts[part.Id] = (RbxCFrame.FromPosition(position), size);
            return part;
        }

        private RbxSelectionBox Box(RbxInstance adornee, RbxInstance parent = null)
        {
            RbxSelectionBox box = (RbxSelectionBox)_registry.Create("SelectionBox");
            box.Adornee = adornee;
            box.Parent = parent ?? _registry.WorldRoot;
            return box;
        }

        [Test]
        public void ANewSelectionBox_HasTheMirrorDefaults_AndIsAGuiBase3d()
        {
            RbxSelectionBox box = (RbxSelectionBox)_registry.Create("SelectionBox");

            Assert.AreEqual(0.15f, box.LineThickness, 1e-6f);
            Assert.AreEqual(RbxGuiBase3d.DefaultColor, box.Color3);
            Assert.AreEqual(0f, box.Transparency);
            Assert.AreEqual(1f, box.SurfaceTransparency, "no faces by default");
            Assert.IsTrue(box.Visible);
            Assert.IsNull(box.Adornee);
            Assert.IsTrue(box.IsA("InstanceAdornment"));
            Assert.IsTrue(box.IsA("GuiBase3d"));
            Assert.IsFalse(box.IsA("GuiBase2d"), "a 3D adornment is not on the UI layer");
        }

        [Test]
        public void ABoxAroundAPart_IsDrawnWithThePartsBox_AndOnlyRedrawnOnAChange()
        {
            RbxInstance part = Part(new RbxVector3(4f, 2f, 0f), new RbxVector3(4f, 4f, 4f));
            RbxSelectionBox box = Box(part);

            _reconciler.Tick();
            _reconciler.Tick();

            Assert.AreEqual(1, _backend.Applies, "a steady box is drawn once");
            RbxSelectionBoxRenderState state = _backend.Drawn[box.Id];
            Assert.AreEqual(new RbxVector3(4f, 2f, 0f), state.CFrame.Position);
            Assert.AreEqual(new RbxVector3(4f, 4f, 4f), state.Size);
            Assert.AreEqual(0.15f, state.LineThickness, 1e-6f);

            box.Color3 = new RbxColor3(1f, 0f, 0f);
            _parts[part.Id] = (RbxCFrame.FromPosition(8f, 2f, 0f), new RbxVector3(4f, 4f, 4f));
            _reconciler.Tick();
            Assert.AreEqual(2, _backend.Applies);
            Assert.AreEqual(new RbxVector3(8f, 2f, 0f), _backend.Drawn[box.Id].CFrame.Position, "it follows the part");
            Assert.AreEqual(new RbxColor3(1f, 0f, 0f), _backend.Drawn[box.Id].Color);
        }

        [Test]
        public void Negative_HiddenUnadornedOrOutOfPlace_BoxesAreNotDrawn()
        {
            RbxInstance part = Part(RbxVector3.Zero, new RbxVector3(4f, 4f, 4f));
            RbxSelectionBox hidden = Box(part);
            hidden.Visible = false;
            RbxSelectionBox unadorned = Box(null);
            RbxSelectionBox stored = Box(part, _game.FindFirstChildOfClass("ServerStorage"));
            RbxInstance storedPart = Part(RbxVector3.Zero, new RbxVector3(4f, 4f, 4f),
                _game.FindFirstChildOfClass("ReplicatedStorage"));
            RbxSelectionBox outsideAdornee = Box(storedPart);

            _reconciler.Tick();

            Assert.AreEqual(0, _backend.Drawn.Count);
            hidden.Visible = true;
            _reconciler.Tick();
            Assert.IsTrue(_backend.Drawn.ContainsKey(hidden.Id), "twin: made visible it is drawn");
            Assert.IsFalse(_backend.Drawn.ContainsKey(unadorned.Id));
            Assert.IsFalse(_backend.Drawn.ContainsKey(stored.Id));
            Assert.IsFalse(_backend.Drawn.ContainsKey(outsideAdornee.Id));
        }

        [Test]
        public void ABoxInAPlayerGui_IsDrawn()
        {
            RbxInstance part = Part(RbxVector3.Zero, new RbxVector3(2f, 2f, 2f));
            RbxInstance playerGui = _registry.Create("PlayerGui");
            RbxSelectionBox box = Box(part, playerGui);

            _reconciler.Tick();

            Assert.IsTrue(_backend.Drawn.ContainsKey(box.Id));
        }

        [Test]
        public void DestroyingTheAdorneeOrTheBox_ReleasesTheDrawnBox()
        {
            RbxInstance first = Part(RbxVector3.Zero, new RbxVector3(2f, 2f, 2f));
            RbxInstance second = Part(RbxVector3.Zero, new RbxVector3(2f, 2f, 2f));
            RbxSelectionBox onFirst = Box(first);
            RbxSelectionBox onSecond = Box(second);
            _reconciler.Tick();
            Assert.AreEqual(2, _backend.Drawn.Count);

            first.Destroy();
            onSecond.Destroy();
            _reconciler.Tick();

            Assert.AreEqual(0, _backend.Drawn.Count);
            Assert.IsNull(onFirst.Adornee, "a destroyed adornee reads as nil");
        }

        [Test]
        public void ABoxAroundAModel_CoversEveryPartOfIt()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            Part(new RbxVector3(0f, 0f, 0f), new RbxVector3(2f, 2f, 2f), model);
            Part(new RbxVector3(10f, 4f, 0f), new RbxVector3(2f, 2f, 2f), model);
            RbxSelectionBox box = Box(model);

            _reconciler.Tick();

            RbxSelectionBoxRenderState state = _backend.Drawn[box.Id];
            Assert.AreEqual(new RbxVector3(5f, 2f, 0f), state.CFrame.Position);
            Assert.AreEqual(new RbxVector3(12f, 6f, 2f), state.Size);
        }

        [Test]
        public void Negative_NonFiniteNumbers_AreRefused()
        {
            RbxSelectionBox box = (RbxSelectionBox)_registry.Create("SelectionBox");

            Assert.Throws<RbxError>(() => box.LineThickness = float.NaN);
            Assert.Throws<RbxError>(() => box.Transparency = float.PositiveInfinity);
            Assert.AreEqual(0.15f, box.LineThickness, 1e-6f);
        }

        private sealed class FakeBackend : IRbxSelectionBoxBackend
        {
            public Dictionary<InstanceId, RbxSelectionBoxRenderState> Drawn { get; } = new();

            public int Applies { get; private set; }

            public void Apply(InstanceId boxId, in RbxSelectionBoxRenderState state)
            {
                Applies++;
                Drawn[boxId] = state;
            }

            public void Release(InstanceId boxId)
            {
                Drawn.Remove(boxId);
            }
        }
    }
}
