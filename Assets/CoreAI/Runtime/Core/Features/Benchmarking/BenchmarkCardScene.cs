using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Colour role of a card mark or text. Layouts name roles, never hex values, so one layout renders in the
    /// light and the dark theme; <see cref="BenchmarkCardScene.Hex"/> resolves a role per theme.
    /// </summary>
    public enum CardInk
    {
        Surface,
        Ink,
        Ink2,
        Muted,
        Grid,
        Axis,
        Track,
        Series,
        SeriesTrack,
        Extreme,
        Good,
        Warning,
        Critical,
        Heat1,
        Heat2,
        Heat3,
        Heat4,
        Heat5,
        HeatText1,
        HeatText2,
        HeatText3,
        HeatText4,
        HeatText5
    }

    /// <summary>Horizontal text alignment relative to the text's x coordinate.</summary>
    public enum CardAnchor
    {
        Start,
        Middle,
        End
    }

    /// <summary>Primitive kinds a <see cref="BenchmarkCardScene"/> holds.</summary>
    public enum CardShapeKind
    {
        Rect,
        Circle,
        Diamond,
        Line,
        Text
    }

    /// <summary>Which theme an SVG export carries.</summary>
    public enum CardTheme
    {
        /// <summary>Light values plus a <c>prefers-color-scheme: dark</c> override, for Markdown embedding.</summary>
        Auto,
        Light,
        Dark
    }

    /// <summary>
    /// One primitive of a card. Coordinates are in scene units with the origin at the top-left corner and y
    /// growing downwards. Rect: <see cref="X"/>/<see cref="Y"/> top-left, <see cref="W"/>/<see cref="H"/> size.
    /// Circle and diamond: <see cref="X"/>/<see cref="Y"/> centre, <see cref="W"/> radius. Line: from
    /// (<see cref="X"/>, <see cref="Y"/>) to (<see cref="W"/>, <see cref="H"/>). Text: <see cref="Y"/> is the
    /// baseline.
    /// </summary>
    public sealed class CardShape
    {
        public CardShapeKind Kind { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }

        /// <summary>Corner radius of a rect, clamped to half its height and its width when drawn.</summary>
        public double Radius { get; set; }

        /// <summary>False squares the rect's left corners (a bar's baseline end).</summary>
        public bool RoundStart { get; set; } = true;

        /// <summary>False squares the rect's right corners.</summary>
        public bool RoundEnd { get; set; } = true;

        public CardInk Ink { get; set; }

        /// <summary>Stroke width of a line.</summary>
        public double StrokeWidth { get; set; } = 1;

        public string Text { get; set; } = string.Empty;

        /// <summary>Font size (em) of a text, in scene units.</summary>
        public double Size { get; set; }

        public bool Bold { get; set; }
        public CardAnchor Anchor { get; set; }

        /// <summary>Width the layout reserved for a text (0 = unbounded); rasterizers shrink text that measures wider.</summary>
        public double MaxWidth { get; set; }

        /// <summary>Equal-width digits, for numbers that align in a column.</summary>
        public bool Tabular { get; set; }

        /// <summary>Optional hover text (an SVG <c>&lt;title&gt;</c>); never the only route to a value.</summary>
        public string Tooltip { get; set; }

        /// <summary>Optional second run on the same baseline right after <see cref="Text"/> (a unit or a title).</summary>
        public string Suffix { get; set; } = string.Empty;

        public double SuffixSize { get; set; }
        public CardInk SuffixInk { get; set; }
        public bool SuffixBold { get; set; }

        /// <summary>Horizontal gap between <see cref="Text"/> and <see cref="Suffix"/>, in scene units.</summary>
        public double SuffixGap { get; set; }
    }

    /// <summary>
    /// Engine-free display list for the benchmark's cards and charts: layouts add primitives with colour
    /// roles, then the scene is written as a self-contained SVG (<see cref="ToSvg"/>) or handed to a Unity
    /// rasterizer for the model-card PNG. Keeping the layout here means the vector card, the PNG and the
    /// tests all see the same geometry.
    /// </summary>
    public sealed class BenchmarkCardScene
    {
        public const string FontFamily = "system-ui, -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif";

        private readonly List<CardShape> _shapes = new();

        public BenchmarkCardScene(double width, double height, string title, string description = null)
        {
            Width = width;
            Height = height;
            Title = title ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Accessible name of the chart (SVG <c>aria-label</c> and <c>&lt;title&gt;</c>).</summary>
        public string Title { get; set; }

        /// <summary>Longer accessible description (SVG <c>&lt;desc&gt;</c>).</summary>
        public string Description { get; set; }

        /// <summary>Primitives in paint order: later shapes cover earlier ones.</summary>
        public IReadOnlyList<CardShape> Shapes => _shapes;

        public CardShape Rect(double x, double y, double w, double h, CardInk ink, double radius = 0)
        {
            return Add(new CardShape { Kind = CardShapeKind.Rect, X = x, Y = y, W = w, H = h, Ink = ink, Radius = radius });
        }

        /// <summary>A horizontal bar growing from <paramref name="x"/>: square at the baseline, 4px-rounded data end.</summary>
        public CardShape Bar(double x, double y, double w, double h, CardInk ink, double radius = 4)
        {
            return Add(new CardShape
            {
                Kind = CardShapeKind.Rect, X = x, Y = y, W = w, H = h, Ink = ink, Radius = radius, RoundStart = false
            });
        }

        /// <summary>A filled circle; <paramref name="ring"/> &gt; 0 first lays a surface-coloured ring under it.</summary>
        public CardShape Circle(double cx, double cy, double r, CardInk ink, double ring = 0)
        {
            if (ring > 0)
            {
                Add(new CardShape { Kind = CardShapeKind.Circle, X = cx, Y = cy, W = r + ring, Ink = CardInk.Surface });
            }

            return Add(new CardShape { Kind = CardShapeKind.Circle, X = cx, Y = cy, W = r, Ink = ink });
        }

        /// <summary>A filled diamond (square rotated 45°) of half-diagonal <paramref name="r"/>, optionally ringed.</summary>
        public CardShape Diamond(double cx, double cy, double r, CardInk ink, double ring = 0)
        {
            if (ring > 0)
            {
                Add(new CardShape { Kind = CardShapeKind.Diamond, X = cx, Y = cy, W = r + ring * 1.41, Ink = CardInk.Surface });
            }

            return Add(new CardShape { Kind = CardShapeKind.Diamond, X = cx, Y = cy, W = r, Ink = ink });
        }

        public CardShape Line(double x1, double y1, double x2, double y2, CardInk ink, double width = 1)
        {
            return Add(new CardShape
            {
                Kind = CardShapeKind.Line, X = x1, Y = y1, W = x2, H = y2, Ink = ink, StrokeWidth = width
            });
        }

        /// <summary>
        /// A single-line text with its baseline at <paramref name="y"/>. When <paramref name="maxWidth"/> is set
        /// the text is truncated with an ellipsis so its estimated width fits.
        /// </summary>
        public CardShape Text(double x, double y, string text, double size, CardInk ink,
            CardAnchor anchor = CardAnchor.Start, bool bold = false, double maxWidth = 0)
        {
            string value = maxWidth > 0 ? Fit(text, size, bold, maxWidth) : text ?? string.Empty;
            return Add(new CardShape
            {
                Kind = CardShapeKind.Text, X = x, Y = y, Text = value, Size = size, Ink = ink, Anchor = anchor,
                Bold = bold, MaxWidth = maxWidth
            });
        }

        /// <summary>
        /// Two runs on one baseline — <paramref name="text"/> then <paramref name="suffix"/> after a
        /// <paramref name="gap"/> — laid out by the renderer, so a value and its unit never drift apart. With
        /// <paramref name="maxWidth"/> the suffix is truncated first.
        /// </summary>
        public CardShape TextWithSuffix(double x, double y, string text, double size, CardInk ink, bool bold,
            string suffix, double suffixSize, CardInk suffixInk, bool suffixBold, double gap,
            CardAnchor anchor = CardAnchor.Start, double maxWidth = 0)
        {
            string main = text ?? string.Empty;
            string tail = suffix ?? string.Empty;
            if (maxWidth > 0)
            {
                double mainW = TextWidth(main, size, bold);
                tail = Fit(tail, suffixSize, suffixBold, maxWidth - mainW - gap);
                if (tail == "…")
                {
                    tail = string.Empty;
                    main = Fit(main, size, bold, maxWidth);
                }
            }

            return Add(new CardShape
            {
                Kind = CardShapeKind.Text, X = x, Y = y, Text = main, Size = size, Ink = ink, Anchor = anchor,
                Bold = bold, MaxWidth = maxWidth, Suffix = tail, SuffixSize = suffixSize, SuffixInk = suffixInk,
                SuffixBold = suffixBold, SuffixGap = gap
            });
        }

        private CardShape Add(CardShape shape)
        {
            _shapes.Add(shape);
            return shape;
        }

        /// <summary>Ordinal heat bin 1..5 of a 0..100 score: &lt;50, 50–74.9, 75–89.9, 90–99.9, 100.</summary>
        public static int HeatBin(double score)
        {
            if (!double.IsFinite(score) || score < 50)
            {
                return 1;
            }

            if (score < 75)
            {
                return 2;
            }

            if (score < 90)
            {
                return 3;
            }

            return score < 99.95 ? 4 : 5;
        }

        /// <summary>Legend label of a heat bin.</summary>
        public static string HeatBinLabel(int bin)
        {
            return bin switch
            {
                5 => "100",
                4 => "90–99.9",
                3 => "75–89.9",
                2 => "50–74.9",
                _ => "<50"
            };
        }

        /// <summary>Fill role of a heat bin.</summary>
        public static CardInk HeatFill(int bin)
        {
            return bin switch
            {
                5 => CardInk.Heat5,
                4 => CardInk.Heat4,
                3 => CardInk.Heat3,
                2 => CardInk.Heat2,
                _ => CardInk.Heat1
            };
        }

        /// <summary>Text role that clears contrast on a heat bin's fill in either theme.</summary>
        public static CardInk HeatText(int bin)
        {
            return bin switch
            {
                5 => CardInk.HeatText5,
                4 => CardInk.HeatText4,
                3 => CardInk.HeatText3,
                2 => CardInk.HeatText2,
                _ => CardInk.HeatText1
            };
        }

        /// <summary>
        /// Resolves a colour role. Values come from the dataviz reference palette: categorical slot 1 (blue)
        /// for scores, slot 2 (orange) for the extreme tier, status steps for verdicts, and a five-step ordinal
        /// blue ramp for heat cells. Every set was run through the palette validator in both modes.
        /// </summary>
        public static string Hex(CardInk ink, bool dark)
        {
            return ink switch
            {
                CardInk.Surface => dark ? "#1a1a19" : "#fcfcfb",
                CardInk.Ink => dark ? "#ffffff" : "#0b0b0b",
                CardInk.Ink2 => dark ? "#c3c2b7" : "#52514e",
                CardInk.Muted => dark ? "#9a988f" : "#6f6e69",
                CardInk.Grid => dark ? "#2c2c2a" : "#e1e0d9",
                CardInk.Axis => dark ? "#383835" : "#c3c2b7",
                CardInk.Track => dark ? "#262624" : "#f0efec",
                CardInk.Series => dark ? "#3987e5" : "#2a78d6",
                CardInk.SeriesTrack => dark ? "#0d366b" : "#cde2fb",
                CardInk.Extreme => dark ? "#d95926" : "#eb6834",
                CardInk.Good => "#0ca30c",
                CardInk.Warning => "#fab219",
                CardInk.Critical => "#d03b3b",
                CardInk.Heat1 => dark ? "#184f95" : "#86b6ef",
                CardInk.Heat2 => dark ? "#256abf" : "#5598e7",
                CardInk.Heat3 => dark ? "#3987e5" : "#2a78d6",
                CardInk.Heat4 => dark ? "#6da7ec" : "#1c5cab",
                CardInk.Heat5 => dark ? "#9ec5f4" : "#104281",
                CardInk.HeatText1 => dark ? "#ffffff" : "#0b0b0b",
                CardInk.HeatText2 => dark ? "#ffffff" : "#0b0b0b",
                CardInk.HeatText3 => "#0b0b0b",
                CardInk.HeatText4 => dark ? "#0b0b0b" : "#ffffff",
                CardInk.HeatText5 => dark ? "#0b0b0b" : "#ffffff",
                _ => dark ? "#ffffff" : "#0b0b0b"
            };
        }

        // WHY: Helvetica/Arial advance widths (1/1000 em) for ASCII 32..126 — the metric-compatible family the
        // SVG font stack and Unity's built-in font fall back to, so estimates stay within a few percent.
        private static readonly short[] AsciiWidths =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
        };

        /// <summary>Estimated rendered width of a single-line text, for truncation and collision checks.</summary>
        public static double TextWidth(string text, double size, bool bold = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double units = 0;
            foreach (char c in text)
            {
                if (c >= 32 && c <= 126)
                {
                    units += AsciiWidths[c - 32];
                }
                else
                {
                    units += c switch
                    {
                        '·' => 278,
                        '–' => 556,
                        '—' => 1000,
                        '…' => 1000,
                        '×' => 584,
                        '≥' => 584,
                        _ => 600
                    };
                }
            }

            // WHY: bold faces run ~8% wider; the extra 3% covers Segoe UI / system-ui metrics.
            return units / 1000.0 * size * (bold ? 1.08 : 1.0) * 1.03;
        }

        /// <summary>Truncates <paramref name="text"/> with an ellipsis until its estimated width fits.</summary>
        public static string Fit(string text, double size, bool bold, double maxWidth)
        {
            string value = text ?? string.Empty;
            if (maxWidth <= 0 || TextWidth(value, size, bold) <= maxWidth)
            {
                return value;
            }

            for (int len = value.Length - 1; len > 0; len--)
            {
                string candidate = value.Substring(0, len).TrimEnd() + "…";
                if (TextWidth(candidate, size, bold) <= maxWidth)
                {
                    return candidate;
                }
            }

            return "…";
        }

        /// <summary>
        /// Writes the scene as a self-contained SVG. <see cref="CardTheme.Auto"/> carries the light values and
        /// a <c>prefers-color-scheme: dark</c> override, so one file reads correctly in both GitHub themes.
        /// <paramref name="classPrefix"/> scopes the style rules when the SVG is inlined into an HTML page.
        /// </summary>
        public string ToSvg(CardTheme theme = CardTheme.Auto, string classPrefix = "bc")
        {
            string p = string.IsNullOrEmpty(classPrefix) ? "bc" : classPrefix;
            HashSet<CardInk> fills = new();
            HashSet<CardInk> strokes = new();
            foreach (CardShape s in _shapes)
            {
                if (s.Kind == CardShapeKind.Line)
                {
                    strokes.Add(s.Ink);
                }
                else
                {
                    fills.Add(s.Ink);
                }

                if (s.Kind == CardShapeKind.Text && !string.IsNullOrEmpty(s.Suffix))
                {
                    fills.Add(s.SuffixInk);
                }
            }

            fills.Add(CardInk.Surface);
            StringBuilder sb = new();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(Width))
                .Append("\" height=\"").Append(N(Height)).Append("\" viewBox=\"0 0 ").Append(N(Width)).Append(' ')
                .Append(N(Height)).Append("\" font-family=\"").Append(Xml(FontFamily))
                .Append("\" role=\"img\" aria-label=\"").Append(Xml(Title)).Append("\">");
            sb.Append("<title>").Append(Xml(Title)).Append("</title>");
            if (!string.IsNullOrEmpty(Description))
            {
                sb.Append("<desc>").Append(Xml(Description)).Append("</desc>");
            }

            sb.Append("<style>");
            AppendRules(sb, p, fills, strokes, theme == CardTheme.Dark);
            sb.Append('.').Append(p).Append("-tn{font-variant-numeric:tabular-nums}");
            if (theme == CardTheme.Auto)
            {
                sb.Append("@media (prefers-color-scheme: dark){");
                AppendRules(sb, p, fills, strokes, true);
                sb.Append('}');
            }

            sb.Append("</style>");
            sb.Append("<rect width=\"100%\" height=\"100%\" rx=\"10\" class=\"").Append(p).Append("-f")
                .Append((int)CardInk.Surface).Append("\"/>");

            foreach (CardShape s in _shapes)
            {
                AppendShape(sb, s, p);
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static void AppendRules(StringBuilder sb, string p, HashSet<CardInk> fills, HashSet<CardInk> strokes,
            bool dark)
        {
            foreach (CardInk ink in fills)
            {
                sb.Append('.').Append(p).Append("-f").Append((int)ink).Append("{fill:").Append(Hex(ink, dark))
                    .Append('}');
            }

            foreach (CardInk ink in strokes)
            {
                sb.Append('.').Append(p).Append("-s").Append((int)ink).Append("{stroke:").Append(Hex(ink, dark))
                    .Append('}');
            }
        }

        private static void AppendShape(StringBuilder sb, CardShape s, string p)
        {
            string tooltip = string.IsNullOrEmpty(s.Tooltip) ? null : "<title>" + Xml(s.Tooltip) + "</title>";
            switch (s.Kind)
            {
                case CardShapeKind.Rect:
                    AppendRect(sb, s, p, tooltip);
                    break;
                case CardShapeKind.Circle:
                    sb.Append("<circle cx=\"").Append(N(s.X)).Append("\" cy=\"").Append(N(s.Y)).Append("\" r=\"")
                        .Append(N(s.W)).Append("\" class=\"").Append(p).Append("-f").Append((int)s.Ink).Append('"');
                    Close(sb, "circle", tooltip);
                    break;
                case CardShapeKind.Diamond:
                    sb.Append("<path d=\"M").Append(N(s.X)).Append(',').Append(N(s.Y - s.W)).Append(" L")
                        .Append(N(s.X + s.W)).Append(',').Append(N(s.Y)).Append(" L").Append(N(s.X)).Append(',')
                        .Append(N(s.Y + s.W)).Append(" L").Append(N(s.X - s.W)).Append(',').Append(N(s.Y))
                        .Append(" Z\" class=\"").Append(p).Append("-f").Append((int)s.Ink).Append('"');
                    Close(sb, "path", tooltip);
                    break;
                case CardShapeKind.Line:
                    sb.Append("<line x1=\"").Append(N(s.X)).Append("\" y1=\"").Append(N(s.Y)).Append("\" x2=\"")
                        .Append(N(s.W)).Append("\" y2=\"").Append(N(s.H)).Append("\" stroke-width=\"")
                        .Append(N(s.StrokeWidth)).Append("\" class=\"").Append(p).Append("-s").Append((int)s.Ink)
                        .Append('"');
                    Close(sb, "line", tooltip);
                    break;
                case CardShapeKind.Text:
                    sb.Append("<text x=\"").Append(N(s.X)).Append("\" y=\"").Append(N(s.Y)).Append("\" font-size=\"")
                        .Append(N(s.Size)).Append('"');
                    if (s.Bold)
                    {
                        sb.Append(" font-weight=\"600\"");
                    }

                    if (s.Anchor != CardAnchor.Start)
                    {
                        sb.Append(" text-anchor=\"").Append(s.Anchor == CardAnchor.Middle ? "middle" : "end")
                            .Append('"');
                    }

                    sb.Append(" class=\"").Append(p).Append("-f").Append((int)s.Ink);
                    if (s.Tabular)
                    {
                        sb.Append(' ').Append(p).Append("-tn");
                    }

                    sb.Append("\">").Append(tooltip ?? string.Empty).Append(Xml(s.Text));
                    if (!string.IsNullOrEmpty(s.Suffix))
                    {
                        sb.Append("<tspan dx=\"").Append(N(s.SuffixGap)).Append("\" font-size=\"")
                            .Append(N(s.SuffixSize)).Append("\" font-weight=\"").Append(s.SuffixBold ? "600" : "400")
                            .Append("\" class=\"").Append(p).Append("-f").Append((int)s.SuffixInk).Append("\">")
                            .Append(Xml(s.Suffix)).Append("</tspan>");
                    }

                    sb.Append("</text>");
                    break;
            }
        }

        private static void AppendRect(StringBuilder sb, CardShape s, string p, string tooltip)
        {
            double w = s.W < 0 ? 0 : s.W;
            double h = s.H < 0 ? 0 : s.H;
            double r = s.Radius;
            if (r > h / 2)
            {
                r = h / 2;
            }

            if (r > w)
            {
                r = w;
            }

            if (r <= 0 || (s.RoundStart && s.RoundEnd))
            {
                sb.Append("<rect x=\"").Append(N(s.X)).Append("\" y=\"").Append(N(s.Y)).Append("\" width=\"")
                    .Append(N(w)).Append("\" height=\"").Append(N(h)).Append('"');
                if (r > 0)
                {
                    sb.Append(" rx=\"").Append(N(r)).Append('"');
                }

                sb.Append(" class=\"").Append(p).Append("-f").Append((int)s.Ink).Append('"');
                Close(sb, "rect", tooltip);
                return;
            }

            double rl = s.RoundStart ? r : 0;
            double rr = s.RoundEnd ? r : 0;
            double x0 = s.X;
            double x1 = s.X + w;
            double y0 = s.Y;
            double y1 = s.Y + h;
            sb.Append("<path d=\"M").Append(N(x0 + rl)).Append(',').Append(N(y0)).Append(" H").Append(N(x1 - rr));
            if (rr > 0)
            {
                sb.Append(" A").Append(N(rr)).Append(',').Append(N(rr)).Append(" 0 0 1 ").Append(N(x1)).Append(',')
                    .Append(N(y0 + rr));
            }

            sb.Append(" V").Append(N(y1 - rr));
            if (rr > 0)
            {
                sb.Append(" A").Append(N(rr)).Append(',').Append(N(rr)).Append(" 0 0 1 ").Append(N(x1 - rr))
                    .Append(',').Append(N(y1));
            }

            sb.Append(" H").Append(N(x0 + rl));
            if (rl > 0)
            {
                sb.Append(" A").Append(N(rl)).Append(',').Append(N(rl)).Append(" 0 0 1 ").Append(N(x0)).Append(',')
                    .Append(N(y1 - rl));
            }

            sb.Append(" V").Append(N(y0 + rl));
            if (rl > 0)
            {
                sb.Append(" A").Append(N(rl)).Append(',').Append(N(rl)).Append(" 0 0 1 ").Append(N(x0 + rl))
                    .Append(',').Append(N(y0));
            }

            sb.Append(" Z\" class=\"").Append(p).Append("-f").Append((int)s.Ink).Append('"');
            Close(sb, "path", tooltip);
        }

        private static void Close(StringBuilder sb, string element, string tooltip)
        {
            if (tooltip == null)
            {
                sb.Append("/>");
                return;
            }

            sb.Append('>').Append(tooltip).Append("</").Append(element).Append('>');
        }

        /// <summary>Formats a coordinate with at most two decimals, invariant culture.</summary>
        public static string N(double v)
        {
            if (!double.IsFinite(v))
            {
                v = 0;
            }

            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Escapes text for XML content and attribute values; drops characters XML 1.0 forbids.</summary>
        public static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            StringBuilder sb = new(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&':
                        sb.Append("&amp;");
                        break;
                    case '<':
                        sb.Append("&lt;");
                        break;
                    case '>':
                        sb.Append("&gt;");
                        break;
                    case '"':
                        sb.Append("&quot;");
                        break;
                    default:
                        if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r')
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }
    }
}
