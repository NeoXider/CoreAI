using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// <see cref="IBundledModSource"/> that reads bundled mods from a <c>Resources/&lt;folder&gt;</c>
    /// directory (default <c>Resources/CoreAIMods</c>). Every <c>.lua</c> asset there — imported as a
    /// <see cref="TextAsset"/> by the project's Lua scripted importer — is offered as a
    /// <see cref="BundledMod"/> whose id/version come from its <c>@coreai</c> header. Because Resources
    /// ship inside the built player, this is how a game is packaged with ready-made mods out of the box.
    /// </summary>
    public sealed class ResourcesBundledModSource : IBundledModSource
    {
        /// <summary>Default resource sub-folder scanned for bundled mods.</summary>
        public const string DefaultResourceFolder = "CoreAIMods";

        private readonly string _folder;

        /// <param name="resourceFolder">
        /// Resources sub-folder to scan (relative to any <c>Resources/</c> root). Defaults to
        /// <see cref="DefaultResourceFolder"/>.
        /// </param>
        public ResourcesBundledModSource(string resourceFolder = DefaultResourceFolder)
        {
            _folder = string.IsNullOrWhiteSpace(resourceFolder) ? DefaultResourceFolder : resourceFolder.Trim();
        }

        /// <inheritdoc />
        public string Origin => "resources";

        /// <inheritdoc />
        public IReadOnlyList<BundledMod> Load()
        {
            List<BundledMod> mods = new();
            TextAsset[] assets = Resources.LoadAll<TextAsset>(_folder);
            if (assets == null)
            {
                return mods;
            }

            HashSet<string> seen = new(System.StringComparer.OrdinalIgnoreCase);
            foreach (TextAsset asset in assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.text))
                {
                    continue;
                }

                // WHY: Per-mod isolation: one asset with a malformed header must not throw out of Load and
                // kill the seeding of every other bundled mod (and the Hub list with it).
                try
                {
                    LuaModHeader header = LuaModHeader.Parse(asset.text, asset.name);
                    string id = (header.Id ?? "").Trim();
                    if (id.Length == 0 || !seen.Add(id))
                    {
                        continue; // no id, or a duplicate id already taken by an earlier asset
                    }

                    mods.Add(new BundledMod(id, asset.text, header.Version));
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning(
                        $"[ResourcesBundledModSource] Skipping bundled mod asset '{asset.name}': {ex.Message}");
                }
            }

            return mods;
        }
    }
}
