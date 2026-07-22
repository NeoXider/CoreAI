using CoreAI.LuaAssets;
using UnityEditor;

namespace CoreAI.Editor
{
    /// <summary>
    /// Hex colors for each <see cref="LuaTokenKind"/>, tuned separately for the Pro (dark) and Light
    /// editor skins so highlighted code stays readable in both.
    /// </summary>
    internal static class LuaSyntaxPalette
    {
        public static string GetColor(LuaTokenKind kind)
        {
            return EditorGUIUtility.isProSkin ? GetDarkColor(kind) : GetLightColor(kind);
        }

        private static string GetDarkColor(LuaTokenKind kind)
        {
            switch (kind)
            {
                case LuaTokenKind.Keyword: return "C586C0";
                case LuaTokenKind.Global: return "4FC1FF";
                case LuaTokenKind.String:
                case LuaTokenKind.LongString:
                case LuaTokenKind.InterpolatedString: return "CE9178";
                case LuaTokenKind.Comment: return "6A9955";
                case LuaTokenKind.Number: return "B5CEA8";
                case LuaTokenKind.FunctionCall: return "DCDCAA";
                case LuaTokenKind.TypeAnnotation: return "4EC9B0";
                case LuaTokenKind.Identifier: return "9CDCFE";
                case LuaTokenKind.Operator: return "D4D4D4";
                default: return "D4D4D4";
            }
        }

        private static string GetLightColor(LuaTokenKind kind)
        {
            switch (kind)
            {
                case LuaTokenKind.Keyword: return "AF00DB";
                case LuaTokenKind.Global: return "0070C1";
                case LuaTokenKind.String:
                case LuaTokenKind.LongString:
                case LuaTokenKind.InterpolatedString: return "A31515";
                case LuaTokenKind.Comment: return "008000";
                case LuaTokenKind.Number: return "098658";
                case LuaTokenKind.FunctionCall: return "795E26";
                case LuaTokenKind.TypeAnnotation: return "267F99";
                case LuaTokenKind.Identifier: return "001080";
                case LuaTokenKind.Operator: return "000000";
                default: return "000000";
            }
        }
    }
}
