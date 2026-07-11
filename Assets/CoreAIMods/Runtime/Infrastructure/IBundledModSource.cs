using System.Collections.Generic;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// A single mod shipped with the game: its raw Lua source plus the identity parsed from the
    /// <c>@coreai</c> header. Bundled mods are read-only content packaged in the build; the
    /// <see cref="BundledModSeeder"/> installs/updates them into the writable
    /// <see cref="CoreAI.Ai.ILuaModSourceStore"/> on startup.
    /// </summary>
    public readonly struct BundledMod
    {
        /// <summary>Stable mod id (from the header, falling back to the asset name).</summary>
        public readonly string Id;

        /// <summary>The mod's Lua source, including its <c>@coreai</c> header.</summary>
        public readonly string Source;

        /// <summary>Declared semantic version (header <c>version:</c>), used for update decisions.</summary>
        public readonly string Version;

        /// <param name="id">Stable mod id.</param>
        /// <param name="source">Full Lua source.</param>
        /// <param name="version">Declared version string.</param>
        public BundledMod(string id, string source, string version)
        {
            Id = id ?? "";
            Source = source ?? "";
            Version = version ?? "";
        }
    }

    /// <summary>
    /// Supplies the mods bundled with a game build so they can be seeded into the writable store on
    /// first run (and updated on later runs). A host can ship a game with a ready-made set of mods by
    /// registering an implementation — e.g. <see cref="ResourcesBundledModSource"/> reading
    /// <c>Resources/CoreAIMods/*.lua</c>. Multiple sources can be combined by a host.
    /// </summary>
    public interface IBundledModSource
    {
        /// <summary>
        /// Origin marker stamped into seeded manifests (<see cref="CoreAI.Ai.LuaModManifest.Origin"/>),
        /// e.g. <c>resources</c>, <c>streamingassets</c>, <c>addressables:&lt;label&gt;</c>. Lets the
        /// seeder tell its own entries apart from user-authored ones so it never clobbers player mods.
        /// </summary>
        string Origin { get; }

        /// <summary>Enumerates the bundled mods available in the build. Never returns null.</summary>
        IReadOnlyList<BundledMod> Load();
    }
}