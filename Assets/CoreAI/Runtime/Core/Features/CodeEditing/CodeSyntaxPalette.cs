namespace CoreAI.CodeEditing
{
    public interface ISyntaxHighlighter { string[] Highlight(string[] lines); }
    public sealed class CodeSyntaxPalette
    {
        public static readonly CodeSyntaxPalette Dark = new();
        public static CodeSyntaxPalette Load(string language) => Dark;
        public string Comment { get; set; } = "#8A929E";
        public string String { get; set; } = "#98C379";
        public string Number { get; set; } = "#D19A66";
        public string Keyword { get; set; } = "#61AFEF";
        public string Function { get; set; } = "#E5C07B";
        public string Type { get; set; } = "#56B6C2";
        public string Variable { get; set; } = "#DCE6EF";
        public string Bracket { get; set; } = "#A8C7FA";
    }
}
