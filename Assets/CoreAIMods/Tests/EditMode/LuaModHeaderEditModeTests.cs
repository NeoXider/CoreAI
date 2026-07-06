using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaModHeaderEditModeTests
    {
        [Test]
        public void Parse_BlockCommentHeader_ReadsAllKnownFields()
        {
            LuaModHeader header = LuaModHeader.Parse(@"--[[@coreai
id: first_person
name: First-Person Controller
version: 1.0.0
active: false
capabilities: All, Full
category: player/controllers
author: CoreAI
description: WASD to move, mouse to look.
tags: movement, camera
unknown: ignored
]]
print('ready')", "fallback");

            Assert.AreEqual("first_person", header.Id);
            Assert.AreEqual("First-Person Controller", header.Name);
            Assert.AreEqual("1.0.0", header.Version);
            Assert.IsFalse(header.Active);
            Assert.AreEqual("All, Full", header.Capabilities);
            Assert.AreEqual("player/controllers", header.Category);
            Assert.AreEqual("CoreAI", header.Author);
            Assert.AreEqual("WASD to move, mouse to look.", header.Description);
            Assert.AreEqual("movement, camera", header.Tags);
        }

        [Test]
        public void Parse_LineCommentFallback_ReadsLeadingCoreAiRun()
        {
            LuaModHeader header = LuaModHeader.Parse(@"-- @coreai ID: line_mod
-- @coreai Name: Line Mod
-- @coreai ACTIVE: false
-- @coreai category: utility
local x = 1", "fallback");

            Assert.AreEqual("line_mod", header.Id);
            Assert.AreEqual("Line Mod", header.Name);
            Assert.IsFalse(header.Active);
            Assert.AreEqual("utility", header.Category);
        }

        [Test]
        public void Parse_MissingKeys_UsesDefaults()
        {
            LuaModHeader header = LuaModHeader.Parse(@"--[[@coreai
id: only_id
]]", "fallback");

            Assert.AreEqual("only_id", header.Id);
            Assert.AreEqual("only_id", header.Name);
            Assert.AreEqual("0.0.0", header.Version);
            Assert.IsTrue(header.Active);
            Assert.AreEqual("All", header.Capabilities);
            Assert.AreEqual("", header.Category);
            Assert.AreEqual("", header.Author);
            Assert.AreEqual("", header.Description);
            Assert.AreEqual("", header.Tags);
        }

        [Test]
        public void Parse_CapabilitiesCommaList_RoundTripsViaLuaCapabilities()
        {
            LuaModHeader header = LuaModHeader.Parse(@"--[[@coreai
capabilities: Read, Gameplay, WorldEdit
]]", "fallback");

            Assert.AreEqual("Read, Gameplay, WorldEdit", header.Capabilities);
        }

        [Test]
        public void Parse_NoHeader_ReturnsFallbackIdAndDefaultActive()
        {
            LuaModHeader header = LuaModHeader.Parse("print('no header')", "fallback_mod");

            Assert.AreEqual("fallback_mod", header.Id);
            Assert.AreEqual("fallback_mod", header.Name);
            Assert.AreEqual("0.0.0", header.Version);
            Assert.IsTrue(header.Active);
            Assert.AreEqual("All", header.Capabilities);
        }
    }
}
