using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// The Microsoft.Extensions.AI version is a CONSUMER constraint, not a preference. Unity ships its
    /// own System.Text.Json (assembly version 8.0.0.0) in the editor's BCL extensions and substitutes it
    /// for any project copy, so a game cannot load a Microsoft.Extensions.AI built against
    /// System.Text.Json 10.0.0.0 - which is what every 10.x release is. The game is therefore pinned to
    /// <see cref="MeaiFloor"/> and cannot move up.
    /// <para>
    /// So CoreAI builds against that same version. That is the whole point of this fixture: using a
    /// member that does not exist in <see cref="MeaiFloor"/> then fails in THIS repository, on an
    /// ordinary build, instead of surfacing days later as CS0234 inside the game. The compiler is the
    /// real guard; these tests only stop the pin itself from drifting away from it in one of the places
    /// that has to agree - the vendored packages, the NuGet manifest, and the portable csproj.
    /// </para>
    /// <para>
    /// Raising the floor is a deliberate act: it is allowed only when the consumer's engine can load the
    /// newer assemblies. Change <see cref="MeaiFloor"/>, re-vendor Assets/Packages, update
    /// tools/portable/CoreAI.Core.csproj, and only then update the game.
    /// </para>
    /// </summary>
    public sealed class MeaiVersionFloorEditModeTests
    {
        /// <summary>The single declared Microsoft.Extensions.AI version every place must agree on.</summary>
        internal const string MeaiFloor = "9.10.2";

        /// <summary>Major version of the System.Text.Json Unity ships and substitutes for the project copy.</summary>
        private const int EngineSystemTextJsonMajor = 8;

        private const string MeaiPackage = "Microsoft.Extensions.AI";
        private const string MeaiAbstractionsPackage = "Microsoft.Extensions.AI.Abstractions";

        /// <summary>
        /// Runs everywhere the package is installed, including inside a consuming game: a host whose
        /// Microsoft.Extensions.AI is OLDER than the floor cannot honour this package's API surface, and
        /// saying so here is far cheaper than a load failure at the first tool call.
        /// </summary>
        [Test]
        public void LoadedAbstractions_AreNotOlderThanTheDeclaredFloor()
        {
            Version floor = MajorMinorOf(MeaiFloor);
            Version loaded = typeof(MEAI.IChatClient).Assembly.GetName().Version;

            Assert.IsNotNull(loaded, "Microsoft.Extensions.AI.Abstractions must report an assembly version.");
            Assert.GreaterOrEqual(new Version(loaded.Major, loaded.Minor), floor,
                $"CoreAI is written against Microsoft.Extensions.AI {MeaiFloor}; this host loaded {loaded}.");
        }

        /// <summary>
        /// Inside the CoreAI checkout the floor is not a minimum but the EXACT version to build against:
        /// a newer one would compile members the consumer cannot load.
        /// </summary>
        [Test]
        public void LoadedAbstractions_InThisRepository_AreExactlyTheFloor()
        {
            RequireRepository();
            Version floor = MajorMinorOf(MeaiFloor);
            Version loaded = typeof(MEAI.IChatClient).Assembly.GetName().Version;

            Assert.AreEqual(floor, new Version(loaded.Major, loaded.Minor),
                $"This repository must build against Microsoft.Extensions.AI {MeaiFloor} exactly, " +
                "so an API that only exists in a newer release cannot compile here and break the game later. " +
                $"Loaded: {loaded}.");
        }

        [Test]
        public void VendoredPackages_AreExactlyTheFloorAndNothingElse()
        {
            string root = RequireRepository();
            string packages = Path.Combine(root, "Assets", "Packages");

            List<string> vendored = Directory.GetDirectories(packages, MeaiPackage + ".*")
                .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();

            CollectionAssert.AreEqual(
                new[] { MeaiPackage + "." + MeaiFloor, MeaiAbstractionsPackage + "." + MeaiFloor },
                vendored,
                "Assets/Packages must vendor exactly one Microsoft.Extensions.AI version - the declared " +
                "floor. A second copy makes the assembly Unity actually compiles against a coin flip.");

            foreach (string directory in vendored)
            {
                string library = Path.Combine(packages, directory, "lib");
                Assert.IsTrue(Directory.Exists(library) &&
                    Directory.GetFiles(library, "*.dll", SearchOption.AllDirectories).Length > 0,
                    $"{directory} carries no assembly; the pin would be satisfied by nothing.");
            }
        }

        [Test]
        public void PackagesConfig_PinsBothMeaiPackagesAtTheFloor()
        {
            string root = RequireRepository();
            string manifest = File.ReadAllText(Path.Combine(root, "Assets", "packages.config"));

            Assert.AreEqual(MeaiFloor, PinnedVersion(manifest, MeaiPackage),
                "Assets/packages.config drives what Unity compiles against.");
            Assert.AreEqual(MeaiFloor, PinnedVersion(manifest, MeaiAbstractionsPackage),
                "The abstractions ship the contract types; a split version pair is a broken build waiting to happen.");
        }

        /// <summary>
        /// The reason MEAI is capped in the first place. Unity substitutes its own System.Text.Json
        /// 8.0.0.0, so a project copy from a different major is either dead weight (the engine wins) or,
        /// worse, what CoreAI compiles against - which is how an API the game cannot run gets in.
        /// </summary>
        [Test]
        public void SystemTextJson_StaysOnTheLineTheEngineSubstitutes()
        {
            string root = RequireRepository();
            string manifest = File.ReadAllText(Path.Combine(root, "Assets", "packages.config"));

            Assert.AreEqual(EngineSystemTextJsonMajor, Version.Parse(PinnedVersion(manifest, "System.Text.Json")).Major,
                $"Unity supplies System.Text.Json {EngineSystemTextJsonMajor}.0.0.0 and overrides the project copy; " +
                "pinning another major here compiles CoreAI against an API the engine will not provide.");

            string project = File.ReadAllText(
                Path.Combine(root, "tools", "portable", "CoreAI.Core.csproj"));
            Match reference = Regex.Match(project,
                "PackageReference\\s+Include=\"System\\.Text\\.Json\"\\s+Version=\"([^\"]+)\"");
            Assert.IsTrue(reference.Success,
                "The portable build must pin System.Text.Json explicitly; otherwise NuGet resolves the newest " +
                "one that satisfies MEAI and the engine-free gate stops matching the engine.");
            Assert.AreEqual(EngineSystemTextJsonMajor, Version.Parse(reference.Groups[1].Value).Major,
                "The portable gate must compile against the same System.Text.Json line as the engine.");
        }

        [Test]
        public void PortableProject_PinsTheSameFloor()
        {
            string root = RequireRepository();
            string project = File.ReadAllText(
                Path.Combine(root, "tools", "portable", "CoreAI.Core.csproj"));

            Match reference = Regex.Match(project,
                "PackageReference\\s+Include=\"" + Regex.Escape(MeaiPackage) + "\"\\s+Version=\"([^\"]+)\"");

            Assert.IsTrue(reference.Success,
                "tools/portable/CoreAI.Core.csproj must reference Microsoft.Extensions.AI explicitly.");
            Assert.AreEqual(MeaiFloor, reference.Groups[1].Value,
                "The portable build is the gate that runs on every CI push (job portable-core) and is what " +
                "actually catches an API that does not exist in the pinned version. Pinning it above " +
                "Assets/packages.config would make that gate test a version nobody ships.");
        }

        private static string PinnedVersion(string manifest, string packageId)
        {
            Match pin = Regex.Match(manifest,
                "id=\"" + Regex.Escape(packageId) + "\"\\s+version=\"([^\"]+)\"");
            Assert.IsTrue(pin.Success, $"Assets/packages.config does not pin {packageId} at all.");
            return pin.Groups[1].Value;
        }

        private static Version MajorMinorOf(string version)
        {
            Version parsed = Version.Parse(version);
            return new Version(parsed.Major, parsed.Minor);
        }

        /// <summary>
        /// The repository root, or an ignored test. The package also ships to games that only consume it,
        /// and there these source-layout checks have nothing to look at - ignoring is honest, failing is not.
        /// </summary>
        private static string RequireRepository()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "tools", "portable", "CoreAI.Core.csproj")) &&
                        File.Exists(Path.Combine(directory.FullName, "Assets", "packages.config")))
                    {
                        return directory.FullName;
                    }
                }
            }

            Assert.Ignore("Not a CoreAI checkout: the source-layout pins are not present to check.");
            return null;
        }
    }
}
