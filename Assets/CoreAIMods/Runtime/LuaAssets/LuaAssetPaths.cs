using System;

namespace CoreAI.LuaAssets
{
    /// <summary>
    /// Recognizes Lua/Luau source assets by file extension: the first-class <c>.lua</c>/<c>.luau</c>
    /// ScriptedImporter output, plus the legacy <c>.lua.txt</c> convention used by existing demo mods.
    /// </summary>
    public static class LuaAssetPaths
    {
        public static bool HasLuaExtension(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            return assetPath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                   assetPath.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) ||
                   assetPath.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase);
        }
    }
}
