using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>Attributes per R6.7 (§5.1.8 item 7): set/get/enumerate, nil removes, name
    /// validation verbatim, unsupported types rejected with BAD_ARGUMENT.</summary>
    [TestFixture]
    public sealed class R6_7_AttributesEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxInstance _part;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _part = _registry.Create("Part");
        }

        [Test]
        public void R6_7_SetGetAndEnumerateRoundTrip()
        {
            _part.SetAttribute("Health", 100);
            _part.SetAttribute("Label", "boss");
            _part.SetAttribute("Enabled", true);

            Assert.AreEqual(100d, _part.GetAttribute("Health"));
            Assert.AreEqual("boss", _part.GetAttribute("Label"));
            Assert.AreEqual(true, _part.GetAttribute("Enabled"));
            Assert.IsNull(_part.GetAttribute("Missing"));

            IReadOnlyDictionary<string, object> all = _part.GetAttributes();
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual(100d, all["Health"]);
        }

        [Test]
        public void R6_7_NilValueRemovesTheAttribute()
        {
            _part.SetAttribute("Health", 5);
            _part.SetAttribute("Health", null);
            Assert.IsNull(_part.GetAttribute("Health"));
            Assert.AreEqual(0, _part.GetAttributes().Count);
        }

        [Test]
        public void R6_7_NumbersNormalizeToDouble()
        {
            _part.SetAttribute("I", 3);
            _part.SetAttribute("F", 1.5f);
            _part.SetAttribute("L", 9L);
            Assert.IsInstanceOf<double>(_part.GetAttribute("I"));
            Assert.IsInstanceOf<double>(_part.GetAttribute("F"));
            Assert.IsInstanceOf<double>(_part.GetAttribute("L"));
        }

        [Test]
        public void R6_7_ReservedRbxPrefixIsRejected()
        {
            RbxError error = Assert.Throws<RbxError>(() => _part.SetAttribute("RBXInternal", 1));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("RBX", error.RawMessage);
        }

        [Test]
        public void R6_7_InvalidNamesAreRejected()
        {
            Assert.Throws<RbxError>(() => _part.SetAttribute(null, 1));
            Assert.Throws<RbxError>(() => _part.SetAttribute("", 1));
            Assert.Throws<RbxError>(() => _part.SetAttribute("has space", 1));
            Assert.Throws<RbxError>(() => _part.SetAttribute("bang!", 1));
            Assert.Throws<RbxError>(() => _part.SetAttribute(new string('a', 101), 1));
            Assert.DoesNotThrow(() => _part.SetAttribute("ok.name-with/underscore_1", 1));
        }

        [Test]
        public void R6_7_UnsupportedValueTypesAreRejectedNamingTheType()
        {
            RbxError error = Assert.Throws<RbxError>(
                () => _part.SetAttribute("Bad", new object()));
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("Object", error.RawMessage);
            StringAssert.Contains("fix:", error.Message);
            StringAssert.Contains("Vector3, Vector2, Color3, or UDim", error.Message);
        }

        [Test]
        public void R6_7_DatatypeValues_AreStoredAndReturnedUnchanged()
        {
            _part.SetAttribute("Spawn", new RbxVector3(1f, 2f, 3f));
            _part.SetAttribute("Screen", new RbxVector2(4f, 5f));
            _part.SetAttribute("Tint", RbxColor3.FromRGB(255f, 0f, 0f));
            _part.SetAttribute("Pad", new RbxUDim(0.5f, 8));

            Assert.AreEqual(new RbxVector3(1f, 2f, 3f), _part.GetAttribute("Spawn"));
            Assert.AreEqual(new RbxVector2(4f, 5f), _part.GetAttribute("Screen"));
            Assert.AreEqual(RbxColor3.FromRGB(255f, 0f, 0f), _part.GetAttribute("Tint"));
            Assert.AreEqual(new RbxUDim(0.5f, 8), _part.GetAttribute("Pad"));
        }
    }
}
