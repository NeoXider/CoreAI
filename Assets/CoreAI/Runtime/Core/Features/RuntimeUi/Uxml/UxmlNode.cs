using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>One attribute as written in UXML.</summary>
    public readonly struct UxmlAttribute
    {
        /// <summary>Creates an attribute.</summary>
        public UxmlAttribute(string name, string value, int line, int column)
        {
            Name = name ?? string.Empty;
            Value = value ?? string.Empty;
            Line = line;
            Column = column;
        }

        /// <summary>Attribute name.</summary>
        public string Name { get; }

        /// <summary>Decoded value.</summary>
        public string Value { get; }

        /// <summary>1-based line.</summary>
        public int Line { get; }

        /// <summary>1-based column.</summary>
        public int Column { get; }
    }

    /// <summary>
    /// One element of a parsed UXML document: its kind, name, classes, the attributes the factory
    /// applies, the <c>style</c> attribute as declarations, text content and children. Template
    /// instances are already expanded into <see cref="RuntimeUiElementKind.TemplateContainer"/> nodes.
    /// </summary>
    public sealed class UxmlNode
    {
        private static readonly IReadOnlyList<UssDeclaration> NoDeclarations = Array.Empty<UssDeclaration>();

        private readonly List<UxmlNode> _children = new();
        private readonly List<UxmlAttribute> _attributes = new();
        private readonly List<string> _classes = new();

        internal UxmlNode(RuntimeUiElementKind kind, string elementType, string qualifiedName, int line, int column)
        {
            Kind = kind;
            ElementType = elementType ?? string.Empty;
            QualifiedName = qualifiedName ?? ElementType;
            Line = line;
            Column = column;
        }

        /// <summary>What the factory builds.</summary>
        public RuntimeUiElementKind Kind { get; internal set; }

        /// <summary>Local type name as resolved (<c>Button</c>), or the unknown name for a placeholder.</summary>
        public string ElementType { get; }

        /// <summary>The element name as written (<c>ui:Button</c>).</summary>
        public string QualifiedName { get; }

        /// <summary>Element name from the <c>name</c> attribute, or null.</summary>
        public string Name { get; internal set; }

        /// <summary>Classes from the <c>class</c> attribute.</summary>
        public IReadOnlyList<string> Classes => _classes;

        /// <summary>Supported attributes other than name, class and style, validated, in source order.</summary>
        public IReadOnlyList<UxmlAttribute> Attributes => _attributes;

        /// <summary>The <c>style</c> attribute as declarations.</summary>
        public IReadOnlyList<UssDeclaration> InlineStyle { get; internal set; } = NoDeclarations;

        /// <summary>Trimmed text content, or null.</summary>
        public string Text { get; internal set; }

        /// <summary>Child elements.</summary>
        public IReadOnlyList<UxmlNode> Children => _children;

        /// <summary>Parent element; null for the document root.</summary>
        public UxmlNode Parent { get; private set; }

        /// <summary>1-based line.</summary>
        public int Line { get; }

        /// <summary>1-based column.</summary>
        public int Column { get; }

        /// <summary>Template alias of a template container, or null.</summary>
        public string TemplateName { get; internal set; }

        /// <summary>The parsed item template of a <c>ListView</c> with <c>item-template</c>, or null.</summary>
        public UxmlNode ItemTemplate { get; internal set; }

        /// <summary>What a placeholder stands in for (shown in its label), or null.</summary>
        public string PlaceholderReason { get; internal set; }

        /// <summary>True for a placeholder.</summary>
        public bool IsPlaceholder => Kind == RuntimeUiElementKind.Placeholder;

        /// <summary>Reads an attribute by name (ordinal).</summary>
        public bool TryGetAttribute(string name, out string value)
        {
            for (int i = _attributes.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_attributes[i].Name, name, StringComparison.Ordinal))
                {
                    value = _attributes[i].Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>Reads an attribute, or returns null.</summary>
        public string GetAttribute(string name)
        {
            return TryGetAttribute(name, out string value) ? value : null;
        }

        /// <summary>Counts this node and all descendants.</summary>
        public int CountDescendantsAndSelf()
        {
            int count = 1;
            for (int i = 0; i < _children.Count; i++)
            {
                count += _children[i].CountDescendantsAndSelf();
            }

            return count;
        }

        internal void AddChild(UxmlNode child)
        {
            child.Parent = this;
            _children.Add(child);
        }

        internal void AddClass(string className)
        {
            if (!string.IsNullOrEmpty(className) && !_classes.Contains(className))
            {
                _classes.Add(className);
            }
        }

        internal void ClearClasses()
        {
            _classes.Clear();
        }

        internal void SetAttribute(UxmlAttribute attribute)
        {
            for (int i = 0; i < _attributes.Count; i++)
            {
                if (string.Equals(_attributes[i].Name, attribute.Name, StringComparison.Ordinal))
                {
                    _attributes[i] = attribute;
                    return;
                }
            }

            _attributes.Add(attribute);
        }

        internal UxmlNode DeepClone()
        {
            UxmlNode clone = new(Kind, ElementType, QualifiedName, Line, Column)
            {
                Name = Name,
                InlineStyle = InlineStyle,
                Text = Text,
                TemplateName = TemplateName,
                ItemTemplate = ItemTemplate,
                PlaceholderReason = PlaceholderReason
            };
            clone._classes.AddRange(_classes);
            clone._attributes.AddRange(_attributes);
            for (int i = 0; i < _children.Count; i++)
            {
                clone.AddChild(_children[i].DeepClone());
            }

            return clone;
        }
    }
}
