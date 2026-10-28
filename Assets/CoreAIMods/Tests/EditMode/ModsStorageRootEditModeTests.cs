using System;
using System.IO;
using CoreAI.Ai.Logging;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Mods package's default stores (mod state, mod sources, world packages and the Lua log file)
    /// resolve their location from <see cref="CoreAiStorageRoot"/>, so a redirected root moves them too.
    /// <para>
    /// WHY: the default world-package store prunes old autosaves past its backup capacity. Built by a
    /// test with the engine data path as its root, it deleted five of the developer's autosaves in a
    /// full EditMode run.
    /// </para>
    /// </summary>
    public sealed class ModsStorageRootEditModeTests
    {
        private string _ownedRoot;

        [SetUp]
        public void SetUp()
        {
            _ownedRoot = Path.Combine(Path.GetTempPath(), "cai-mods-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_ownedRoot))
            {
                Directory.Delete(_ownedRoot, true);
            }
        }

        [Test]
        public void DefaultModsStores_BuiltByATest_LandUnderTheRedirectedRoot()
        {
            using (CoreAiStorageRoot.Override(_ownedRoot))
            {
                string coreAi = Path.Combine(_ownedRoot, CoreAiPersistentPaths.RootFolderName);

                using (FileLuaModStore state = new())
                {
                    AssertUnder(coreAi, state.StoragePath, "FileLuaModStore");
                    state.Set("storage-root-probe", "k", "v");
                    Assert.AreEqual("v", state.Get("storage-root-probe", "k"));
                }

                using (FileLuaModSourceStore sources = new())
                {
                    AssertUnder(coreAi, sources.StoragePath, "FileLuaModSourceStore");
                }

                FileRbxWorldPackageStore packages = new(startupNamespace: "storage-root-probe");
                AssertUnder(coreAi, packages.StartupDirectoryForTests, "FileRbxWorldPackageStore");

                LuaLogFileSink sink = new(new LuaLogService());
                AssertUnder(coreAi, sink.FilePath, "LuaLogFileSink");
                sink.Dispose();
            }

            Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(_ownedRoot, CoreAiPersistentPaths.RootFolderName),
                    "*", SearchOption.AllDirectories),
                "The mod state write must have landed under the redirected root.");
        }

        private static void AssertUnder(string root, string path, string store)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringAssert.StartsWith(normalizedRoot, Path.GetFullPath(path),
                $"{store} resolved '{path}', outside the redirected root '{root}'.");
        }
    }
}
