using System.Collections.Generic;
using System.Linq;
using CoreAI.ChatMarkdown;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ChatMarkdown
{
    /// <summary>
    /// The real backend over <c>com.neoxider.markdownrenderer</c>; compiles only while the package is installed.
    /// Pins what the chat relies on: registration, the shipped style sheet, headings/lists/code blocks, a streaming
    /// unterminated fence still rendering as code, images shown as links (nothing fetched) and rich-text tags shown
    /// instead of applied.
    /// </summary>
    [TestFixture]
    public sealed class NeoxiderMarkdownRendererEditModeTests
    {
        private ICoreAiMarkdownRenderer _registered;

        [SetUp]
        public void SetUp()
        {
            _registered = CoreAiMarkdownRendererRegistry.Current;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiMarkdownRendererRegistry.Register(_registered);
        }

        [Test]
        public void Registration_PutsTheBackendIntoTheRegistry()
        {
            CoreAiMarkdownRendererRegistry.Register(null);
            NeoxiderMarkdownRendererRegistration.Register();
            Assert.IsInstanceOf<NeoxiderMarkdownRenderer>(CoreAiMarkdownRendererRegistry.Current);
        }

        [Test]
        public void StyleSheet_ShipsInResources()
        {
            Assert.IsNotNull(new NeoxiderMarkdownRenderer().StyleSheet,
                "Resources/" + NeoxiderMarkdownRenderer.StyleSheetResourcePath + ".uss must exist; players need it.");
        }

        [Test]
        public void RendersHeadingListAndHighlightedCodeBlock()
        {
            CoreAiMarkdownView view = new("## Plan\n\n- first\n- second\n\n```csharp\nint x = 1;\n```\n\nDone **now**.",
                new NeoxiderMarkdownRenderer());

            Assert.IsTrue(view.IsRenderedAsMarkdown);
            Assert.AreEqual("Plan", view.Q<Label>(className: "h2")?.text);
            StringAssert.Contains("• first", view.Q<Label>(className: "list")?.text);
            VisualElement code = view.Q(className: "codeblock");
            Assert.IsNotNull(code, "A fenced block renders as a code block.");
            Assert.IsTrue(code.Query<Label>().ToList().Any(label => label.text.Contains("x")));
            Assert.IsTrue(view.Query<Label>(className: "paragraph").ToList().Any(label => label.text.Contains("<b>now</b>")));
            Assert.IsTrue(view.Query<Label>().ToList().All(label => label.selection.isSelectable),
                "A finished answer is selectable like the plain bubbles.");
        }

        [Test]
        public void StreamingUnterminatedFence_StillRendersAsCode()
        {
            CoreAiMarkdownView view = new(null, new NeoxiderMarkdownRenderer());

            view.UpdateStreaming("Look:\n\n```python\nprint(1)");

            VisualElement code = view.Q(className: "codeblock");
            Assert.IsNotNull(code);
            List<Label> labels = view.Query<Label>().ToList();
            Assert.IsTrue(labels.Any(label => label.text.Contains("print")));
            Assert.IsFalse(labels.Any(label => label.text.Contains("```")), "No fence leaks into the text.");
        }

        [Test]
        public void Images_AreShownAsLinks_SoNothingIsFetched()
        {
            CoreAiMarkdownView view = new("![cat](https://example.invalid/cat.png)", new NeoxiderMarkdownRenderer());

            Assert.IsNull(view.Q<Image>(), "An image element would make the renderer download its URL.");
            StringAssert.Contains("<link=https://example.invalid/cat.png>", view.Q<Label>(className: "paragraph")?.text);
        }

        [Test]
        public void RichTextTags_AreShown_NotApplied()
        {
            CoreAiMarkdownView view = new("<size=300>big</size>", new NeoxiderMarkdownRenderer());

            string text = view.Q<Label>(className: "paragraph")?.text ?? "";
            StringAssert.Contains("<​size=300>", text);
        }
    }
}
