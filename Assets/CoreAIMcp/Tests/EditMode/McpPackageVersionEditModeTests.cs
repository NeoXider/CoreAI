using System.IO;
using CoreAI.Mcp.Protocol;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CoreAI.Mcp.Tests
{
    /// <summary>
    /// <see cref="McpServerInfo.Version"/> is what every client sees in the <c>initialize</c> handshake
    /// and in bug reports, and its doc comment promises it tracks the package. This test is what makes
    /// that promise true - it had drifted three minor versions behind.
    /// </summary>
    public sealed class McpPackageVersionEditModeTests
    {
        [Test]
        public void AdvertisedVersion_MatchesThePackageManifest()
        {
            string manifestPath = Path.Combine(Application.dataPath, "CoreAIMcp/package.json");
            Assert.IsTrue(File.Exists(manifestPath), $"package manifest not found at {manifestPath}");

            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
            string packageVersion = manifest["version"]?.ToString();

            Assert.IsNotNull(packageVersion, "package.json must declare a version.");
            Assert.AreEqual(packageVersion, McpServerInfo.Version,
                "McpServerInfo.Version must be bumped together with package.json.");
        }
    }
}
