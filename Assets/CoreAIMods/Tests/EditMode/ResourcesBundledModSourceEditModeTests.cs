using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure.Lua;
using NUnit.Framework;
using UnityEditor;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="ResourcesBundledModSource"/> per-mod isolation: one bundled mod
    /// with an unknown header token must not break the loading of the other bundled mods (a throwing
    /// header parse used to abort the whole <c>Load</c>, killing the seeding of ALL bundled mods and the
    /// Hub list with it). Uses a temporary Resources folder created and deleted per test.
    /// </summary>
    public sealed class ResourcesBundledModSourceEditModeTests
    {
        private const string RootFolder = "Assets/CoreAIModsTests_TmpResources";
        private const string SubFolder = "CoreAIModsTests_Bundled";

        [TearDown]
        public void DeleteTemporaryResources()
        {
            AssetDatabase.DeleteAsset(RootFolder);
        }

        [Test]
        public void Load_ModWithUnknownCapabilityToken_DoesNotBreakOtherBundledMods()
        {
            string folder = $"{RootFolder}/Resources/{SubFolder}";
            Directory.CreateDirectory(folder);
            File.WriteAllText($"{folder}/good_mod.txt",
                "--[[@coreai\nid: good_mod\nversion: 1.2.3\ncapabilities: Read\n]]\nreport('ok')\n");
            File.WriteAllText($"{folder}/weird_caps.txt",
                "--[[@coreai\nid: weird_caps\nversion: 0.1.0\ncapabilities: Read, TotallyUnknownCap\n]]\nreport('ok')\n");
            AssetDatabase.Refresh();

            IReadOnlyList<BundledMod> mods = new ResourcesBundledModSource(SubFolder).Load();

            Dictionary<string, BundledMod> byId = new();
            foreach (BundledMod mod in mods)
            {
                byId[mod.Id] = mod;
            }

            Assert.IsTrue(byId.ContainsKey("good_mod"), "The well-formed bundled mod must load.");
            Assert.AreEqual("1.2.3", byId["good_mod"].Version);
            Assert.IsTrue(byId.ContainsKey("weird_caps"),
                "A bundled mod with an unknown capabilities token must still load (tolerant header parse).");
            Assert.AreEqual("0.1.0", byId["weird_caps"].Version);
        }
    }
}
