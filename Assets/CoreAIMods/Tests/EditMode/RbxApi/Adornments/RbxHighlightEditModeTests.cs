using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Adornments
{
    /// <summary>
    /// Engine-free <c>Highlight</c>: the mirror's defaults, the Roblox rule that a nil Adornee highlights the parent, a
    /// model drawn as all of its parts, the 255 limit, and the reconciler that calls its backend only on a change.
    /// Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxHighlightEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private FakeBackend _backend;
        private RbxHighlightReconciler _reconciler;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            _backend = new FakeBackend();
            _reconciler = new RbxHighlightReconciler(_registry, _backend);
        }

        [TearDown]
        public void TearDown()
        {
            _reconciler.Dispose();
        }

        private RbxInstance Part(RbxInstance parent = null)
        {
            RbxInstance part = _registry.Create("Part");
            part.Parent = parent ?? _registry.WorldRoot;
            return part;
        }

        private RbxHighlight Highlight(RbxInstance parent, RbxInstance adornee = null)
        {
            RbxHighlight highlight = (RbxHighlight)_registry.Create("Highlight");
            highlight.Adornee = adornee;
            highlight.Parent = parent;
            return highlight;
        }

        [Test]
        public void ANewHighlight_HasTheMirrorDefaults_AndIsAPlainInstance()
        {
            RbxHighlight highlight = (RbxHighlight)_registry.Create("Highlight");

            Assert.AreEqual(RbxHighlightDepthMode.AlwaysOnTop, highlight.DepthMode);
            Assert.IsTrue(highlight.Enabled);
            Assert.AreEqual(new RbxColor3(1f, 0f, 0f), highlight.FillColor);
            Assert.AreEqual(0.5f, highlight.FillTransparency);
            Assert.AreEqual(new RbxColor3(1f, 1f, 1f), highlight.OutlineColor);
            Assert.AreEqual(0f, highlight.OutlineTransparency);
            Assert.IsNull(highlight.Adornee);
            Assert.AreEqual("Highlight", highlight.ClassName);
            Assert.IsFalse(highlight.IsA("GuiBase3d"), "Highlight derives from Instance in the mirror");
        }

        [Test]
        public void ANilAdornee_HighlightsTheParent_AndAnAdorneeTakesOver()
        {
            RbxInstance parent = Part();
            RbxInstance other = Part();
            RbxHighlight highlight = Highlight(parent);

            _reconciler.Tick();
            Assert.AreEqual(1, _backend.Applied.Count);
            CollectionAssert.AreEqual(new[] { parent.Id.Value }, Ids(_backend.Last(highlight.Id)));

            highlight.Adornee = other;
            _reconciler.Tick();
            CollectionAssert.AreEqual(new[] { other.Id.Value }, Ids(_backend.Last(highlight.Id)),
                "the Adornee wins over the parent");

            highlight.Adornee = null;
            _reconciler.Tick();
            CollectionAssert.AreEqual(new[] { parent.Id.Value }, Ids(_backend.Last(highlight.Id)),
                "nil hands it back to the parent");
        }

        [Test]
        public void AModel_IsDrawnAsAllItsParts_AndAPartAddedLaterJoins()
        {
            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            RbxInstance first = Part(model);
            RbxInstance folder = _registry.Create("Folder");
            folder.Parent = model;
            RbxInstance nested = Part(folder);
            RbxHighlight highlight = Highlight(model);

            _reconciler.Tick();
            CollectionAssert.AreEquivalent(new[] { first.Id.Value, nested.Id.Value }, Ids(_backend.Last(highlight.Id)));

            RbxInstance later = Part(model);
            _reconciler.Tick();
            CollectionAssert.AreEquivalent(new[] { first.Id.Value, nested.Id.Value, later.Id.Value },
                Ids(_backend.Last(highlight.Id)));

            later.Parent = _registry.WorldRoot;
            _reconciler.Tick();
            CollectionAssert.AreEquivalent(new[] { first.Id.Value, nested.Id.Value }, Ids(_backend.Last(highlight.Id)),
                "a part moved out of the model leaves the highlight");
        }

        [Test]
        public void TheBackend_IsCalledOnlyOnAChange()
        {
            RbxHighlight highlight = Highlight(Part());
            _reconciler.Tick();
            _reconciler.Tick();
            Assert.AreEqual(1, _backend.Applied.Count, "an unchanged highlight is not redrawn");

            highlight.FillColor = new RbxColor3(0f, 1f, 0f);
            highlight.FillColor = new RbxColor3(0f, 1f, 0f);
            _reconciler.Tick();
            Assert.AreEqual(2, _backend.Applied.Count);
            Assert.AreEqual(new RbxColor3(0f, 1f, 0f), _backend.Last(highlight.Id).FillColor);

            highlight.DepthMode = RbxHighlightDepthMode.Occluded;
            highlight.OutlineTransparency = 0.25f;
            _reconciler.Tick();
            Assert.AreEqual(3, _backend.Applied.Count);
            Assert.AreEqual(RbxHighlightDepthMode.Occluded, _backend.Last(highlight.Id).DepthMode);
            Assert.AreEqual(0.25f, _backend.Last(highlight.Id).OutlineTransparency);
        }

        [Test]
        public void Disabling_ReleasesTheDraw_AndEnablingDrawsAgain()
        {
            RbxHighlight highlight = Highlight(Part());
            _reconciler.Tick();
            highlight.Enabled = false;
            _reconciler.Tick();
            Assert.AreEqual(1, _backend.Released.Count);
            Assert.AreEqual(0, _reconciler.DrawnCount);

            highlight.Enabled = true;
            _reconciler.Tick();
            Assert.AreEqual(1, _reconciler.DrawnCount);
        }

        [Test]
        public void AHighlightOutsideTheWorkspace_IsNotDrawn_ButOneInAPlayerGuiIs()
        {
            RbxInstance part = Part();
            RbxInstance storage = _game.GetService("ReplicatedStorage");
            Highlight(storage, part);
            Highlight(storage);
            _reconciler.Tick();
            Assert.AreEqual(0, _reconciler.DrawnCount, "neither the stored one nor one whose parent is not in the Workspace");

            RbxInstance playerGui = _registry.Create("PlayerGui");
            playerGui.Parent = storage;
            Highlight(playerGui, part);
            _reconciler.Tick();
            Assert.AreEqual(1, _reconciler.DrawnCount);
        }

        [Test]
        public void DestroyingTheAdornee_ReleasesTheDraw_AndAnEmptyModelDrawsNothing()
        {
            RbxInstance part = Part();
            RbxHighlight highlight = Highlight(_registry.WorldRoot, part);
            _reconciler.Tick();
            Assert.AreEqual(1, _reconciler.DrawnCount);

            part.Destroy();
            _reconciler.Tick();
            Assert.AreEqual(0, _reconciler.DrawnCount);
            Assert.IsNull(highlight.Adornee);

            RbxInstance model = _registry.Create("Model");
            model.Parent = _registry.WorldRoot;
            Highlight(model);
            _reconciler.Tick();
            Assert.AreEqual(0, _reconciler.DrawnCount, "a model with no part has no silhouette");
        }

        [Test]
        public void OnlyTheFirst255Highlights_AreDrawn()
        {
            RbxInstance part = Part();
            List<RbxHighlight> all = new();
            for (int index = 0; index < RbxHighlight.MaxDrawn + 1; index++)
            {
                all.Add(Highlight(_registry.WorldRoot, part));
            }

            _reconciler.Tick();
            Assert.AreEqual(RbxHighlight.MaxDrawn, _reconciler.DrawnCount);
            Assert.IsFalse(_backend.Has(all[RbxHighlight.MaxDrawn].Id), "the 256th is silently ignored");

            all[0].Destroy();
            _reconciler.Tick();
            Assert.AreEqual(RbxHighlight.MaxDrawn, _reconciler.DrawnCount);
            Assert.IsTrue(_backend.Has(all[RbxHighlight.MaxDrawn].Id), "a freed slot goes to the next one");
        }

        [Test]
        public void ANonFiniteTransparency_IsRefused_AndAFiniteOneIsKept()
        {
            RbxHighlight highlight = (RbxHighlight)_registry.Create("Highlight");
            Assert.Throws<RbxError>(() => highlight.FillTransparency = float.NaN);
            Assert.Throws<RbxError>(() => highlight.OutlineTransparency = float.PositiveInfinity);
            Assert.AreEqual(0.5f, highlight.FillTransparency);

            highlight.FillTransparency = 1f;
            Assert.AreEqual(1f, highlight.FillTransparency);
        }

        [Test]
        public void AClone_CarriesEveryProperty()
        {
            RbxInstance part = Part();
            RbxHighlight highlight = Highlight(_registry.WorldRoot, part);
            highlight.DepthMode = RbxHighlightDepthMode.Occluded;
            highlight.Enabled = false;
            highlight.FillColor = new RbxColor3(0f, 0f, 1f);
            highlight.FillTransparency = 0.1f;
            highlight.OutlineColor = new RbxColor3(1f, 1f, 0f);
            highlight.OutlineTransparency = 0.3f;

            RbxHighlight copy = (RbxHighlight)highlight.Clone();

            Assert.AreSame(part, copy.Adornee);
            Assert.AreEqual(RbxHighlightDepthMode.Occluded, copy.DepthMode);
            Assert.IsFalse(copy.Enabled);
            Assert.AreEqual(new RbxColor3(0f, 0f, 1f), copy.FillColor);
            Assert.AreEqual(0.1f, copy.FillTransparency);
            Assert.AreEqual(new RbxColor3(1f, 1f, 0f), copy.OutlineColor);
            Assert.AreEqual(0.3f, copy.OutlineTransparency);
        }

        [Test]
        public void ARestore_SetsTheStateWithoutARevision_AndASetterAdvancesOne()
        {
            RbxInstance part = Part();
            RbxHighlight highlight = Highlight(_registry.WorldRoot);
            Assert.IsTrue(_registry.TryGetRecord(highlight.Id, out InstanceRecord before));
            long revision = before.Revision;

            highlight.RestoreDurableState(part, RbxHighlightDepthMode.Occluded, false, new RbxColor3(0f, 1f, 0f), 0.2f,
                new RbxColor3(0f, 0f, 0f), 0.4f);
            Assert.IsTrue(_registry.TryGetRecord(highlight.Id, out InstanceRecord restored));
            Assert.AreEqual(revision, restored.Revision, "a restore is no change");
            Assert.AreSame(part, highlight.Adornee);
            Assert.AreEqual(0.4f, highlight.OutlineTransparency);

            highlight.Enabled = true;
            Assert.IsTrue(_registry.TryGetRecord(highlight.Id, out InstanceRecord changed));
            Assert.Greater(changed.Revision, revision);
        }

        private static ulong[] Ids(RbxHighlightRenderState state)
        {
            ulong[] ids = new ulong[state.Parts.Count];
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = state.Parts[index].Value;
            }

            return ids;
        }

        private sealed class FakeBackend : IRbxHighlightBackend
        {
            private readonly Dictionary<ulong, RbxHighlightRenderState> _drawn = new();

            public List<RbxHighlightRenderState> Applied { get; } = new();

            public List<InstanceId> Released { get; } = new();

            public void Apply(InstanceId highlightId, in RbxHighlightRenderState state)
            {
                Applied.Add(state);
                _drawn[highlightId.Value] = state;
            }

            public void Release(InstanceId highlightId)
            {
                Released.Add(highlightId);
                _drawn.Remove(highlightId.Value);
            }

            public bool Has(InstanceId id)
            {
                return _drawn.ContainsKey(id.Value);
            }

            public RbxHighlightRenderState Last(InstanceId id)
            {
                Assert.IsTrue(_drawn.TryGetValue(id.Value, out RbxHighlightRenderState state), "highlight is drawn");
                return state;
            }
        }
    }
}
