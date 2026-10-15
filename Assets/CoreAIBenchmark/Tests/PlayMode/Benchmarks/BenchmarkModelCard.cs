using System;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Benchmarking;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Rasterizes the benchmark model card (<see cref="BenchmarkReportFormatter.ModelCardScene"/>) into a PNG:
    /// every primitive of the engine-free scene becomes a flat mesh or a <see cref="TextMesh"/> on a private
    /// layer far from the benchmark world, an orthographic camera maps one scene unit to
    /// <see cref="PixelsPerUnit"/> pixels, and everything is destroyed after the capture. Reports null when the
    /// player has no graphics device, so headless runs keep working; the SVG twin
    /// (<see cref="BenchmarkReportFormatter.ToModelCardSvg"/>) needs no GPU at all.
    /// </summary>
    public static class BenchmarkModelCard
    {
        /// <summary>Output pixels per scene unit: the 1600x900 card becomes a 3200x1800 PNG.</summary>
        public const int PixelsPerUnit = 2;

        // WHY: TextMesh vertex colours reach the shader unconverted while material colours are linearized, so in
        // a Linear-space project text would come out lighter than its token; texts get pre-linearized colours.
        private static bool LinearSpace => QualitySettings.activeColorSpace == ColorSpace.Linear;

        // WHY: far from any benchmark scene and on its own layer, so no world object leaks into the card and no
        // gameplay camera ever sees the card.
        private static readonly Vector3 Origin = new(-20000f, 20000f, 0f);

        // WHY: parallel target runs may capture cards in overlapping frames; each capture gets its own slot so
        // one card camera never sees another card on the shared layer.
        private const float SlotSpacing = 4000f;
        private static int _nextSlot;

        /// <summary>Renders the dark-theme model card; <paramref name="onPng"/> receives the PNG bytes or null.</summary>
        public static IEnumerator Capture(BenchmarkReport report, Action<byte[]> onPng)
        {
            return Capture(report, onPng, true);
        }

        /// <summary>Renders the model card in the dark or light theme; <paramref name="onPng"/> receives the PNG bytes or null.</summary>
        public static IEnumerator Capture(BenchmarkReport report, Action<byte[]> onPng, bool dark)
        {
            if (report == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                onPng?.Invoke(null);
                yield break;
            }

            GameObject root = null;
            Camera cam = null;
            RenderTexture rt = null;
            List<Object> owned = new();
            bool ready = false;
            byte[] png = null;
            Texture2D tex = null;
            RenderTexture prevActive = RenderTexture.active;
            // WHY: disposal at a render wait must release the capture rig and restore global state.
            try
            {
                try
                {
                    BenchmarkCardScene scene = BenchmarkReportFormatter.ModelCardScene(report);
                    int layer = FreeLayer();
                    root = new GameObject("BenchmarkModelCard") { layer = layer };
                    int slot = _nextSlot++ % 8;
                    root.transform.position = Origin + new Vector3(slot * SlotSpacing, 0f, 0f);

                    GameObject camGo = new("BenchmarkModelCardCamera") { layer = layer };
                    camGo.transform.SetParent(root.transform, false);
                    camGo.transform.localPosition = new Vector3((float)(scene.Width / 2), (float)(-scene.Height / 2), -100f);
                    camGo.transform.localRotation = Quaternion.identity;
                    cam = camGo.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = (float)(scene.Height / 2);
                    cam.nearClipPlane = 1f;
                    cam.farClipPlane = 300f;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = ToColor(CardInk.Surface, dark, false);
                    cam.cullingMask = 1 << layer;
                    cam.allowHDR = false;
                    cam.allowMSAA = true;

                    int width = (int)Math.Round(scene.Width * PixelsPerUnit);
                    int height = (int)Math.Round(scene.Height * PixelsPerUnit);
                    // WHY: the card already renders at 2x, so 4x MSAA gives clean edges at half the 8x memory cost.
                    rt = new RenderTexture(width, height, 24) { antiAliasing = 4 };
                    cam.targetTexture = rt;

                    Painter painter = new(root.transform, layer, dark, owned);
                    IReadOnlyList<CardShape> shapes = scene.Shapes;
                    for (int i = 0; i < shapes.Count; i++)
                    {
                        painter.Paint(shapes[i], 50f - i * 0.02f);
                    }

                    ready = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] model-card setup failed: {ex.Message}");
                }

                // WHY: Editor batch mode does not resume WaitForEndOfFrame; read the previous rendered frame.
                yield return Application.isBatchMode ? null : new WaitForEndOfFrame();

                try
                {
                    // WHY: a half-built card (setup threw midway) would still render; report no card instead.
                    if (ready && cam != null && rt != null)
                    {
                        RenderTexture.active = rt;
                        tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        tex.Apply();
                        png = tex.EncodeToPNG();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Benchmark] model-card capture failed: {ex.Message}");
                }
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (cam != null)
                {
                    cam.targetTexture = null;
                }

                Destroy(tex);
                Destroy(rt);
                Destroy(root);
                foreach (Object o in owned)
                {
                    Destroy(o);
                }
            }

            onPng?.Invoke(png);
        }

        private static void Destroy(Object o)
        {
            if (o != null)
            {
                Object.DestroyImmediate(o);
            }
        }

        /// <summary>Highest unnamed user layer, so the card camera culls everything else.</summary>
        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    return i;
                }
            }

            return 31;
        }

        private static Color ToColor(CardInk ink, bool dark, bool forVertexColor)
        {
            Color c = ColorUtility.TryParseHtmlString(BenchmarkCardScene.Hex(ink, dark), out Color parsed)
                ? parsed
                : Color.magenta;
            return forVertexColor && LinearSpace ? c.linear : c;
        }

        /// <summary>Turns scene primitives into child objects of the card root, one mesh or text each.</summary>
        private sealed class Painter
        {
            private const int ArcSegments = 8;
            private const int CircleSegments = 48;
            private const float CentreOffsetEm = 0.34f;

            private readonly Transform _root;
            private readonly int _layer;
            private readonly bool _dark;
            private readonly List<Object> _owned;
            private readonly Dictionary<CardInk, Material> _materials = new();
            private readonly Font _font;
            private readonly Shader _shader;

            public Painter(Transform root, int layer, bool dark, List<Object> owned)
            {
                _root = root;
                _layer = layer;
                _dark = dark;
                _owned = owned;
                _font = BuiltinFont();
                _shader = Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("Unlit/Color")
                          ?? Shader.Find("Sprites/Default");
            }

            public void Paint(CardShape s, float z)
            {
                switch (s.Kind)
                {
                    case CardShapeKind.Rect:
                        if (s.W > 0 && s.H > 0)
                        {
                            Polygon(RectOutline(s), s.Ink, z);
                        }

                        break;
                    case CardShapeKind.Circle:
                        Polygon(CircleOutline(s.X, s.Y, s.W), s.Ink, z);
                        break;
                    case CardShapeKind.Diamond:
                        Polygon(new List<Vector2>
                        {
                            new((float)s.X, (float)(s.Y - s.W)), new((float)(s.X + s.W), (float)s.Y),
                            new((float)s.X, (float)(s.Y + s.W)), new((float)(s.X - s.W), (float)s.Y)
                        }, s.Ink, z);
                        break;
                    case CardShapeKind.Line:
                        Polygon(LineOutline(s), s.Ink, z);
                        break;
                    case CardShapeKind.Text:
                        Text(s, z);
                        break;
                }
            }

            private static List<Vector2> RectOutline(CardShape s)
            {
                float x0 = (float)s.X;
                float y0 = (float)s.Y;
                float x1 = (float)(s.X + s.W);
                float y1 = (float)(s.Y + s.H);
                float r = Mathf.Min((float)s.Radius, (float)s.H / 2f, (float)s.W);
                float rl = s.RoundStart ? Mathf.Max(r, 0f) : 0f;
                float rr = s.RoundEnd ? Mathf.Max(r, 0f) : 0f;
                List<Vector2> pts = new();
                Corner(pts, x1 - rr, y0 + rr, rr, -90f);
                Corner(pts, x1 - rr, y1 - rr, rr, 0f);
                Corner(pts, x0 + rl, y1 - rl, rl, 90f);
                Corner(pts, x0 + rl, y0 + rl, rl, 180f);
                return pts;
            }

            /// <summary>Quarter arc (y down) starting at <paramref name="startDeg"/>; a single point when r is 0.</summary>
            private static void Corner(List<Vector2> pts, float cx, float cy, float r, float startDeg)
            {
                if (r <= 0f)
                {
                    pts.Add(new Vector2(cx, cy));
                    return;
                }

                for (int i = 0; i <= ArcSegments; i++)
                {
                    float a = (startDeg + 90f * i / ArcSegments) * Mathf.Deg2Rad;
                    pts.Add(new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r));
                }
            }

            private static List<Vector2> CircleOutline(double cx, double cy, double r)
            {
                List<Vector2> pts = new(CircleSegments);
                for (int i = 0; i < CircleSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / CircleSegments;
                    pts.Add(new Vector2((float)(cx + Math.Cos(a) * r), (float)(cy + Math.Sin(a) * r)));
                }

                return pts;
            }

            private static List<Vector2> LineOutline(CardShape s)
            {
                Vector2 a = new((float)s.X, (float)s.Y);
                Vector2 b = new((float)s.W, (float)s.H);
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-6f)
                {
                    d = Vector2.right;
                }

                Vector2 n = new Vector2(-d.y, d.x).normalized * ((float)s.StrokeWidth / 2f);
                return new List<Vector2> { a + n, b + n, b - n, a - n };
            }

            /// <summary>Fills a convex outline (scene coordinates, y down) as a fan with both windings.</summary>
            private void Polygon(List<Vector2> outline, CardInk ink, float z)
            {
                if (outline.Count < 3)
                {
                    return;
                }

                Vector2 centre = Vector2.zero;
                foreach (Vector2 p in outline)
                {
                    centre += p;
                }

                centre /= outline.Count;
                List<Vector3> verts = new(outline.Count + 1) { Local(centre, z) };
                foreach (Vector2 p in outline)
                {
                    verts.Add(Local(p, z));
                }

                List<int> tris = new(outline.Count * 6);
                for (int i = 0; i < outline.Count; i++)
                {
                    int a = i + 1;
                    int b = (i + 1) % outline.Count + 1;
                    tris.Add(0);
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(0);
                    tris.Add(b);
                    tris.Add(a);
                }

                Mesh mesh = new() { name = "BenchmarkModelCardShape" };
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                _owned.Add(mesh);

                GameObject go = Child("Shape");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = MaterialFor(ink);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            private void Text(CardShape s, float z)
            {
                if (string.IsNullOrEmpty(s.Text) && string.IsNullOrEmpty(s.Suffix))
                {
                    return;
                }

                float size = (float)s.Size;
                float suffixSize = (float)s.SuffixSize;
                float gap = (float)s.SuffixGap;
                bool hasSuffix = !string.IsNullOrEmpty(s.Suffix);
                float mainW = Measure(s.Text, size, s.Bold);
                float suffixW = hasSuffix ? Measure(s.Suffix, suffixSize, s.SuffixBold) : 0f;
                float total = mainW + (hasSuffix ? gap + suffixW : 0f);

                // WHY: layout widths are estimates; when the real font measures wider, shrink rather than overflow.
                if (s.MaxWidth > 0 && total > s.MaxWidth && total > 0f)
                {
                    float k = (float)s.MaxWidth / total;
                    size *= k;
                    suffixSize *= k;
                    gap *= k;
                    mainW *= k;
                    total *= k;
                }

                float startX = s.Anchor switch
                {
                    CardAnchor.Middle => (float)s.X - total / 2f,
                    CardAnchor.End => (float)s.X - total,
                    _ => (float)s.X
                };

                TextRun(s.Text, startX, (float)s.Y, size, s.Bold, s.Ink, z);
                if (hasSuffix)
                {
                    TextRun(s.Suffix, startX + mainW + gap, (float)s.Y, suffixSize, s.SuffixBold, s.SuffixInk, z);
                }
            }

            private void TextRun(string text, float x, float baseline, float size, bool bold, CardInk ink, float z)
            {
                if (string.IsNullOrEmpty(text) || size <= 0f)
                {
                    return;
                }

                int fontSize = Mathf.Clamp(Mathf.RoundToInt(size * PixelsPerUnit), 8, 300);
                GameObject go = Child("Text");
                go.transform.localPosition = Local(new Vector2(x, baseline - CentreOffsetEm * size), z);
                TextMesh tm = go.AddComponent<TextMesh>();
                if (_font != null)
                {
                    tm.font = _font;
                    MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        renderer.sharedMaterial = _font.material;
                    }
                }

                tm.richText = false;
                tm.text = text;
                tm.fontSize = fontSize;
                // WHY: a TextMesh em is characterSize * fontSize / 10 world units; one scene unit is one world unit.
                tm.characterSize = size / (fontSize * 0.1f);
                tm.anchor = TextAnchor.MiddleLeft;
                tm.alignment = TextAlignment.Left;
                tm.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
                tm.color = ToColor(ink, _dark, true);
            }

            /// <summary>Rendered width in scene units, from the real font's advances when it is available.</summary>
            private float Measure(string text, float size, bool bold)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return 0f;
                }

                if (_font == null || !_font.dynamic)
                {
                    return (float)BenchmarkCardScene.TextWidth(text, size, bold);
                }

                int fontSize = Mathf.Clamp(Mathf.RoundToInt(size * PixelsPerUnit), 8, 300);
                FontStyle style = bold ? FontStyle.Bold : FontStyle.Normal;
                _font.RequestCharactersInTexture(text, fontSize, style);
                float advance = 0f;
                foreach (char c in text)
                {
                    if (_font.GetCharacterInfo(c, out CharacterInfo info, fontSize, style))
                    {
                        advance += info.advance;
                    }
                }

                return advance * size / fontSize;
            }

            private GameObject Child(string name)
            {
                GameObject go = new(name) { layer = _layer };
                go.transform.SetParent(_root, false);
                return go;
            }

            private static Vector3 Local(Vector2 scenePoint, float z)
            {
                return new Vector3(scenePoint.x, -scenePoint.y, z);
            }

            private Material MaterialFor(CardInk ink)
            {
                if (_materials.TryGetValue(ink, out Material cached))
                {
                    return cached;
                }

                Material m = new(_shader) { name = "BenchmarkModelCard_" + ink };
                Color c = ToColor(ink, _dark, false);
                if (m.HasProperty("_BaseColor"))
                {
                    m.SetColor("_BaseColor", c);
                }

                if (m.HasProperty("_Color"))
                {
                    m.SetColor("_Color", c);
                }

                _materials[ink] = m;
                _owned.Add(m);
                return m;
            }

            private static Font BuiltinFont()
            {
                foreach (string name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
                {
                    try
                    {
                        Font font = Resources.GetBuiltinResource<Font>(name);
                        if (font != null)
                        {
                            return font;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // WHY: Unity renamed the built-in font; an unknown name throws instead of returning null.
                    }
                }

                return null;
            }
        }
    }
}
