using System.Collections.Generic;
using System.IO;
using CoreAI.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for the WebGL template gate.
    /// <para>
    /// The shipped 2026-09-09 player was built with Unity's stock Default template, where
    /// <c>config.autoSyncPersistentDataPath = true</c> is present but COMMENTED OUT. Since CoreAI no
    /// longer drives the deprecated manual <c>FS.syncfs</c> path, that flag is the only durable-storage
    /// channel left, and a build without it silently loses everything CoreAI writes on reload.
    /// </para>
    /// <para>
    /// The text checks below cover the parser; the <c>OnPreprocessBuild</c> checks cover the gate
    /// itself, because a correct parser that Unity never calls stops nothing. The define symbols are
    /// injected rather than written into player settings: writing real scripting defines recompiles the
    /// editor, which would tear down the test run that asked for it.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class CoreAIWebGlPersistentDataSyncBuildGuardEditModeTests
    {
        // WHY: Unity ignores folders whose name ends with '~', so the fixture template never enters the
        // asset database, needs no .meta, and cannot be mistaken for a shipped template.
        private const string FixtureTemplateName = "CoreAiBuildGuardFixture~";

        private const string StockDefaultTemplate =
            "      // If you would like all file writes inside Unity Application.persistentDataPath\n" +
            "      // directory to automatically persist so that the contents are remembered when\n" +
            "      // the user revisits the site the next time, uncomment the following line:\n" +
            "      // config.autoSyncPersistentDataPath = true;\n";

        private string _originalTemplate;
        private List<string> _warnings;

        private static string FixtureDirectory => Path.Combine("Assets", "WebGLTemplates", FixtureTemplateName);

        private static string FixtureIndexPath => Path.Combine(FixtureDirectory, "index.html");

        [SetUp]
        public void SetUp()
        {
            _originalTemplate = PlayerSettings.WebGL.template;
            _warnings = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            PlayerSettings.WebGL.template = _originalTemplate;
            if (Directory.Exists(FixtureDirectory))
            {
                Directory.Delete(FixtureDirectory, true);
            }
        }

        [Test]
        public void Guard_IsRegisteredWithTheBuildPipeline()
        {
            Assert.IsTrue(
                typeof(IPreprocessBuildWithReport).IsAssignableFrom(typeof(CoreAIWebGlPersistentDataSyncBuildGuard)),
                "The interface IS the registration: drop it and Unity never calls the gate, so a player " +
                "ships again with browser storage disarmed and nothing red anywhere.");
        }

        /// <summary>
        /// The gate is driven with a <c>null</c> report, which means "verify unconditionally": the
        /// platform filter belongs to the caller (Unity passes a WebGL report) and a test cannot
        /// construct a <c>BuildReport</c> carrying a platform.
        /// </summary>
        [Test]
        public void OnPreprocessBuild_StockUnityTemplate_AbortsTheBuild()
        {
            SelectFixtureTemplate(StockDefaultTemplate);

            BuildFailedException failure = Assert.Throws<BuildFailedException>(
                () => NewGuard("").OnPreprocessBuild(null),
                "A commented-out flag persists nothing; the build must stop instead of shipping it.");

            StringAssert.Contains("autoSyncPersistentDataPath", failure.Message);
        }

        [Test]
        public void OnPreprocessBuild_TemplateWithNoIndexHtml_AbortsTheBuild()
        {
            PlayerSettings.WebGL.template = "PROJECT:" + FixtureTemplateName;

            Assert.Throws<BuildFailedException>(
                () => NewGuard("").OnPreprocessBuild(null),
                "An unverifiable template is not an armed one: unreadable must fail closed.");
        }

        [Test]
        public void OnPreprocessBuild_TemplateThatArmsStorage_LetsTheBuildThrough()
        {
            SelectFixtureTemplate("      config.autoSyncPersistentDataPath = true;\n");

            Assert.DoesNotThrow(
                () => NewGuard("").OnPreprocessBuild(null),
                "The gate must block only the defect; blocking a correct template would be its own bug.");
            CollectionAssert.IsEmpty(_warnings, "A correct template is not something to warn about.");
        }

        /// <summary>
        /// Uses the constructor Unity itself calls, so the default define reader is exercised too: a gate
        /// wired to a reader that never returns this project's symbols would silently stop gating - or,
        /// worse, would ignore an opt-out the developer did state.
        /// </summary>
        [Test]
        public void OnPreprocessBuild_ConstructedTheWayUnityConstructsIt_ReadsThisProjectsDefineSymbols()
        {
            SelectFixtureTemplate(StockDefaultTemplate);

            if (CoreAIWebGlPersistentDataSyncBuildGuard.IsOptedOut(
                    PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL)))
            {
                Assert.DoesNotThrow(
                    () => new CoreAIWebGlPersistentDataSyncBuildGuard().OnPreprocessBuild(null),
                    $"This project defines '{CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol}' " +
                    "for the Web platform, so the gate must stand down instead of failing the build.");
                return;
            }

            Assert.Throws<BuildFailedException>(
                () => new CoreAIWebGlPersistentDataSyncBuildGuard().OnPreprocessBuild(null),
                "Without the opt-out symbol the parameterless guard must still refuse a template that " +
                "disarms browser storage.");
        }

        [Test]
        public void OnPreprocessBuild_ProjectsOwnWebGlTemplate_PassesItsOwnGate()
        {
            Assert.DoesNotThrow(
                () => new CoreAIWebGlPersistentDataSyncBuildGuard().OnPreprocessBuild(null),
                $"The selected WebGL template '{PlayerSettings.WebGL.template}' cannot pass CoreAI's own " +
                "build gate, so a WebGL build of this project would abort. Either the template does not " +
                $"set {CoreAIWebGlPersistentDataSyncBuildGuard.RequiredTemplateLine}, or its folder is " +
                "missing from the working tree - Assets/WebGLTemplates/** belongs in the repository, " +
                $".meta files included. Menu '{CoreAIWebGlTemplateInstaller.MenuPath}' reinstalls it.");
        }

        [Test]
        public void OnPreprocessBuild_OptOutDefineSymbol_LetsTheBuildThrough()
        {
            SelectFixtureTemplate(StockDefaultTemplate);

            Assert.DoesNotThrow(
                () => NewGuard("SOME_OTHER_SYMBOL;" +
                               CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol)
                    .OnPreprocessBuild(null),
                "A developer who states that this player ships without CoreAI's storage must not be " +
                "handed an unopenable build failure.");
        }

        [Test]
        public void OnPreprocessBuild_OptOutDefineSymbol_IsLoggedAndNamesWhatIsLost()
        {
            SelectFixtureTemplate(StockDefaultTemplate);

            NewGuard(CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol).OnPreprocessBuild(null);

            Assert.AreEqual(1, _warnings.Count, "A silent opt-out is the defect this escape hatch must not become.");
            StringAssert.Contains(CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol, _warnings[0]);
            StringAssert.Contains("reload", _warnings[0]);
        }

        [Test]
        public void OnPreprocessBuild_OptOutDefineSymbol_OnAnArmedTemplate_SaysTheSymbolIsRedundant()
        {
            SelectFixtureTemplate("      config.autoSyncPersistentDataPath = true;\n");

            NewGuard(CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol).OnPreprocessBuild(null);

            Assert.AreEqual(1, _warnings.Count);
            StringAssert.Contains("can be removed", _warnings[0],
                "An opt-out kept after the template was fixed hides the next regression forever.");
        }

        [Test]
        public void IsOptedOut_MatchesWholeSymbolsOnly()
        {
            Assert.IsTrue(CoreAIWebGlPersistentDataSyncBuildGuard.IsOptedOut(
                "A;" + CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol + ";B"));
            Assert.IsFalse(CoreAIWebGlPersistentDataSyncBuildGuard.IsOptedOut(
                CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol + "_EXTRA"));
            Assert.IsFalse(CoreAIWebGlPersistentDataSyncBuildGuard.IsOptedOut(""));
        }

        /// <summary>
        /// The message is the whole remedy for a consumer: it must name a path inside THEIR project, the
        /// exact line, and the way out - never a path that exists only in CoreAI's own repository.
        /// </summary>
        [Test]
        public void FailureMessage_NamesTheExactLine_TheProjectLocalInstaller_AndTheOptOut()
        {
            string message = CoreAIWebGlPersistentDataSyncBuildGuard.DescribeMissingFlag(
                "PROJECT:MyOwnTemplate",
                "Assets/WebGLTemplates/MyOwnTemplate/index.html");

            StringAssert.Contains(CoreAIWebGlPersistentDataSyncBuildGuard.RequiredTemplateLine, message);
            StringAssert.Contains(CoreAIWebGlTemplateInstaller.MenuPath, message);
            StringAssert.Contains(CoreAIWebGlTemplateInstaller.InstalledTemplateFolder, message);
            StringAssert.Contains(CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol, message);
            StringAssert.Contains("PROJECT:MyOwnTemplate", message);
        }

        [Test]
        public void UnreadableTemplateMessage_AlsoNamesTheRemedies()
        {
            string message = CoreAIWebGlPersistentDataSyncBuildGuard.DescribeUnreadableTemplate(
                "PROJECT:Missing",
                "Assets/WebGLTemplates/Missing/index.html");

            StringAssert.Contains(CoreAIWebGlPersistentDataSyncBuildGuard.RequiredTemplateLine, message);
            StringAssert.Contains(CoreAIWebGlTemplateInstaller.MenuPath, message);
            StringAssert.Contains(CoreAIWebGlPersistentDataSyncBuildGuard.OptOutDefineSymbol, message);
        }

        [Test]
        public void StockUnityTemplate_LeavesTheFlagCommentedOut_AndIsRejected()
        {
            Assert.IsFalse(
                CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(StockDefaultTemplate),
                "A commented-out line persists nothing; treating it as enabled is the exact defect.");
        }

        [Test]
        public void PlainAssignment_IsAccepted()
        {
            Assert.IsTrue(CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                "      config.autoSyncPersistentDataPath = true;\n"));
        }

        [Test]
        public void ObjectLiteralEntry_IsAccepted()
        {
            Assert.IsTrue(CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                "      createUnityInstance(canvas, {\n" +
                "        autoSyncPersistentDataPath: true,\n" +
                "      });\n"));
        }

        [Test]
        public void ExplicitlyDisabled_IsRejected()
        {
            Assert.IsFalse(CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                "      config.autoSyncPersistentDataPath = false;\n"));
        }

        [Test]
        public void BlockCommentedAssignment_IsRejected()
        {
            Assert.IsFalse(CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                "      /* config.autoSyncPersistentDataPath = true; */\n"));
        }

        [Test]
        public void TrailingCommentAfterTheAssignment_IsStillAccepted()
        {
            Assert.IsTrue(CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync(
                "      config.autoSyncPersistentDataPath = true; // required by CoreAI\n"));
        }

        [Test]
        public void ResolveTemplateIndexPath_ProjectTemplate_ResolvesUnderTheProjectsTemplateFolder()
        {
            Assert.AreEqual(
                "Assets/WebGLTemplates/CoreAI/index.html",
                CoreAIWebGlPersistentDataSyncBuildGuard.ResolveTemplateIndexPath("PROJECT:CoreAI"),
                "PROJECT: templates live in the project, not in the editor installation.");
        }

        [Test]
        public void ResolveTemplateIndexPath_UnnamedTemplate_ResolvesToNothingRatherThanAGuess()
        {
            Assert.IsEmpty(
                CoreAIWebGlPersistentDataSyncBuildGuard.ResolveTemplateIndexPath("PROJECT:"),
                "An unresolvable name must reach the gate as unverifiable, not as some other template.");
        }

        private CoreAIWebGlPersistentDataSyncBuildGuard NewGuard(string webGlDefineSymbols)
        {
            return new CoreAIWebGlPersistentDataSyncBuildGuard(
                () => webGlDefineSymbols,
                warning => _warnings.Add(warning));
        }

        private static void SelectFixtureTemplate(string indexHtml)
        {
            Directory.CreateDirectory(FixtureDirectory);
            File.WriteAllText(FixtureIndexPath, indexHtml);
            PlayerSettings.WebGL.template = "PROJECT:" + FixtureTemplateName;
        }
    }
}
