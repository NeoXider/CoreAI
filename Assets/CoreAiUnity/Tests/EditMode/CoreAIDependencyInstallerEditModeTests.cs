using CoreAI.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards the <c>Packages/manifest.json</c> edit performed by
    /// <see cref="CoreAIDependencyInstaller"/>: the result must always be parseable JSON, and the
    /// "already installed" check must only consider the <c>dependencies</c> object.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIDependencyInstallerEditModeTests
    {
        private const string Key = "com.cysharp.unitask";
        private const string Url = "https://github.com/Cysharp/UniTask.git";

        [Test]
        public void TryAddDependency_EmptyDependenciesObject_ProducesParseableJsonWithoutTrailingComma()
        {
            const string manifest = "{\n  \"dependencies\": {}\n}";

            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(manifest, Key, Url, out string updated));

            JObject parsed = JObject.Parse(updated);
            Assert.AreEqual(Url, (string)parsed["dependencies"][Key]);
            Assert.IsFalse(updated.Contains(",\n}"), "An empty dependencies object must not gain a trailing comma.");
        }

        [Test]
        public void TryAddDependency_WhitespaceOnlyDependenciesObject_ProducesParseableJson()
        {
            const string manifest = "{\n  \"dependencies\": {\n  }\n}";

            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(manifest, Key, Url, out string updated));

            JObject parsed = JObject.Parse(updated);
            Assert.AreEqual(Url, (string)parsed["dependencies"][Key]);
        }

        [Test]
        public void TryAddDependency_PopulatedDependenciesObject_KeepsExistingEntries()
        {
            const string manifest =
                "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"1.0.0\"\n  }\n}";

            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(manifest, Key, Url, out string updated));

            JObject parsed = JObject.Parse(updated);
            Assert.AreEqual(Url, (string)parsed["dependencies"][Key]);
            Assert.AreEqual("1.0.0", (string)parsed["dependencies"]["com.unity.ugui"]);
        }

        [Test]
        public void TryAddDependency_TwoInsertsIntoEmptyObject_StaysParseable()
        {
            const string manifest = "{\n  \"dependencies\": {}\n}";

            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(manifest, Key, Url, out string once));
            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(once, "jp.hadashikick.vcontainer", "https://x",
                out string twice));

            JObject parsed = JObject.Parse(twice);
            Assert.AreEqual(Url, (string)parsed["dependencies"][Key]);
            Assert.AreEqual("https://x", (string)parsed["dependencies"]["jp.hadashikick.vcontainer"]);
        }

        [Test]
        public void DependenciesSectionContainsKey_KeyOnlyUnderTestables_IsNotReportedAsInstalled()
        {
            string manifest =
                "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"1.0.0\"\n  },\n" +
                $"  \"testables\": [\n    \"{Key}\"\n  ]\n}}";

            Assert.IsFalse(CoreAIDependencyInstaller.DependenciesSectionContainsKey(manifest, Key));
        }

        [Test]
        public void DependenciesSectionContainsKey_KeyInsideDependencies_IsReportedAsInstalled()
        {
            string manifest = $"{{\n  \"dependencies\": {{\n    \"{Key}\": \"{Url}\"\n  }}\n}}";

            Assert.IsTrue(CoreAIDependencyInstaller.DependenciesSectionContainsKey(manifest, Key));
        }

        [Test]
        public void TryAddDependency_KeyOnlyUnderTestables_StillAddsItToDependencies()
        {
            string manifest =
                "{\n  \"dependencies\": {},\n" +
                $"  \"testables\": [\n    \"{Key}\"\n  ]\n}}";

            Assert.IsTrue(CoreAIDependencyInstaller.TryAddDependency(manifest, Key, Url, out string updated));

            JObject parsed = JObject.Parse(updated);
            Assert.AreEqual(Url, (string)parsed["dependencies"][Key]);
        }
    }
}
