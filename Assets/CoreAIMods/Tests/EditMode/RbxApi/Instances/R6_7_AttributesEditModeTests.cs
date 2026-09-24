using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>Attributes per R6.7 (§5.1.8 item 7): set/get/enumerate, nil removes, name
    /// validation verbatim, unsupported types rejected with BAD_ARGUMENT, and the per-instance
    /// attribute limit a saved world keeps.</summary>
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
        public void R6_7_NonAsciiAttributeName_IsRefusedForANewName()
        {
            // WHY escapes: the subject is non-ASCII input, and escapes keep the source ASCII so the
            // English-only prose guard has nothing to excuse. The four cover a Cyrillic word, an
            // Arabic-Indic digit, a Latin-1 letter and a full-width Latin letter; char.IsLetterOrDigit
            // accepts every one of them, the mirror rule none.
            string[] nonAsciiNames =
            {
                "\u0417\u0434\u043E\u0440\u043E\u0432\u044C\u0435",
                "Level\u0663",
                "Caf\u00E9",
                "\uFF28\uFF50"
            };
            foreach (string name in nonAsciiNames)
            {
                RbxError error = Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName(name),
                    "a new attribute name must use ASCII letters and digits only: " + name);
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
                StringAssert.Contains("non-ASCII", error.RawMessage);
                StringAssert.Contains("ASCII letters", error.Message);
            }

            RbxError cyrillic = Assert.Throws<RbxError>(
                () => AttributeContract.ValidateNewName(nonAsciiNames[0]));
            StringAssert.Contains("(U+0417)", cyrillic.RawMessage,
                "the first offending character is named by code point, readable in any log font");
        }

        [Test]
        public void R6_7_NewNameRule_KeepsEveryOtherLimitAndAcceptsAsciiNames()
        {
            Assert.DoesNotThrow(() => AttributeContract.ValidateNewName("ok.name-with/underscore_1"));
            Assert.DoesNotThrow(() => AttributeContract.ValidateNewName("Health"));
            Assert.DoesNotThrow(() => AttributeContract.ValidateNewName(new string('a', 100)));
            Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName(null));
            Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName(""));
            Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName("has space"));
            Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName("bang!"));
            Assert.Throws<RbxError>(() => AttributeContract.ValidateNewName(new string('a', 101)));
            RbxError reserved = Assert.Throws<RbxError>(
                () => AttributeContract.ValidateNewName("RBXInternal"));
            StringAssert.Contains("RBX", reserved.RawMessage);
        }

        [Test]
        public void R6_7_NonAsciiAttributeName_FromAnOlderWorldPackage_StillRestoresReadsAndSaves()
        {
            // WHY a hand-built node: it is exactly what a package saved before the ASCII rule holds,
            // independent of whether today's SetAttribute would still create the name.
            string legacyName = "\u0417\u0434\u043E\u0440\u043E\u0432\u044C\u0435";
            InstanceTreeSnapshot snapshot = new();
            InstanceSnapshot node = new()
            {
                Id = 1UL,
                ClassName = "Folder",
                Name = "LegacyStats",
                Archivable = true
            };
            node.Attributes.Add(new AttributeSnapshot
            {
                Name = legacyName,
                Kind = AttributeValueKind.Number,
                NumberValue = 100d
            });
            snapshot.Instances.Add(node);

            Assert.DoesNotThrow(() => AttributeContract.ValidateName(legacyName),
                "the stored-name rule must keep accepting what older packages hold");

            InstanceRegistry target = new();
            RbxInstance restored = null;
            Assert.DoesNotThrow(() => restored = InstanceTreeSerializer.Restore(snapshot, target, "local"),
                "an older world holding a non-ASCII attribute name must still load");

            Assert.AreEqual(100d, restored.GetAttribute(legacyName));
            InstanceTreeSnapshot saved = InstanceTreeSerializer.Capture(restored);
            Assert.AreEqual(legacyName, saved.Instances[0].Attributes[0].Name,
                "a restored world must save again with the name it was loaded with");
        }

        [Test]
        public void R6_7_UnsupportedValueTypesAreRejectedNamingTheType()
        {
            RbxError error = Assert.Throws<RbxError>(() => _part.SetAttribute("Bad", new object()));
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

        [Test]
        public void R6_7_AddingAnAttributeBeyondTheWorldPackageLimit_RaisesBadArgument()
        {
            int limit = InstanceTreeSerializer.MaximumAttributesPerInstance;
            for (int index = 0; index < limit; index++)
            {
                _part.SetAttribute("A" + index, index);
            }

            RbxError error = Assert.Throws<RbxError>(() => _part.SetAttribute("OneTooMany", 1),
                "a saved world holds at most " + limit + " attributes per instance, so the live "
                + "instance must refuse the next one instead of producing a world that cannot be saved");

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains("OneTooMany", error.RawMessage);
            Assert.AreEqual(limit, _part.GetAttributes().Count);
            Assert.IsNull(_part.GetAttribute("OneTooMany"));
        }

        [Test]
        public void R6_7_AtTheLimit_ReplacingOrRemovingAnAttributeStillWorks()
        {
            int limit = InstanceTreeSerializer.MaximumAttributesPerInstance;
            for (int index = 0; index < limit; index++)
            {
                _part.SetAttribute("A" + index, index);
            }

            Assert.DoesNotThrow(() => _part.SetAttribute("A0", "replaced"),
                "replacing an existing attribute does not add one");
            Assert.AreEqual("replaced", _part.GetAttribute("A0"));
            Assert.DoesNotThrow(() => _part.SetAttribute("A1", null));
            Assert.DoesNotThrow(() => _part.SetAttribute("Fresh", true),
                "removing one attribute frees a slot for a new one");
            Assert.AreEqual(limit, _part.GetAttributes().Count);
        }
    }
}
