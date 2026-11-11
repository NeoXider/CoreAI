using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// Parity of the runtime interpreter with the editor importer: the same UXML and USS text is
    /// imported as real assets and also interpreted at runtime, both trees are laid out in the same
    /// panel, and every supported property must resolve to the same value on every named element.
    /// This is what pins the interpreter's selector matching, specificity, <c>var()</c>, shorthands and
    /// value conversion to UI Toolkit's own behaviour.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiImporterParityEditModeTests
    {
        private const string Folder = "Assets/__CoreAiRuntimeUiParity";

        private const string ParityUxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
            "  <ui:VisualElement name=\"panel\" class=\"panel themed\">\n" +
            "    <ui:Label name=\"title\" class=\"title\" text=\"Inventory\"/>\n" +
            "    <ui:VisualElement name=\"row\" class=\"row\">\n" +
            "      <ui:Button name=\"slot-a\" class=\"slot\" text=\"A\"/>\n" +
            "      <ui:Button name=\"slot-b\" class=\"slot selected\" text=\"B\"/>\n" +
            "      <ui:VisualElement name=\"wrap\">\n" +
            "        <ui:Label name=\"deep\" class=\"count\" text=\"3\"/>\n" +
            "      </ui:VisualElement>\n" +
            "    </ui:VisualElement>\n" +
            "    <ui:Label name=\"hidden\" class=\"gone\" text=\"x\"/>\n" +
            "    <ui:Label name=\"ghost\" class=\"ghost\" text=\"y\"/>\n" +
            "    <ui:ProgressBar name=\"hp\" low-value=\"0\" high-value=\"100\" value=\"40\" title=\"HP\"/>\n" +
            "    <ui:Slider name=\"vol\" low-value=\"0\" high-value=\"10\" value=\"3\"/>\n" +
            "    <ui:Toggle name=\"mute\" value=\"true\"/>\n" +
            "    <ui:TextField name=\"nick\" value=\"Steve\"/>\n" +
            "  </ui:VisualElement>\n" +
            "</ui:UXML>\n";

        private const string ParityUss =
            ".themed { --accent: #ffcc00; --gap: 6px; --size: 48px; }\n" +
            ".panel { position: absolute; left: 10px; top: 20px; width: 400px; padding: 8px 12px; " +
            "background-color: rgba(10, 20, 30, 0.8); border-width: 1px 2px 3px 4px; " +
            "border-color: red green blue #fff; border-radius: 4px 8px; flex-direction: column; }\n" +
            ".title { font-size: 22px; color: var(--accent); -unity-font-style: bold; -unity-text-align: middle-center; " +
            "letter-spacing: 1px; margin-bottom: var(--gap); white-space: nowrap; }\n" +
            ".row { flex-direction: row; justify-content: space-between; align-items: center; height: 60px; }\n" +
            ".slot { width: var(--size); height: var(--size); margin: 0 2px; border-width: 2px; " +
            "border-color: #888; opacity: 0.75; transition: opacity 0.2s ease-out, scale 100ms linear 0.05s; }\n" +
            ".slot.selected { border-color: var(--accent); scale: 1.1; opacity: 1; }\n" +
            "#slot-a { rotate: 15deg; translate: 2px -3px; transform-origin: left top; }\n" +
            ".row > .slot { flex-shrink: 0; }\n" +
            ".row .count { font-size: 12px; color: rgb(0, 200, 0); }\n" +
            "#wrap > Label { -unity-text-outline-width: 1px; -unity-text-outline-color: black; }\n" +
            "Label { color: rgb(240, 240, 240); }\n" +
            ".gone { display: none; }\n" +
            ".ghost { visibility: hidden; flex-grow: 1; flex-basis: 30%; max-width: 50%; min-height: 10px; }\n" +
            "ProgressBar { flex: 1; margin: 1px 2px 3px 4px; }\n" +
            "Slider { align-self: flex-end; padding: 3px; }\n";

        private string _uxmlPath;
        private string _ussPath;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "__CoreAiRuntimeUiParity");
            }

            _uxmlPath = Folder + "/Parity.uxml";
            _ussPath = Folder + "/Parity.uss";
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            File.WriteAllText(Path.Combine(projectRoot, _ussPath), ParityUss, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(projectRoot, _uxmlPath), ParityUxml, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(_ussPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(_uxmlPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void SupportedSubset_ResolvesExactlyAsTheEditorImporterDoes()
        {
            StyleSheet importedSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(_ussPath);
            VisualTreeAsset importedTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(_uxmlPath);
            Assert.IsNotNull(importedSheet, "The editor importer produced no StyleSheet.");
            Assert.IsNotNull(importedTree, "The editor importer produced no VisualTreeAsset.");
            Assert.IsFalse(importedSheet.importedWithErrors, "The fixture USS must be valid for the importer.");

            using RuntimeUiTestPanel panel = new();
            RuntimeUiResult result = panel.Host.CreateScreen("parity", ParityUxml, ParityUss);
            Assert.IsTrue(result.Success, result.ToString());
            Assert.AreEqual(0, result.Diagnostics.Count,
                "The fixture is inside the supported subset:\n" + string.Join("\n", result.Diagnostics));

            VisualElement importerLayer = Fill(new VisualElement { name = "importer-layer", pickingMode = PickingMode.Ignore });
            TemplateContainer importerRoot = importedTree.CloneTree();
            Fill(importerRoot);
            importerRoot.styleSheets.Add(importedSheet);
            importerLayer.Add(importerRoot);
            panel.Root.Add(importerLayer);
            panel.Update();
            panel.Update();

            List<string> mismatches = new();
            List<string> names = new()
            {
                "panel", "title", "row", "slot-a", "slot-b", "wrap", "deep", "hidden", "ghost", "hp", "vol", "mute",
                "nick"
            };
            for (int i = 0; i < names.Count; i++)
            {
                VisualElement interpreted = result.Screen.GetElement(names[i]).VisualElement;
                VisualElement imported = importerRoot.Q(names[i]);
                Assert.IsNotNull(imported, names[i] + " is missing from the imported tree.");
                Compare(names[i], interpreted, imported, mismatches);
            }

            CompareAttributes(result.Screen, importerRoot, mismatches);
            Assert.IsEmpty(mismatches, "Interpreter vs editor importer:\n" + string.Join("\n", mismatches));
        }

        private static VisualElement Fill(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0f;
            element.style.top = 0f;
            element.style.right = 0f;
            element.style.bottom = 0f;
            return element;
        }

        private static void Compare(string name, VisualElement a, VisualElement b, List<string> mismatches)
        {
            IResolvedStyle x = a.resolvedStyle;
            IResolvedStyle y = b.resolvedStyle;
            Float(name, "width", x.width, y.width, mismatches);
            Float(name, "height", x.height, y.height, mismatches);
            Float(name, "left", x.left, y.left, mismatches);
            Float(name, "top", x.top, y.top, mismatches);
            Float(name, "margin-left", x.marginLeft, y.marginLeft, mismatches);
            Float(name, "margin-top", x.marginTop, y.marginTop, mismatches);
            Float(name, "margin-right", x.marginRight, y.marginRight, mismatches);
            Float(name, "margin-bottom", x.marginBottom, y.marginBottom, mismatches);
            Float(name, "padding-left", x.paddingLeft, y.paddingLeft, mismatches);
            Float(name, "padding-top", x.paddingTop, y.paddingTop, mismatches);
            Float(name, "padding-right", x.paddingRight, y.paddingRight, mismatches);
            Float(name, "padding-bottom", x.paddingBottom, y.paddingBottom, mismatches);
            Float(name, "border-left-width", x.borderLeftWidth, y.borderLeftWidth, mismatches);
            Float(name, "border-top-width", x.borderTopWidth, y.borderTopWidth, mismatches);
            Float(name, "border-right-width", x.borderRightWidth, y.borderRightWidth, mismatches);
            Float(name, "border-bottom-width", x.borderBottomWidth, y.borderBottomWidth, mismatches);
            Colour(name, "border-left-color", x.borderLeftColor, y.borderLeftColor, mismatches);
            Colour(name, "border-top-color", x.borderTopColor, y.borderTopColor, mismatches);
            Colour(name, "border-right-color", x.borderRightColor, y.borderRightColor, mismatches);
            Colour(name, "border-bottom-color", x.borderBottomColor, y.borderBottomColor, mismatches);
            Float(name, "border-top-left-radius", x.borderTopLeftRadius, y.borderTopLeftRadius, mismatches);
            Float(name, "border-top-right-radius", x.borderTopRightRadius, y.borderTopRightRadius, mismatches);
            Float(name, "border-bottom-right-radius", x.borderBottomRightRadius, y.borderBottomRightRadius, mismatches);
            Float(name, "border-bottom-left-radius", x.borderBottomLeftRadius, y.borderBottomLeftRadius, mismatches);
            Colour(name, "background-color", x.backgroundColor, y.backgroundColor, mismatches);
            Colour(name, "color", x.color, y.color, mismatches);
            Float(name, "font-size", x.fontSize, y.fontSize, mismatches);
            Float(name, "opacity", x.opacity, y.opacity, mismatches);
            Same(name, "display", x.display, y.display, mismatches);
            Same(name, "visibility", x.visibility, y.visibility, mismatches);
            Same(name, "position", x.position, y.position, mismatches);
            Same(name, "flex-direction", x.flexDirection, y.flexDirection, mismatches);
            Same(name, "flex-wrap", x.flexWrap, y.flexWrap, mismatches);
            Float(name, "flex-grow", x.flexGrow, y.flexGrow, mismatches);
            Float(name, "flex-shrink", x.flexShrink, y.flexShrink, mismatches);
            Same(name, "flex-basis", x.flexBasis.ToString(), y.flexBasis.ToString(), mismatches);
            Same(name, "max-width", x.maxWidth.ToString(), y.maxWidth.ToString(), mismatches);
            Same(name, "min-height", x.minHeight.ToString(), y.minHeight.ToString(), mismatches);
            Same(name, "align-items", x.alignItems, y.alignItems, mismatches);
            Same(name, "align-self", x.alignSelf, y.alignSelf, mismatches);
            Same(name, "justify-content", x.justifyContent, y.justifyContent, mismatches);
            Same(name, "-unity-font-style", x.unityFontStyleAndWeight, y.unityFontStyleAndWeight, mismatches);
            Same(name, "-unity-text-align", x.unityTextAlign, y.unityTextAlign, mismatches);
            Same(name, "white-space", x.whiteSpace, y.whiteSpace, mismatches);
            Float(name, "letter-spacing", x.letterSpacing, y.letterSpacing, mismatches);
            Float(name, "-unity-text-outline-width", x.unityTextOutlineWidth, y.unityTextOutlineWidth, mismatches);
            Colour(name, "-unity-text-outline-color", x.unityTextOutlineColor, y.unityTextOutlineColor, mismatches);
            Vector(name, "translate", x.translate, y.translate, mismatches);
            Vector(name, "scale", x.scale.value, y.scale.value, mismatches);
            Float(name, "rotate", x.rotate.angle.ToDegrees(), y.rotate.angle.ToDegrees(), mismatches);
            Vector(name, "transform-origin", x.transformOrigin, y.transformOrigin, mismatches);
            Same(name, "transition-property", Join(x.transitionProperty), Join(y.transitionProperty), mismatches);
            Same(name, "transition-duration", Seconds(x.transitionDuration), Seconds(y.transitionDuration), mismatches);
            Same(name, "transition-timing-function", Join(x.transitionTimingFunction),
                Join(y.transitionTimingFunction), mismatches);
            Same(name, "transition-delay", Seconds(x.transitionDelay), Seconds(y.transitionDelay), mismatches);
        }

        private static void CompareAttributes(RuntimeUiScreen screen, VisualElement imported, List<string> mismatches)
        {
            ProgressBar hp = imported.Q<ProgressBar>("hp");
            ProgressBar hpRuntime = (ProgressBar)screen.GetElement("hp").VisualElement;
            Float("hp", "value", hpRuntime.value, hp.value, mismatches);
            Same("hp", "title", hpRuntime.title, hp.title, mismatches);
            Slider vol = imported.Q<Slider>("vol");
            Slider volRuntime = (Slider)screen.GetElement("vol").VisualElement;
            Float("vol", "value", volRuntime.value, vol.value, mismatches);
            Float("vol", "high-value", volRuntime.highValue, vol.highValue, mismatches);
            Same("mute", "value", ((Toggle)screen.GetElement("mute").VisualElement).value,
                imported.Q<Toggle>("mute").value, mismatches);
            Same("nick", "value", ((TextField)screen.GetElement("nick").VisualElement).value,
                imported.Q<TextField>("nick").value, mismatches);
            Same("title", "text", ((Label)screen.GetElement("title").VisualElement).text,
                imported.Q<Label>("title").text, mismatches);
        }

        private static void Float(string name, string property, float interpreted, float imported, List<string> mismatches)
        {
            bool bothNaN = float.IsNaN(interpreted) && float.IsNaN(imported);
            if (!bothNaN && Math.Abs(interpreted - imported) > 0.01f)
            {
                mismatches.Add(name + " " + property + ": " + interpreted + " vs " + imported);
            }
        }

        private static void Colour(string name, string property, Color interpreted, Color imported, List<string> mismatches)
        {
            if (Math.Abs(interpreted.r - imported.r) > 0.003f || Math.Abs(interpreted.g - imported.g) > 0.003f
                || Math.Abs(interpreted.b - imported.b) > 0.003f || Math.Abs(interpreted.a - imported.a) > 0.003f)
            {
                mismatches.Add(name + " " + property + ": " + interpreted + " vs " + imported);
            }
        }

        private static void Vector(string name, string property, Vector3 interpreted, Vector3 imported,
            List<string> mismatches)
        {
            if ((interpreted - imported).sqrMagnitude > 0.0001f)
            {
                mismatches.Add(name + " " + property + ": " + interpreted + " vs " + imported);
            }
        }

        private static void Same<T>(string name, string property, T interpreted, T imported, List<string> mismatches)
        {
            if (!EqualityComparer<T>.Default.Equals(interpreted, imported))
            {
                mismatches.Add(name + " " + property + ": " + interpreted + " vs " + imported);
            }
        }

        /// <summary>Durations in seconds: the interpreter stores <c>100ms</c> as 0.1 s, which UI Toolkit treats identically.</summary>
        private static string Seconds(IEnumerable<TimeValue> items)
        {
            StringBuilder builder = new();
            foreach (TimeValue item in items)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                float seconds = item.unit == TimeUnit.Millisecond ? item.value / 1000f : item.value;
                builder.Append(seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string Join<T>(IEnumerable<T> items)
        {
            StringBuilder builder = new();
            foreach (T item in items)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(item);
            }

            return builder.ToString();
        }
    }
}
