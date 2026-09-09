using System.Collections.Generic;
using System.IO;
using CoreAI.Editor;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Coverage for the half of the WebGL storage story that used to be missing: the gate travelled
    /// inside <c>com.neoxider.coreaiunity</c> while the template that satisfies it lived in CoreAI's own
    /// <c>Assets/WebGLTemplates</c>, outside every package. A consumer therefore received a build
    /// failure pointing at a folder that did not exist in their project.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIWebGlTemplateInstallerEditModeTests
    {
        [Test]
        public void PackagedTemplate_ShipsInsideThePackage()
        {
            Assert.IsTrue(
                CoreAIWebGlTemplateInstaller.TryResolvePackagedTemplateFolder(out string folder, out string failure),
                "The build gate refuses a template that does not arm browser storage and offers the " +
                $"menu item '{CoreAIWebGlTemplateInstaller.MenuPath}' as the fix. If the package stops " +
                $"carrying '{CoreAIWebGlTemplateInstaller.PackagedTemplatesFolderName}', that offer " +
                $"becomes a dead end for every consumer. {failure}");
            Assert.IsTrue(Directory.Exists(folder));
        }

        [Test]
        public void PackagedTemplate_ArmsBrowserStorage()
        {
            CoreAIWebGlTemplateInstaller.TryResolvePackagedTemplateFolder(out string folder, out _);

            Assert.IsTrue(
                CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                    File.ReadAllText(Path.Combine(folder, "index.html"))),
                "A shipped template that cannot pass the gate it exists to satisfy would send the " +
                "consumer in a circle.");
        }

        [Test]
        public void InstalledTemplatePath_IsWhereUnityResolvesProjectTemplates()
        {
            Assert.AreEqual(
                CoreAIWebGlTemplateInstaller.InstalledTemplateIndexPath,
                CoreAIWebGlPersistentDataSyncBuildGuard.ResolveTemplateIndexPath(
                    CoreAIWebGlTemplateInstaller.SelectedTemplateValue),
                "The installer writes the template where Unity looks for PROJECT: templates, or the " +
                "menu item would install something the build never reads.");
        }

        /// <summary>
        /// The installed copy in this repository is exactly what the package ships. Two copies of one
        /// file drift; this is the check that notices before a consumer does.
        /// </summary>
        [Test]
        public void InstalledTemplate_MatchesThePackagedSource()
        {
            string installed = CoreAIWebGlTemplateInstaller.GetInstalledTemplateAbsolutePath();
            if (!Directory.Exists(installed))
            {
                Assert.Ignore(
                    $"'{CoreAIWebGlTemplateInstaller.InstalledTemplateFolder}' is not present in this " +
                    $"project; run '{CoreAIWebGlTemplateInstaller.MenuPath}' to install it.");
            }

            CoreAIWebGlTemplateInstaller.TryResolvePackagedTemplateFolder(out string packaged, out _);
            IReadOnlyList<string> expected = CoreAIWebGlTemplateInstaller.EnumerateTemplateFiles(packaged);
            IReadOnlyList<string> actual = CoreAIWebGlTemplateInstaller.EnumerateTemplateFiles(installed);

            CollectionAssert.AreEqual(expected, actual,
                $"The installed template drifted from the packaged one. Re-run '{CoreAIWebGlTemplateInstaller.MenuPath}' " +
                "after editing the copy inside the package - the package is the source of truth.");
            foreach (string relative in expected)
            {
                FileAssert.AreEqual(
                    new FileInfo(Path.Combine(packaged, relative)),
                    new FileInfo(Path.Combine(installed, relative)),
                    $"'{relative}' differs between the packaged template and the installed one.");
            }
        }

        [Test]
        public void CopyTemplateFolder_ReplacesTheDestinationInsteadOfMergingIntoIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAiTemplateInstaller" + Path.GetRandomFileName());
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            try
            {
                Directory.CreateDirectory(Path.Combine(source, "TemplateData"));
                File.WriteAllText(Path.Combine(source, "index.html"), "current");
                File.WriteAllText(Path.Combine(source, "TemplateData", "style.css"), "body{}");
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "index.html"), "outdated");
                File.WriteAllText(Path.Combine(destination, "removed-in-a-later-version.js"), "stale");

                CoreAIWebGlTemplateInstaller.CopyTemplateFolder(source, destination);

                Assert.AreEqual("current", File.ReadAllText(Path.Combine(destination, "index.html")));
                Assert.AreEqual("body{}", File.ReadAllText(Path.Combine(destination, "TemplateData", "style.css")));
                Assert.IsFalse(
                    File.Exists(Path.Combine(destination, "removed-in-a-later-version.js")),
                    "A merge would leave files from an older template version behind and the installed " +
                    "copy would no longer be what the package ships.");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void EnumerateTemplateFiles_IgnoresTheMetaFilesUnityGeneratesForTheInstalledCopy()
        {
            string root = Path.Combine(Path.GetTempPath(), "CoreAiTemplateFiles" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "index.html"), "x");
                File.WriteAllText(Path.Combine(root, "index.html.meta"), "guid");

                CollectionAssert.AreEqual(
                    new[] { "index.html" },
                    CoreAIWebGlTemplateInstaller.EnumerateTemplateFiles(root),
                    "Only the project copy has .meta files; comparing them would make the drift check " +
                    "fail for a reason that is not drift.");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
