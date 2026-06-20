using System.IO;
using UnityEngine;
using UnityEditor.AssetImporters;

namespace CoreAI.Editor
{
    /// <summary>
    /// Imports <c>*.lua</c> files as first-class <see cref="TextAsset"/>s so Lua mods and scripts can be
    /// authored with a proper <c>.lua</c> extension (editor recognition, drag-and-drop references) instead
    /// of the <c>.lua.txt</c> workaround. The imported asset behaves like any other <see cref="TextAsset"/>:
    /// <c>myLuaAsset.text</c> returns the source, and it can be assigned to a <c>TextAsset</c> field.
    /// </summary>
    /// <remarks>
    /// Unity does not import <c>.lua</c> as a <see cref="TextAsset"/> by default; this importer adds that.
    /// If another package in the project already registers an importer for the <c>lua</c> extension, Unity
    /// reports a duplicate-importer error — delete one of them. The importer is text-only and has no
    /// dependency on MoonSharp, so it works in no-Lua builds too.
    /// </remarks>
    [ScriptedImporter(version: 1, ext: "lua")]
    public sealed class LuaScriptedImporter : ScriptedImporter
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
