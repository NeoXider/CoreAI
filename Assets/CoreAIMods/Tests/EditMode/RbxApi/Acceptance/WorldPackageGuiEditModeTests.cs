using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// The Roblox GUI of a saved world: an optional <c>gui</c> block on every world-owned GUI node (ScreenGui,
    /// GuiObject and the UI layout objects) and an optional top-level <c>starter_gui</c> block for the StarterGui
    /// switches. Captured and restored through the classes' own getters and setters, enums as Roblox numbers, never
    /// a layout result or presenter state; a mod's own GUI and every PlayerGui copy stay out; a world without GUI
    /// writes the same bytes as before.
    /// </summary>
    [TestFixture]
    public sealed class WorldPackageGuiEditModeTests
    {
        private const string WorldId = "worldpkg-gui";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 11, 17, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// A package with one ScreenGui and one TextLabel, written by hand in the form the writer produces: it reads,
        /// restores the values it names, and re-encodes to the same text, so the key names, enum numbers and array
        /// forms of the block cannot drift.
        /// </summary>
        private const string GoldenWorldJson = @"{
  ""schema_version"": 1,
  ""settings"": {
    ""world_id"": ""worldpkg-gui"",
    ""world_acl_version"": null,
    ""meters_per_stud"": 0.28,
    ""gravity_studs_per_second_squared"": 196.2,
    ""signal_behavior"": ""Deferred""
  },
  ""camera_cframe"": null,
  ""instances"": [
    {
      ""id"": ""1"",
      ""parent_id"": ""0"",
      ""class_name"": ""DataModel"",
      ""name"": ""Game"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    },
    {
      ""id"": ""2"",
      ""parent_id"": ""1"",
      ""class_name"": ""Workspace"",
      ""name"": ""Workspace"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": {
        ""primary_part_id"": ""0"",
        ""has_stored_world_pivot"": false,
        ""stored_world_pivot"": null
      },
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    },
    {
      ""id"": ""3"",
      ""parent_id"": ""1"",
      ""class_name"": ""StarterGui"",
      ""name"": ""StarterGui"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": null,
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""1"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null
    },
    {
      ""id"": ""4"",
      ""parent_id"": ""3"",
      ""class_name"": ""ScreenGui"",
      ""name"": ""Hud"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": ""console:hud"",
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""2"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null,
      ""gui"": {
        ""auto_localize"": true,
        ""enabled"": true,
        ""reset_on_spawn"": false,
        ""z_index_behavior"": 0,
        ""display_order"": 3,
        ""ignore_gui_inset"": true,
        ""clip_to_device_safe_area"": true,
        ""safe_area_compatibility"": 1,
        ""screen_insets"": 2
      }
    },
    {
      ""id"": ""5"",
      ""parent_id"": ""4"",
      ""class_name"": ""TextLabel"",
      ""name"": ""Score"",
      ""archivable"": true,
      ""owner_mod_id"": null,
      ""origin_tag"": ""console:hud"",
      ""owner_actor_id"": null,
      ""access_scope"": null,
      ""revision"": ""3"",
      ""tags"": [],
      ""attributes"": [],
      ""model"": null,
      ""part"": null,
      ""click_detector"": null,
      ""material_variant"": null,
      ""value"": null,
      ""humanoid"": null,
      ""gui"": {
        ""auto_localize"": true,
        ""active"": false,
        ""anchor_point"": [
          1.0,
          0.0
        ],
        ""automatic_size"": 0,
        ""background_color3"": [
          0.25,
          0.5,
          1.0
        ],
        ""background_transparency"": 1.0,
        ""border_color3"": [
          0.0,
          0.0,
          0.0
        ],
        ""border_mode"": 0,
        ""border_size_pixel"": 0,
        ""clips_descendants"": false,
        ""interactable"": true,
        ""layout_order"": -1,
        ""position"": [
          1.0,
          -12,
          0.0,
          12
        ],
        ""rotation"": 0.0,
        ""selectable"": false,
        ""size"": [
          0.25,
          10,
          0.0,
          64
        ],
        ""size_constraint"": 0,
        ""visible"": true,
        ""z_index"": 2,
        ""text"": ""Score: 0"",
        ""text_color3"": [
          1.0,
          1.0,
          1.0
        ],
        ""text_size"": 24.0,
        ""text_transparency"": 0.0,
        ""text_scaled"": false,
        ""text_wrapped"": false,
        ""text_x_alignment"": 0,
        ""text_y_alignment"": 1,
        ""text_truncate"": 0,
        ""font"": 19,
        ""rich_text"": false,
        ""text_stroke_color3"": [
          0.0,
          0.0,
          0.0
        ],
        ""text_stroke_transparency"": 1.0,
        ""line_height"": 1.0,
        ""max_visible_graphemes"": -1,
        ""text_direction"": 0
      }
    }
  ],
  ""starter_gui"": {
    ""show_development_gui"": true,
    ""screen_orientation"": 4,
    ""core_gui_disabled"": [
      2,
      3
    ]
  }
}";

        private const string GoldenManifestJson = @"{
  ""format"": ""coreai-rbx-world"",
  ""format_version"": 1,
  ""minimum_reader_version"": 1,
  ""api_version"": ""MVP2"",
  ""created_utc"": ""2026-11-11T17:00:00.0000000Z"",
  ""world_entry"": ""world.json"",
  ""mods"": []
}";

        // ==================== Round trip ====================

        /// <summary>
        /// A world with a small HUD (every GUI class, its layout objects, the StarterGui switches and two bindables)
        /// saves, loads and saves to the same bytes; restored through the setters and captured again it is still the
        /// same package, and the values are back on the restored instances.
        /// </summary>
        [Test]
        public void Gui_SmallHud_RoundTripsByteIdentically_AndRestoresThroughTheSetters()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            RbxWorldPackagePayload captured = world.Capture(CapturedAtUtc);

            byte[] package = RbxWorldPackageSerializer.WritePackage(captured);
            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(package);
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(read));

            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            RbxWorldPackagePayload recaptured = RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                restored.Registry, restored.Game, restored.PartSink, WorldPackageTestWorlds.NewSettingsFor(WorldId),
                capturedAtUtc: CapturedAtUtc));
            CollectionAssert.AreEqual(package, RbxWorldPackageSerializer.WritePackage(recaptured),
                "restore through the setters reproduces every stored property");

            Assert.AreEqual(world.GuiNodeCount, read.Gui.Count, "every world-owned GUI node carries a block");
            RbxTextLabel score = Restored<RbxTextLabel>(restored, world.Score);
            Assert.AreEqual("Score: 0", score.TextState.Text);
            Assert.AreEqual(19, score.TextState.Font);
            Assert.AreEqual(RbxTextXAlignment.Left, score.TextState.TextXAlignment);
            Assert.AreEqual(new RbxUDim2(0.25f, 10, 0f, 64), Restored<RbxFrame>(restored, world.Panel).Size);
            Assert.AreEqual(3, Restored<RbxScreenGui>(restored, world.Hud).DisplayOrder);
            Assert.IsFalse(Restored<RbxScreenGui>(restored, world.Hud).ResetOnSpawn);
            Assert.AreEqual(new RbxUDim(0.5f, 0), Restored<RbxUiCorner>(restored, world.Corner).BottomLeftRadius);
            Assert.AreEqual(new RbxUDim(0f, 12), Restored<RbxUiCorner>(restored, world.Corner).TopLeftRadius);
            Assert.AreEqual("icons/shop-hover", Restored<RbxImageButton>(restored, world.Shop).HoverImage);
            Assert.AreEqual(new RbxVector2(0f, 40f), Restored<RbxScrollingFrame>(restored, world.List).CanvasPosition);
            Assert.AreEqual(RbxScrollingDirection.Y, Restored<RbxScrollingFrame>(restored, world.List).ScrollingDirection);
            Assert.AreEqual("Say something", Restored<RbxTextBox>(restored, world.Chat).PlaceholderText);
            Assert.IsFalse(Restored<RbxTextBox>(restored, world.Chat).ClearTextOnFocus);
            RbxUiSizeConstraint constraint = Restored<RbxUiSizeConstraint>(restored, world.SizeConstraint);
            Assert.AreEqual(new RbxVector2(8f, 8f), constraint.MinSize);
            Assert.IsTrue(float.IsPositiveInfinity(constraint.MaxSize.X), "the default unbounded MaxSize survives");
            RbxStarterGui starterGui = (RbxStarterGui)restored.Game.FindFirstChildOfClass("StarterGui");
            Assert.IsFalse(starterGui.GetCoreGuiEnabled(2));
            Assert.IsTrue(starterGui.GetCoreGuiEnabled(3));
            Assert.IsFalse(starterGui.ShowDevelopmentGui);
            restored.Game.Destroy();
        }

        /// <summary>The block holds every stored property of its class and nothing the presenter computes.</summary>
        [Test]
        public void Gui_Block_HoldsEveryPropertyOfItsClass_AndNoLayoutResultOrPresenterState()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            JObject json = WorldJson(RbxWorldPackageSerializer.WritePackage(world.Capture(CapturedAtUtc)));

            JObject panel = (JObject)FindInstance(json, world.Panel.Id.Value)["gui"];
            JObject chat = (JObject)FindInstance(json, world.Chat.Id.Value)["gui"];
            JObject list = (JObject)FindInstance(json, world.List.Id.Value)["gui"];

            Assert.AreEqual(RbxWorldPackageGui.Of(world.Panel).PropertyNames.Count, panel.Count);
            foreach (JObject block in new[] { panel, chat, list })
            {
                foreach (JProperty property in block.Properties())
                {
                    StringAssert.DoesNotStartWith("absolute", property.Name);
                }
            }

            Assert.IsNull(chat["cursor_position"], "the caret is not durable state");
            Assert.IsNull(chat["is_focused"]);
            Assert.AreEqual(2, (int)list["scrolling_direction"], "enums are stored as Roblox numbers");
            Assert.AreEqual(new JArray(0.0, 0, 3.0, 0).ToString(), list["canvas_size"].ToString());
        }

        // ==================== Which nodes ====================

        /// <summary>
        /// A mod's own ScreenGui and every copy StarterGui made into the player's PlayerGui stay out of the package;
        /// the world-owned originals are all in it.
        /// </summary>
        [Test]
        public void Gui_ModMadeGuiAndPlayerGuiCopies_AreNotSaved()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            RbxScreenGui modHud = world.Add<RbxScreenGui>("ScreenGui", world.StarterGui, "ModHud", "hud-mod");
            world.Add<RbxTextLabel>("TextLabel", modHud, "ModLabel", "hud-mod");
            RbxPlayer player = world.AddPlayerWithCopies();
            RbxInstance playerGui = player.FindFirstChildOfClass("PlayerGui");
            Assert.IsNotNull(playerGui.FindFirstChild("Hud"), "precondition: the HUD was copied into PlayerGui");

            RbxWorldPackagePayload payload = world.Capture(CapturedAtUtc);

            Assert.AreEqual(world.GuiNodeCount, payload.Gui.Count);
            foreach (RbxInstance copy in playerGui.GetDescendants())
            {
                Assert.IsFalse(payload.Gui.ContainsKey(copy.Id), "a PlayerGui copy is not saved");
            }

            Assert.IsFalse(payload.Gui.ContainsKey(modHud.Id), "a mod's own GUI is not saved");
            Assert.IsTrue(payload.Gui.ContainsKey(world.Hud.Id));
        }

        /// <summary>A BindableEvent and a BindableFunction save their name and class only; no block, no callback.</summary>
        [Test]
        public void Bindables_SaveOnlyTheirName()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture(CapturedAtUtc));
            JObject json = WorldJson(package);

            JObject bindableEvent = FindInstance(json, world.ScoreChanged.Id.Value);
            JObject bindableFunction = FindInstance(json, world.GetScore.Id.Value);
            Assert.IsNull(bindableEvent["gui"]);
            Assert.IsNull(bindableFunction["gui"]);
            Assert.AreEqual("ScoreChanged", (string)bindableEvent["name"]);
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                RbxWorldPackageSerializer.ReadPackage(package));
            Assert.IsInstanceOf<RbxBindableEvent>(Restored<RbxInstance>(restored, world.ScoreChanged));
            Assert.IsInstanceOf<RbxBindableFunction>(Restored<RbxInstance>(restored, world.GetScore));
            restored.Game.Destroy();
        }

        // ==================== Without GUI, and old packages ====================

        /// <summary>
        /// Twin: a world without GUI and with the StarterGui defaults writes neither key, so its package is what the
        /// writer produced before the blocks existed (the literal goldens in <c>Mvp3WorldPackageEditModeTests</c> pin
        /// those bytes).
        /// </summary>
        [Test]
        public void Gui_WorldWithoutGui_WritesNeitherKey()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.Create(WorldId);
            RbxWorldPackagePayload payload = world.Capture(CapturedAtUtc);
            string text = Encoding.UTF8.GetString(EntryBytes(RbxWorldPackageSerializer.WritePackage(payload),
                RbxWorldPackageSerializer.WorldEntryName));

            Assert.AreEqual(0, payload.Gui.Count);
            Assert.IsNull(payload.StarterGui);
            StringAssert.DoesNotContain("\"gui\"", text);
            StringAssert.DoesNotContain("\"starter_gui\"", text);
        }

        /// <summary>
        /// An old package whose StarterGui holds GUI nodes without a block (written before the block existed) loads as
        /// before: the nodes restore with their class defaults, and the package re-encodes to the same text.
        /// </summary>
        [Test]
        public void Gui_OldPackageWithoutBlocks_LoadsWithClassDefaults_AndReencodesUnchanged()
        {
            string oldWorld = StripGuiBlocks(GoldenWorldJson);
            byte[] old = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(GoldenManifestJson)),
                (RbxWorldPackageSerializer.WorldEntryName, Encoding.UTF8.GetBytes(oldWorld)));

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(old);

            Assert.AreEqual(0, read.Gui.Count);
            Assert.IsNull(read.StarterGui);
            Assert.AreEqual(Normalize(oldWorld), Normalize(EntryText(RbxWorldPackageSerializer.WritePackage(read),
                RbxWorldPackageSerializer.WorldEntryName)));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            Assert.IsTrue(restored.Registry.TryGet(new InstanceId(5UL), out RbxInstance label));
            Assert.AreEqual("Label", ((RbxTextLabel)label).TextState.Text);
            Assert.AreEqual(new RbxUDim2(0f, 200, 0f, 50), ((RbxTextLabel)label).Size);
            Assert.AreEqual(RbxGuiText.DefaultTextSize, ((RbxTextLabel)label).TextState.TextSize);
            restored.Game.Destroy();
        }

        /// <summary>Golden: the literal package reads, restores the values it names and re-encodes to the same text.</summary>
        [Test]
        public void Gui_LiteralPackage_ReadsRestoresAndReencodesToTheSameText()
        {
            byte[] golden = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(GoldenManifestJson)),
                (RbxWorldPackageSerializer.WorldEntryName, Encoding.UTF8.GetBytes(GoldenWorldJson)));

            RbxWorldPackagePayload read = RbxWorldPackageSerializer.ReadPackage(golden);
            byte[] rewritten = RbxWorldPackageSerializer.WritePackage(read);

            Assert.AreEqual(Normalize(GoldenWorldJson), Normalize(EntryText(rewritten, RbxWorldPackageSerializer.WorldEntryName)));
            Assert.AreEqual(Normalize(GoldenManifestJson), Normalize(EntryText(rewritten, RbxWorldPackageSerializer.ManifestEntryName)));
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(read);
            Assert.IsTrue(restored.Registry.TryGet(new InstanceId(5UL), out RbxInstance instance));
            RbxTextLabel label = (RbxTextLabel)instance;
            Assert.AreEqual("Score: 0", label.TextState.Text);
            Assert.AreEqual(new RbxVector2(1f, 0f), label.AnchorPoint);
            Assert.AreEqual(new RbxUDim2(1f, -12, 0f, 12), label.Position);
            Assert.AreEqual(-1, label.LayoutOrder);
            Assert.AreEqual(new RbxColor3(0.25f, 0.5f, 1f), label.BackgroundColor3);
            Assert.IsTrue(restored.Registry.TryGet(new InstanceId(4UL), out RbxInstance screen));
            Assert.AreEqual(RbxZIndexBehavior.Global, ((RbxScreenGui)screen).ZIndexBehavior);
            RbxStarterGui starterGui = (RbxStarterGui)restored.Game.FindFirstChildOfClass("StarterGui");
            Assert.IsFalse(starterGui.GetCoreGuiEnabled(2));
            Assert.IsFalse(starterGui.GetCoreGuiEnabled(3));
            Assert.IsFalse(starterGui.GetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll));
            restored.Game.Destroy();
        }

        // ==================== StarterGui switches ====================

        /// <summary>Switching every core GUI off through All is stored as the nine types and restores as All off.</summary>
        [Test]
        public void StarterGui_AllCoreGuiOff_IsStoredAsEveryType_AndRestores()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.Create(WorldId);
            world.StarterGui.SetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll, false);
            world.StarterGui.SetScreenOrientation(3);

            byte[] package = RbxWorldPackageSerializer.WritePackage(world.Capture(CapturedAtUtc));
            JObject block = (JObject)WorldJson(package)["starter_gui"];

            Assert.AreEqual(new JArray(0, 1, 2, 3, 5, 6, 7, 8, 9).ToString(), block["core_gui_disabled"].ToString());
            RbxWorldPackageRestoreResult restored = RbxWorldPackageSerializer.RestoreFresh(
                RbxWorldPackageSerializer.ReadPackage(package));
            RbxStarterGui back = (RbxStarterGui)restored.Game.FindFirstChildOfClass("StarterGui");
            Assert.IsFalse(back.GetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll));
            Assert.AreEqual(3, back.ScreenOrientation);
            restored.Game.Destroy();
        }

        // ==================== Refusals ====================

        /// <summary>A block the class's setters would not take as it is, or that belongs to no GUI node, is refused, never repaired.</summary>
        [Test]
        public void Gui_InvalidBlocks_AreRefusedByName()
        {
            byte[] golden = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, Encoding.UTF8.GetBytes(GoldenManifestJson)),
                (RbxWorldPackageSerializer.WorldEntryName, Encoding.UTF8.GetBytes(GoldenWorldJson)));

            AssertRefused(golden, json => Gui(json, 5)["future_property"] = 1, "future_property");
            AssertRefused(golden, json => Gui(json, 5).Remove("text_size"), "text_size");
            AssertRefused(golden, json => Gui(json, 5)["display_order"] = 1, "display_order");
            AssertRefused(golden, json => Gui(json, 5)["text_x_alignment"] = 3, "text_x_alignment");
            AssertRefused(golden, json => Gui(json, 5)["text_size"] = "8", "text_size");
            AssertRefused(golden, json => Gui(json, 5)["size"] = new JArray(0.25, 10.5, 0.0, 64), "size");
            AssertRefused(golden, json => Gui(json, 5)["size"] = new JArray(0.25, 10, 0.0), "size");
            AssertRefused(golden, json => Gui(json, 5)["anchor_point"] = new JArray("Infinity", 0.0), "anchor_point");
            AssertRefused(golden, json => Gui(json, 5)["rotation"] = 1e39, "rotation");
            AssertRefused(golden, json => Gui(json, 4)["z_index_behavior"] = 2, "z_index_behavior");
            AssertRefused(golden, json => FindInstance(json, 2UL)["gui"] = Gui(json, 4).DeepClone(), "not a GUI class");
            AssertRefused(golden, json => json["starter_gui"]["core_gui_disabled"] = new JArray(3, 2), "ascending");
            AssertRefused(golden, json => json["starter_gui"]["core_gui_disabled"] = new JArray(4), "All");
            AssertRefused(golden, json =>
            {
                JArray instances = (JArray)json["instances"];
                instances.RemoveAt(4);
                instances.RemoveAt(3);
                instances.RemoveAt(2);
            }, "no StarterGui service");
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(golden), "twin: the golden as written reads");
        }

        /// <summary>
        /// The two keys are new members: a reader that does not know them, as every reader up to 7.56.0, refuses the
        /// package and names the key (the generic rule is pinned by
        /// <c>WorldPackageLightingEditModeTests.UnknownKeys_InWorldJsonAnInstanceOrTheManifest_AreRefusedByName</c>);
        /// a world with GUI writes them, so 7.56.0 never opens it without its GUI.
        /// </summary>
        [Test]
        public void Gui_WorldWithGui_WritesBothNewKeys()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            string text = Encoding.UTF8.GetString(EntryBytes(RbxWorldPackageSerializer.WritePackage(world.Capture(CapturedAtUtc)),
                RbxWorldPackageSerializer.WorldEntryName));

            StringAssert.Contains("\"gui\": {", text);
            StringAssert.Contains("\"starter_gui\": {", text);
            StringAssert.Contains("\"minimum_reader_version\": 1", EntryText(
                RbxWorldPackageSerializer.WritePackage(world.Capture(CapturedAtUtc)), RbxWorldPackageSerializer.ManifestEntryName),
                "the blocks need no reader version: an old reader refuses them by name instead of losing them");
        }

        // ==================== Text, bounds and budgets ====================

        /// <summary>A lone UTF-16 surrogate in a label's text is replaced in the package only and reported once.</summary>
        [Test]
        public void Gui_LoneSurrogateInText_IsReplacedInTheSnapshotOnly_WithADiagnostic()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            world.Score.TextState.SetText("coin " + (char)0xD83D);

            RbxWorldPackagePayload payload = world.Capture(CapturedAtUtc);

            Assert.IsTrue(payload.Gui[world.Score.Id].TryGetValue("Text", out object text));
            Assert.AreEqual("coin �", text);
            Assert.AreEqual("coin " + (char)0xD83D, world.Score.TextState.Text, "the live label keeps what the script wrote");
            int reported = 0;
            foreach (RbxWorldPackageDiagnostic diagnostic in payload.Diagnostics)
            {
                if (diagnostic.ModelId == world.Score.Id.Value && diagnostic.Member == "Text"
                                                              && diagnostic.Reason == "ill-formed-text")
                {
                    reported++;
                }
            }

            Assert.AreEqual(1, reported);
            Assert.IsNotNull(RbxWorldPackageSerializer.ReadPackage(RbxWorldPackageSerializer.WritePackage(payload)));
        }

        /// <summary>
        /// The size bound behind the write-behind proof counts the block: a long label raises it at least by its text,
        /// the bound stays at or above the bytes, and a limit the text pushes past is no longer proven.
        /// </summary>
        [Test]
        public void Gui_SizeBound_CountsTheBlock_AndALongLabelEndsTheProof()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            RbxWorldPackagePayload shortText = world.Capture(CapturedAtUtc);
            long shortBound = RbxWorldPackageWorldJson.MeasureUpperBound(shortText);
            world.Score.TextState.SetText(new string('x', 200000));
            RbxWorldPackagePayload longText = world.Capture(CapturedAtUtc);
            long longBound = RbxWorldPackageWorldJson.MeasureUpperBound(longText);
            long written;
            using (MemoryStream stream = RbxWorldPackageWorldJson.Write(longText, 0L))
            {
                written = stream.Length;
            }

            Assert.GreaterOrEqual(longBound - shortBound, 200000L - "Score: 0".Length);
            Assert.GreaterOrEqual(longBound, written);
            Assert.AreEqual(written, RbxWorldPackageWorldJson.MeasureExact(longText));
            RbxWorldPackageLimits tight = new(4 * 1024 * 1024, (int)shortBound + 1000, 8 * 1024 * 1024, 64);
            Assert.IsTrue(RbxWorldPackageSerializer.TryProveEncodable(shortText, tight, out long _));
            Assert.IsFalse(RbxWorldPackageSerializer.TryProveEncodable(longText, tight, out long _));
        }

        /// <summary>The WebGL work budget counts GUI text as it counts every other string the package writes.</summary>
        [Test]
        public void Gui_WebGlWorkBudget_CountsGuiText()
        {
            WorldPackageGuiWorld world = WorldPackageGuiWorld.WithHud(WorldId);
            FileRbxWorldPackageStore.MeasureWebGlWork(world.Capture(CapturedAtUtc), out int _, out long before);
            world.Score.TextState.SetText(new string('y', 5000));

            FileRbxWorldPackageStore.MeasureWebGlWork(world.Capture(CapturedAtUtc), out int _, out long after);

            Assert.AreEqual(5000L - "Score: 0".Length, after - before);
        }

        // ==================== Drift guards ====================

        /// <summary>
        /// Every concrete class of the catalog that is a GuiBase2d or a UIBase carries a block, so a GUI class added
        /// later cannot be saved without its properties unnoticed.
        /// </summary>
        [Test]
        public void Schema_CoversEveryConcreteGuiClassOfTheCatalog()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            List<string> missing = new();
            foreach (ClassDescriptor descriptor in catalog.All)
            {
                if (descriptor.IsAbstract || !(catalog.IsA(descriptor.Name, "GuiBase2d") || catalog.IsA(descriptor.Name, "UIBase")))
                {
                    continue;
                }

                if (!RbxWorldPackageGui.IsGuiClass(descriptor.Name))
                {
                    missing.Add(descriptor.Name);
                }
            }

            CollectionAssert.IsEmpty(missing, "GUI classes the package would save without their properties");
            Assert.IsFalse(RbxWorldPackageGui.IsGuiClass("PlayerGui"), "twin: PlayerGui is per player and never saved");
        }

        /// <summary>
        /// Every public property setter of the GUI classes is stored by the block or left out on purpose with a reason,
        /// so a property added later cannot be lost on save unnoticed.
        /// </summary>
        [Test]
        public void Schema_CoversEveryPublicGuiSetter()
        {
            HashSet<string> stored = new(RbxWorldPackageGuiSchema.AllPropertyNames());
            Type[] types =
            {
                typeof(RbxGuiBase2d), typeof(RbxLayerCollector), typeof(RbxScreenGui), typeof(RbxGuiObject), typeof(RbxFrame),
                typeof(RbxGuiButton), typeof(RbxTextLabel), typeof(RbxTextButton), typeof(RbxTextBox), typeof(RbxImageLabel),
                typeof(RbxImageButton), typeof(RbxScrollingFrame), typeof(RbxGuiText), typeof(RbxGuiImage), typeof(RbxUiCorner),
                typeof(RbxUiPadding), typeof(RbxUiStroke), typeof(RbxUiScale), typeof(RbxUiGridStyleLayout),
                typeof(RbxUiListLayout), typeof(RbxUiGridLayout), typeof(RbxUiAspectRatioConstraint), typeof(RbxUiSizeConstraint)
            };
            List<string> uncovered = new();
            foreach (Type type in types)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!method.Name.StartsWith("Set", StringComparison.Ordinal) || method.GetParameters().Length != 1)
                    {
                        continue;
                    }

                    string property = method.Name.Substring(3);
                    if (!stored.Contains(property) && !RbxWorldPackageGuiSchema.Omitted.ContainsKey(property))
                    {
                        uncovered.Add(type.Name + "." + property);
                    }
                }
            }

            CollectionAssert.IsEmpty(uncovered, "GUI setters whose property a saved world would lose");
            Assert.IsFalse(stored.Contains("CursorPosition"), "twin: the caret is left out on purpose");
        }

        // ==================== Helpers ====================

        private static T Restored<T>(RbxWorldPackageRestoreResult restored, RbxInstance original) where T : RbxInstance
        {
            Assert.IsTrue(restored.Registry.TryGet(original.Id, out RbxInstance instance), original.Name + " was restored");
            return (T)instance;
        }

        private static JObject Gui(JObject world, ulong id)
        {
            return (JObject)FindInstance(world, id)["gui"];
        }

        private static string StripGuiBlocks(string world)
        {
            JObject json;
            using (StringReader text = new(world))
            using (Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
            {
                json = JObject.Load(reader);
            }

            foreach (JToken node in (JArray)json["instances"])
            {
                ((JObject)node).Remove("gui");
            }

            json.Remove("starter_gui");
            return json.ToString();
        }

        private static void AssertRefused(byte[] package, Action<JObject> edit, string expected)
        {
            JObject world = WorldJson(package);
            edit(world);
            byte[] edited = WorldPackageTestWorlds.BuildZip(
                (RbxWorldPackageSerializer.ManifestEntryName, EntryBytes(package, RbxWorldPackageSerializer.ManifestEntryName)),
                (RbxWorldPackageSerializer.WorldEntryName, new UTF8Encoding(false, true).GetBytes(world.ToString())));
            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(
                () => RbxWorldPackageSerializer.ReadPackage(edited));
            StringAssert.Contains(expected, refused.Message);
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

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n");
        }

        private static JObject WorldJson(byte[] package)
        {
            using StringReader text = new(EntryText(package, RbxWorldPackageSerializer.WorldEntryName));
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static string EntryText(byte[] package, string name)
        {
            return new UTF8Encoding(false, true).GetString(EntryBytes(package, name));
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
    }

    /// <summary>
    /// An engine-free world with a StarterGui HUD that reaches every GUI class the package stores, the StarterGui
    /// switches and two bindables; shared by the GUI, undo and encode tests.
    /// </summary>
    internal sealed class WorldPackageGuiWorld
    {
        private WorldPackageGuiWorld(string worldId)
        {
            WorldId = worldId;
            Registry = new InstanceRegistry(worldId: worldId);
            Game = DataModelBootstrap.CreateGame(Registry);
            Sink = new InMemoryPartPropertySink(Registry);
            StarterGui = (RbxStarterGui)Game.FindFirstChildOfClass("StarterGui");
        }

        public string WorldId { get; }

        public InstanceRegistry Registry { get; }

        public RbxDataModel Game { get; }

        public InMemoryPartPropertySink Sink { get; }

        public RbxStarterGui StarterGui { get; }

        public RbxScreenGui Hud { get; private set; }

        public RbxFrame Panel { get; private set; }

        public RbxUiCorner Corner { get; private set; }

        public RbxTextLabel Score { get; private set; }

        public RbxImageButton Shop { get; private set; }

        public RbxTextBox Chat { get; private set; }

        public RbxScrollingFrame List { get; private set; }

        public RbxUiSizeConstraint SizeConstraint { get; private set; }

        public RbxBindableEvent ScoreChanged { get; private set; }

        public RbxBindableFunction GetScore { get; private set; }

        /// <summary>The world-owned GUI nodes the HUD holds.</summary>
        public int GuiNodeCount { get; private set; }

        public static WorldPackageGuiWorld Create(string worldId)
        {
            return new WorldPackageGuiWorld(worldId);
        }

        public static WorldPackageGuiWorld WithHud(string worldId)
        {
            WorldPackageGuiWorld world = new(worldId);
            world.BuildHud();
            return world;
        }

        public T Add<T>(string className, RbxInstance parent, string name, string ownerModId = null) where T : RbxInstance
        {
            T instance = (T)Registry.Create(className, ownerModId, ownerModId == null ? "console:hud" : null);
            instance.Name = name;
            instance.Parent = parent;
            return instance;
        }

        /// <summary>Admits a local player and copies StarterGui into its PlayerGui, as a spawn does.</summary>
        public RbxPlayer AddPlayerWithCopies()
        {
            RbxPlayers players = (RbxPlayers)Game.GetService("Players");
            players.CharacterAutoLoads = false;
            RbxPlayer player = players.EnsureActor(Registry, "local-player");
            new RbxStarterGuiReplicator(Game).CopyInto(player);
            return player;
        }

        public RbxWorldPackagePayload Capture(DateTime capturedAtUtc)
        {
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                Registry, Game, Sink, WorldPackageTestWorlds.NewSettingsFor(WorldId), capturedAtUtc: capturedAtUtc));
        }

        private void BuildHud()
        {
            Hud = Add<RbxScreenGui>("ScreenGui", StarterGui, "Hud");
            Hud.SetDisplayOrder(3);
            Hud.SetResetOnSpawn(false);
            Hud.SetIgnoreGuiInset(true);
            Hud.SetZIndexBehavior(RbxZIndexBehavior.Global);
            Hud.SetScreenInsets(1);

            Panel = Add<RbxFrame>("Frame", Hud, "Panel");
            Panel.SetSize(new RbxUDim2(0.25f, 10, 0f, 64));
            Panel.SetPosition(new RbxUDim2(1f, -12, 0f, 12));
            Panel.SetAnchorPoint(new RbxVector2(1f, 0f));
            Panel.SetBackgroundColor3(new RbxColor3(0.1f, 0.1f, 0.2f));
            Panel.SetBackgroundTransparency(0.25d);
            Panel.SetBorderSizePixel(0);
            Panel.SetZIndex(2);
            Panel.SetLayoutOrder(-1);
            Panel.SetStyle(2);

            Corner = Add<RbxUiCorner>("UICorner", Panel, "Round");
            Corner.SetCornerRadius(new RbxUDim(0f, 12));
            Corner.SetBottomLeftRadius(new RbxUDim(0.5f, 0));
            RbxUiStroke stroke = Add<RbxUiStroke>("UIStroke", Panel, "Edge");
            stroke.SetThickness(2d);
            stroke.SetColor(new RbxColor3(1f, 0.8f, 0f));
            stroke.SetApplyStrokeMode(RbxApplyStrokeMode.Border);
            stroke.SetBorderStrokePosition(RbxBorderStrokePosition.Inner);
            RbxUiPadding padding = Add<RbxUiPadding>("UIPadding", Panel, "Inset");
            padding.SetPaddingLeft(new RbxUDim(0f, 6));
            padding.SetPaddingTop(new RbxUDim(0.1f, 0));
            RbxUiListLayout layout = Add<RbxUiListLayout>("UIListLayout", Panel, "Row");
            layout.SetPadding(new RbxUDim(0f, 4));
            layout.SetFillDirection(RbxFillDirection.Horizontal);
            layout.SetSortOrder(RbxGuiSortOrder.Name);
            layout.SetHorizontalAlignment(RbxHorizontalAlignment.Right);

            Score = Add<RbxTextLabel>("TextLabel", Panel, "Score");
            Score.TextState.SetText("Score: 0");
            Score.TextState.SetTextColor3(new RbxColor3(1f, 1f, 1f));
            Score.TextState.SetTextSize(24d);
            Score.TextState.SetFont(19);
            Score.TextState.SetTextXAlignment(RbxTextXAlignment.Left);
            Score.TextState.SetTextScaled(true);
            Score.TextState.SetRichText(true);
            Score.TextState.SetLineHeight(1.25d);
            Score.TextState.SetMaxVisibleGraphemes(12);
            Score.SetBackgroundTransparency(1d);

            RbxTextButton play = Add<RbxTextButton>("TextButton", Panel, "Play");
            play.TextState.SetText("Play");
            play.SetAutoButtonColor(false);
            play.SetModal(true);
            play.SetStyle(1);

            Shop = Add<RbxImageButton>("ImageButton", Panel, "Shop");
            Shop.ImageState.SetImage("icons/shop");
            Shop.ImageState.SetScaleType(RbxScaleType.Fit);
            Shop.ImageState.SetImageRectOffset(new RbxVector2(4f, 8f));
            Shop.SetHoverImage("icons/shop-hover");
            Shop.SetPressedImage("icons/shop-pressed");
            Add<RbxUiScale>("UIScale", Shop, "Grow").SetScale(1.5d);

            Chat = Add<RbxTextBox>("TextBox", Hud, "Chat");
            Chat.SetPlaceholderText("Say something");
            Chat.SetPlaceholderColor3(new RbxColor3(0.6f, 0.6f, 0.6f));
            Chat.SetClearTextOnFocus(false);
            Chat.SetMultiLine(true);
            Chat.SetShowNativeInput(false);
            Chat.TextState.SetText("");

            List = Add<RbxScrollingFrame>("ScrollingFrame", Hud, "Inventory");
            List.SetCanvasSize(new RbxUDim2(0f, 0, 3f, 0));
            List.SetCanvasPosition(new RbxVector2(0f, 40f));
            List.SetScrollingDirection(RbxScrollingDirection.Y);
            List.SetScrollBarThickness(6);
            List.SetAutomaticCanvasSize(RbxAutomaticSize.Y);
            List.SetElasticBehavior(2);
            RbxUiGridLayout grid = Add<RbxUiGridLayout>("UIGridLayout", List, "Cells");
            grid.SetCellSize(new RbxUDim2(0f, 50, 0f, 50));
            grid.SetCellPadding(new RbxUDim2(0f, 2, 0f, 2));
            grid.SetFillDirectionMaxCells(4);
            grid.SetStartCorner(RbxStartCorner.BottomRight);
            RbxImageLabel coin = Add<RbxImageLabel>("ImageLabel", List, "Coin");
            coin.ImageState.SetImage("icons/coin");
            coin.ImageState.SetImageColor3(new RbxColor3(1f, 0.85f, 0.2f));
            coin.ImageState.SetImageTransparency(0.5d);
            coin.ImageState.SetTileSize(new RbxUDim2(0f, 16, 0f, 16));
            RbxUiAspectRatioConstraint aspect = Add<RbxUiAspectRatioConstraint>("UIAspectRatioConstraint", coin, "Square");
            aspect.SetAspectRatio(1.5d);
            aspect.SetAspectType(RbxAspectType.ScaleWithParentSize);
            aspect.SetDominantAxis(RbxDominantAxis.Height);
            SizeConstraint = Add<RbxUiSizeConstraint>("UISizeConstraint", coin, "AtLeast");
            SizeConstraint.SetMinSize(new RbxVector2(8f, 8f));
            GuiNodeCount = 16;

            StarterGui.SetCoreGuiEnabled(2, false);
            StarterGui.SetShowDevelopmentGui(false);

            RbxInstance storage = Game.FindFirstChildOfClass("ReplicatedStorage");
            ScoreChanged = Add<RbxBindableEvent>("BindableEvent", storage, "ScoreChanged");
            GetScore = Add<RbxBindableFunction>("BindableFunction", storage, "GetScore");
        }
    }
}
