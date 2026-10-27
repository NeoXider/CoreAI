using System.Text;

namespace CoreAI.CodeEditing
{

    public static class VsCodePalette
    {
        public const string Comment = "#6A9955";
        public const string String = "#CE9178";
        public const string Number = "#B5CEA8";
        public const string Keyword = "#569CD6";
        public const string Function = "#DCDCAA";
        public const string Type = "#4EC9B0";
        public const string Variable = "#9CDCFE";

        public const string InlineCode = "#48EEFF";
    }

    public static class SyntaxHighlightText
    {
        public static void AppendColored(StringBuilder builder, string text, string color)
        {
            builder.Append("<color=");
            builder.Append(color);
            builder.Append('>');
            builder.Append(Escape(text));
            builder.Append("</color>");
        }

        public static string Escape(string text) => text.Replace("&", "&​").Replace("<", "<​");
    }
}
