using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.RuntimeUi;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>An engine-free element for cascade tests: a type, a name, classes, states and a parent.</summary>
    internal sealed class FakeUssElement : IUssElement
    {
        private readonly HashSet<string> _classes = new(StringComparer.Ordinal);
        private readonly List<FakeUssElement> _children = new();

        public FakeUssElement(string typeName, string name = null, params string[] classes)
        {
            TypeName = typeName;
            Name = name;
            for (int i = 0; i < classes.Length; i++)
            {
                _classes.Add(classes[i]);
            }
        }

        public string TypeName { get; }

        public string Name { get; }

        public FakeUssElement Parent { get; private set; }

        public IReadOnlyList<FakeUssElement> Children => _children;

        public IUssElement StyleParent => Parent;

        public UssPseudoState PseudoStates { get; set; }

        public bool HasClass(string className)
        {
            return className != null && _classes.Contains(className);
        }

        public FakeUssElement Add(FakeUssElement child)
        {
            child.Parent = this;
            _children.Add(child);
            return child;
        }

        public void AddClass(string className)
        {
            _classes.Add(className);
        }

        /// <summary>Builds a fake tree mirroring a parsed UXML node (the root stands for the screen root).</summary>
        public static FakeUssElement FromNode(UxmlNode node, bool isRoot)
        {
            string typeName = isRoot ? "VisualElement" : UxmlElementCatalog.TypeNameOf(node.Kind);
            FakeUssElement element = new(typeName, node.Name);
            for (int i = 0; i < node.Classes.Count; i++)
            {
                element.AddClass(node.Classes[i]);
            }

            if (isRoot)
            {
                element.PseudoStates = UssPseudoState.Root;
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                element.Add(FromNode(node.Children[i], false));
            }

            return element;
        }
    }

    /// <summary>A minimal cascade driver over a fake tree: resolves parents before children.</summary>
    internal sealed class FakeCascade
    {
        private readonly UssStyleResolver _resolver;
        private readonly UssResolvedStyle _scratch = new();
        private readonly Dictionary<FakeUssElement, UssCustomProperties> _scopes = new();

        public FakeCascade(RuntimeUiDiagnosticList diagnostics = null)
        {
            Diagnostics = diagnostics ?? new RuntimeUiDiagnosticList();
            _resolver = new UssStyleResolver(Diagnostics);
        }

        public RuntimeUiDiagnosticList Diagnostics { get; }

        public List<UssStyleSheet> Sheets { get; } = new();

        public int DefaultSheetCount { get; set; }

        public UssCustomProperties RootScope { get; set; } = UssCustomProperties.Empty;

        public UssResolvedStyle Resolve(FakeUssElement element, IReadOnlyList<UssDeclaration> overrides = null,
            IReadOnlyList<UssDeclaration> inline = null)
        {
            UssCustomProperties inherited = RootScope;
            if (element.Parent != null)
            {
                if (!_scopes.TryGetValue(element.Parent, out inherited))
                {
                    Resolve(element.Parent);
                    inherited = _scopes[element.Parent];
                }
            }

            _resolver.Resolve(Sheets, DefaultSheetCount, element, inherited, inline, overrides, _scratch);
            _scopes[element] = _scratch.CustomProperties;
            return _scratch;
        }

        /// <summary>Resolves the whole tree depth-first and returns how many elements were styled.</summary>
        public int ResolveTree(FakeUssElement root)
        {
            int count = 1;
            Resolve(root);
            for (int i = 0; i < root.Children.Count; i++)
            {
                count += ResolveTree(root.Children[i]);
            }

            return count;
        }

        public void ClearScopes()
        {
            _scopes.Clear();
        }
    }

    /// <summary>Shared fixture texts.</summary>
    internal static class RuntimeUiFixtureTexts
    {
        /// <summary>A hotbar-and-HUD screen of exactly 200 elements below the UXML root.</summary>
        public static string TwoHundredElementUxml()
        {
            StringBuilder builder = new();
            builder.Append("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n");
            builder.Append("  <ui:VisualElement name=\"hud\" class=\"hud\">\n");
            builder.Append("    <ui:Label name=\"crosshair\" class=\"crosshair\" text=\"+\"/>\n");
            builder.Append("    <ui:VisualElement name=\"hotbar\" class=\"hotbar\">\n");
            int count = 3;
            int slot = 0;
            while (count + 4 <= 200)
            {
                builder.Append("      <ui:VisualElement name=\"slot").Append(slot)
                    .Append("\" class=\"slot\">\n");
                builder.Append("        <ui:VisualElement class=\"icon\"/>\n");
                builder.Append("        <ui:Label class=\"count\" text=\"").Append(slot).Append("\"/>\n");
                builder.Append("        <ui:Button class=\"cai-btn use\" text=\"Use\"/>\n");
                builder.Append("      </ui:VisualElement>\n");
                count += 4;
                slot++;
            }

            builder.Append("    </ui:VisualElement>\n");
            while (count < 200)
            {
                builder.Append("    <ui:Label class=\"cai-text\" text=\"line\"/>\n");
                count++;
            }

            builder.Append("  </ui:VisualElement>\n");
            builder.Append("</ui:UXML>\n");
            return builder.ToString();
        }

        /// <summary>USS for the 200-element screen: layout, tokens, pseudo-states and combinators.</summary>
        public const string HotbarUss =
            ":root { --slot-size: 64px; --accent: rgb(255, 196, 64); }\n" +
            ".hud { position: absolute; left: 0; top: 0; right: 0; bottom: 0; align-items: center; }\n" +
            ".crosshair { position: absolute; left: 50%; top: 50%; translate: -50% -50%; font-size: 24px; color: white; }\n" +
            ".hotbar { position: absolute; bottom: 16px; flex-direction: row; padding: 4px; " +
            "background-color: rgba(0, 0, 0, 0.5); border-radius: 8px; }\n" +
            ".slot { width: var(--slot-size); height: var(--slot-size); margin: 0 2px; border-width: 2px; " +
            "border-color: rgba(255, 255, 255, 0.2); transition: border-color 0.1s ease-out; }\n" +
            ".slot:hover { border-color: var(--accent); }\n" +
            ".slot.is-selected { border-color: var(--accent); scale: 1.1; }\n" +
            ".slot > .icon { flex-grow: 1; background-color: rgb(80, 80, 80); }\n" +
            ".slot .count { position: absolute; right: 2px; bottom: 0; font-size: 12px; -unity-font-style: bold; }\n" +
            ".use { display: none; }\n" +
            "Label { color: rgb(240, 240, 240); }\n";
    }
}
