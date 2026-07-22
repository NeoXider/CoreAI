using System.IO;
using UnityEngine;
using UnityEditor.AssetImporters;

namespace CoreAI.Editor
{
    /// <summary>
    /// Imports <c>*.luau</c> files as first-class <see cref="TextAsset"/>s, mirroring
    /// <c>LuaScriptedImporter</c> (Assets/CoreAiUnity/Editor) which already claims the <c>.lua</c>
    /// extension. Kept as a separate importer instead of extending that one: <c>.lua</c> is owned by the
    /// CoreAiUnity host package, not CoreAIMods.
    /// </summary>
    /// <remarks>
    /// Text-only, no dependency on the Lua runtime, so it works in no-Lua (<c>COREAI_NO_LUA</c>) builds too.
    /// </remarks>
    [ScriptedImporter(1, "luau")]
    public sealed class LuauScriptedImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            string source = File.ReadAllText(ctx.assetPath);
            TextAsset asset = new(source);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }
}
