using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Instances.TextChat;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// A saved world keeps its TextChatCommands (an optional <c>text_chat_command</c> block) and SelectionBoxes (an
    /// optional <c>selection_box</c> block, the adornee by instance id): restored without advancing a revision, an
    /// adornee the package does not keep restores as nil with one diagnostic, a world without either class writes the
    /// same bytes as before, and a part's local-only <c>LocalTransparencyModifier</c> is never saved.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageAdornmentsEditModeTests
    {
        private const string WorldId = "worldpkg-adornments";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc);

        /// <summary>A command and an outline save, load and save to the same bytes, and restore their properties and revisions.</summary>
        [Test]
        public void CommandAndOutline_RoundTripByteIdentically_AndRestoreWithoutARevisionAdvance()
        {
            AdornedWorld world = AdornedWorld.Create();
            RbxTextChatCommand command = world.AddCommand();
            RbxSelectionBox box = world.AddOutline(world.Part);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            RbxWorldPackagePayload recaptured = RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                restored.Registry, restored.Game, restored.PartSink, WorldPackageTestWorlds.NewSettingsFor(WorldId),
                capturedAtUtc: CapturedAtUtc));

            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(recaptured),
                "the restore reproduces the properties and the saved revisions");
            Assert.IsTrue(restored.Registry.TryGet(command.Id, out RbxInstance commandBack));
            Assert.AreEqual("/dance", ((RbxTextChatCommand)commandBack).PrimaryAlias);
            Assert.AreEqual("/d", ((RbxTextChatCommand)commandBack).SecondaryAlias);
            Assert.IsFalse(((RbxTextChatCommand)commandBack).AutocompleteVisible);
            Assert.IsTrue(restored.Registry.TryGet(box.Id, out RbxInstance boxBack));
            RbxSelectionBox outline = (RbxSelectionBox)boxBack;
            Assert.AreEqual(world.Part.Id, outline.Adornee.Id);
            Assert.AreEqual(new RbxColor3(1f, 0.5f, 0f), outline.Color3);
            Assert.AreEqual(0.3f, outline.LineThickness);
            Assert.AreEqual(0.75f, outline.SurfaceTransparency);
            Assert.IsFalse(outline.Visible);
            JObject json = WorldJson(package);
            Assert.AreEqual(world.Part.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (string)FindInstance(json, box.Id.Value)["selection_box"]["adornee_id"]);
            restored.Game.Destroy();
        }

        /// <summary>Twin: a world without either class writes neither key.</summary>
        [Test]
        public void WorldWithoutCommandsOrOutlines_WritesNeitherKey()
        {
            string text = Encoding.UTF8.GetString(EntryBytes(
                RbxWorldPackageSerializer.WritePackage(AdornedWorld.Create().Capture()), RbxWorldPackageSerializer.WorldEntryName));

            StringAssert.DoesNotContain("\"text_chat_command\"", text);
            StringAssert.DoesNotContain("\"selection_box\"", text);
        }

        /// <summary>An outline of a mod's own part is captured with a nil adornee and one diagnostic; the live box keeps its adornee.</summary>
        [Test]
        public void Outline_OfAModsOwnPart_IsCapturedWithANilAdornee_AndOneDiagnostic()
        {
            AdornedWorld world = AdornedWorld.Create();
            RbxInstance modPart = world.Registry.Create("Part", "some-mod");
            PartProperties properties = PartProperties.CreateDefault();
            world.Sink.SetPartProperties(modPart.Id, in properties);
            modPart.Parent = world.Registry.WorldRoot;
            RbxSelectionBox box = world.AddOutline(modPart);

            RbxWorldPackagePayload payload = world.Capture();

            Assert.AreEqual(0UL, payload.SelectionBoxes[box.Id].AdorneeId);
            Assert.AreSame(modPart, box.Adornee, "the live outline is untouched");
            Assert.AreEqual(1, CountDiagnostics(payload, box.Id.Value, "Adornee", "mod-ephemeral"));
        }

        /// <summary>
        /// An adornee id that names no instance of the package (a hand-edited or damaged file) loads as nil with one
        /// diagnostic, never as a refused package.
        /// </summary>
        [Test]
        public void Outline_WithAnAdorneeThatIsNotInThePackage_LoadsAsNil_WithOneDiagnostic()
        {
            AdornedWorld world = AdornedWorld.Create();
            RbxSelectionBox box = world.AddOutline(world.Part);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            JObject json = WorldJson(package);
            FindInstance(json, box.Id.Value)["selection_box"]["adornee_id"] = "987654321";

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(ReplaceWorld(package, json));

            Assert.AreEqual(0UL, read.SelectionBoxes[box.Id].AdorneeId);
            Assert.AreEqual(1, CountDiagnostics(read, box.Id.Value, "Adornee", "missing"));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            Assert.IsTrue(restored.Registry.TryGet(box.Id, out RbxInstance back));
            Assert.IsNull(((RbxSelectionBox)back).Adornee);
            restored.Game.Destroy();
        }

        /// <summary>Blocks the reader cannot take are refused by name: a foreign key, a block on another class, a missing field.</summary>
        [Test]
        public void Blocks_ForeignOrMisplacedOrIncomplete_AreRefused()
        {
            AdornedWorld world = AdornedWorld.Create();
            RbxTextChatCommand command = world.AddCommand();
            RbxSelectionBox box = world.AddOutline(world.Part);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());

            AssertRefused(package, json => FindInstance(json, command.Id.Value)["text_chat_command"]["future_alias"] = "x", "future_alias");
            AssertRefused(package, json => ((JObject)FindInstance(json, box.Id.Value)["selection_box"]).Remove("line_thickness"), "line_thickness");
            AssertRefused(package, json => FindInstance(json, world.Part.Id.Value)["text_chat_command"] =
                FindInstance(json, command.Id.Value)["text_chat_command"].DeepClone(), "not a TextChatCommand");
            AssertRefused(package, json => FindInstance(json, box.Id.Value)["selection_box"]["color3"] = new JArray(1.0, 0.0), "color3");
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(package), "twin: the package as written reads");
        }

        /// <summary>A part's LocalTransparencyModifier is local to this machine and never enters a package.</summary>
        [Test]
        public void Part_LocalTransparencyModifier_IsNeverSaved()
        {
            AdornedWorld world = AdornedWorld.Create();
            world.Sink.SetLocalTransparencyModifier(world.Part.Id, 0.8f);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture());
            string text = Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(RbxWorldPackageSerializer.ReadPackage(package));

            StringAssert.DoesNotContain("local_transparency", text);
            Assert.AreEqual(0f, ((InMemoryPartPropertySink)restored.PartSink).GetLocalTransparencyModifier(world.Part.Id));
            restored.Game.Destroy();
        }

        /// <summary>
        /// Drift guard: every public property a script sets on either class is stored, so one added later fails here
        /// until persistence covers it.
        /// </summary>
        [Test]
        public void Blocks_CoverEveryPublicSettableProperty()
        {
            HashSet<string> stored = new(StringComparer.Ordinal)
            {
                "PrimaryAlias", "SecondaryAlias", "Enabled", "AutocompleteVisible",
                "Adornee", "Color3", "Transparency", "Visible", "LineThickness", "SurfaceColor3", "SurfaceTransparency"
            };
            List<string> uncovered = new();
            foreach (Type type in new[] { typeof(RbxTextChatCommand), typeof(RbxSelectionBox), typeof(RbxInstanceAdornment), typeof(RbxGuiBase3d) })
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.SetMethod != null && property.SetMethod.IsPublic && !stored.Contains(property.Name))
                    {
                        uncovered.Add(type.Name + "." + property.Name);
                    }
                }
            }

            CollectionAssert.IsEmpty(uncovered, "properties a saved world would lose");
        }

        /// <summary>Their setters advance the revision, so an AI edit that only recolours an outline or renames an alias is undoable.</summary>
        [Test]
        public void Setters_AdvanceTheRevision_SoTheUndoMarkSeesAnAiEdit()
        {
            AdornedWorld world = AdornedWorld.Create();
            RbxTextChatCommand command = world.AddCommand();
            RbxSelectionBox box = world.AddOutline(world.Part);
            RbxWorldStateMark start = RbxWorldStateMark.Of(world.Registry, 0);

            box.Color3 = new RbxColor3(0f, 1f, 0f);
            RbxWorldStateMark recoloured = RbxWorldStateMark.Of(world.Registry, 0);
            command.PrimaryAlias = "/jig";

            Assert.IsFalse(recoloured.HasSameRevisionsAs(start));
            Assert.IsFalse(RbxWorldStateMark.Of(world.Registry, 0).HasSameRevisionsAs(recoloured));
        }

        private static int CountDiagnostics(RbxWorldPackagePayload payload, ulong instanceId, string member, string reason)
        {
            int count = 0;
            foreach (RbxWorldPackageDiagnostic diagnostic in payload.Diagnostics)
            {
                if (diagnostic.ModelId == instanceId && diagnostic.Member == member && diagnostic.Reason == reason)
                {
                    count++;
                }
            }

            return count;
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit, string expected)
        {
            JObject world = WorldJson(package);
            edit(world);
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(ReplaceWorld(package, world)));
            StringAssert.Contains(expected, refused.Message);
        }

        private static byte[] ReplaceWorld(byte[] package, JObject world)
        {
            return WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, EntryBytes(package, RbxWorldPackageSerializer.ManifestEntryName)),
                (RbxWorldPackageSerializer.WorldEntryName, new UTF8Encoding(false, true).GetBytes(world.ToString())));
        }

        private static JObject FindInstance(JObject world, ulong id)
        {
            foreach (JToken node in (JArray)world["instances"])
            {
                if ((string)node["id"] == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    return (JObject)node;
                }
            }

            Assert.Fail("instance " + id + " is not in world.json");
            return null;
        }

        private static JObject WorldJson(byte[] package)
        {
            using StringReader text = new(Encoding.UTF8.GetString(EntryBytes(package, RbxWorldPackageSerializer.WorldEntryName)));
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static byte[] EntryBytes(byte[] package, string name)
        {
            using MemoryStream input = new(package, false);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false);
            using Stream stream = archive.GetEntry(name).Open();
            using MemoryStream output = new();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private sealed class AdornedWorld
        {
            private AdornedWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink, RbxInstance part)
            {
                Registry = registry;
                Game = game;
                Sink = sink;
                Part = part;
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public InMemoryPartPropertySink Sink { get; }

            public RbxInstance Part { get; }

            public static AdornedWorld Create()
            {
                InstanceRegistry registry = new(worldId: WorldId);
                RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                InMemoryPartPropertySink sink = new(registry);
                RbxInstance part = registry.Create("Part", originTag: "console:build");
                part.Name = "Pillar";
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                sink.SetPartProperties(part.Id, in properties);
                part.Parent = registry.WorldRoot;
                return new AdornedWorld(registry, game, sink, part);
            }

            public RbxTextChatCommand AddCommand()
            {
                RbxTextChatCommand command = (RbxTextChatCommand)Registry.Create("TextChatCommand", originTag: "console:build");
                command.Name = "Dance";
                command.PrimaryAlias = "/dance";
                command.SecondaryAlias = "/d";
                command.AutocompleteVisible = false;
                command.Parent = Game.FindFirstChildOfClass("TextChatService");
                return command;
            }

            public RbxSelectionBox AddOutline(RbxInstance adornee)
            {
                RbxSelectionBox box = (RbxSelectionBox)Registry.Create("SelectionBox", originTag: "console:build");
                box.Name = "Outline";
                box.Adornee = adornee;
                box.Color3 = new RbxColor3(1f, 0.5f, 0f);
                box.LineThickness = 0.3f;
                box.SurfaceColor3 = new RbxColor3(0.2f, 0.2f, 0.2f);
                box.SurfaceTransparency = 0.75f;
                box.Visible = false;
                box.Parent = Registry.WorldRoot;
                return box;
            }

            public RbxWorldPackagePayload Capture()
            {
                return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                    Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: CapturedAtUtc));
            }
        }
    }
}
