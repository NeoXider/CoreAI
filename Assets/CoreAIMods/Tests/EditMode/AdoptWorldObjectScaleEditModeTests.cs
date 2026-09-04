using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// AdoptWorldObject reads initial Part state through the inverse RbxSpace boundary, so the
    /// adopted Size must match the part the user actually sees — including under a Size-scaled
    /// ancestor, where localScale omits the parent factor.
    /// </summary>
    public sealed class AdoptWorldObjectScaleEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private readonly List<GameObject> _created = new();
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            // WHY: A non-1:1 scale is the only regime where a metres/studs leak is observable.
            RbxSpace.ResetForTests(0.5f);
            _root = new GameObject("AdoptScaleTestRoot");
            _created.Add(_root);
            _binder = new InstanceGameObjectBinder(_root.transform);
            _registry = new InstanceRegistry(null, _binder);
            _game = DataModelBootstrap.CreateGame(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _game.Destroy();
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
            RbxSpace.ResetForTests();
        }

        [Test]
        public void AdoptWorldObject_WithoutScaledAncestor_ReportsLocalSize()
        {
            GameObject go = new("AdoptProbe");
            _created.Add(go);
            go.transform.localScale = new Vector3(1f, 2f, 3f);
            RbxInstance part = _registry.Create("Part");

            _binder.AdoptWorldObject(part.Id, go);

            RbxVector3 size = _binder.GetPartPropertiesOrDefault(part.Id).Size;
            RbxVector3 expected = RbxSpace.SizeFromUnity(new Vector3(1f, 2f, 3f));
            Assert.AreEqual(expected.X, size.X, Epsilon);
            Assert.AreEqual(expected.Y, size.Y, Epsilon);
            Assert.AreEqual(expected.Z, size.Z, Epsilon);
        }

        [Test]
        public void AdoptWorldObject_UnderScaledAncestor_ReportsWorldSeenSize()
        {
            GameObject ancestor = new("AdoptAncestor");
            _created.Add(ancestor);
            ancestor.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject go = new("AdoptNestedProbe");
            _created.Add(go);
            go.transform.SetParent(ancestor.transform, false);
            go.transform.localScale = Vector3.one;
            RbxInstance part = _registry.Create("Part");

            _binder.AdoptWorldObject(part.Id, go);

            // WHY: the user sees a 2 m cube (1 m local under a 2x ancestor); at 0.5 m/stud that
            // is 4 studs, not the 2 studs localScale alone would report.
            RbxVector3 size = _binder.GetPartPropertiesOrDefault(part.Id).Size;
            Assert.AreEqual(4f, size.X, Epsilon);
            Assert.AreEqual(4f, size.Y, Epsilon);
            Assert.AreEqual(4f, size.Z, Epsilon);
        }
    }
}
