using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// A forgiving UXML reader. It tolerates what hand- and AI-written markup gets wrong (missing
    /// namespace declarations, unquoted attributes, unclosed or mismatched tags, text content) and never
    /// throws: everything it could not build as written is reported with its line, an unknown element
    /// becomes a labelled placeholder, and the rest of the tree is still built.
    /// </summary>
    public static class UxmlParser
    {
        private const string StructuralUxml = "UXML";
        private const string StructuralTemplate = "Template";
        private const string StructuralInstance = "Instance";
        private const string StructuralOverrides = "AttributeOverrides";
        private const string StructuralStyle = "Style";

        /// <summary>Parses UXML text.</summary>
        public static UxmlDocument Parse(string text, UxmlParseOptions options = null)
        {
            ParseContext context = new(options);
            return ParseDocument(text, context, null);
        }

        private static UxmlDocument ParseDocument(string text, ParseContext context, string templateName)
        {
            string source = text ?? string.Empty;
            UxmlNode root = new(RuntimeUiElementKind.VisualElement, StructuralUxml, StructuralUxml, 1, 1);
            Dictionary<string, string> templates = new(StringComparer.Ordinal);
            int countBefore = context.ElementCount;
            if (source.Length > context.Limits.MaxTextLength)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlLimitExceeded, RuntimeUiDiagnosticSeverity.Error, 1, 1,
                    "the markup is " + source.Length + " characters; the limit is " + context.Limits.MaxTextLength,
                    templateName);
                return new UxmlDocument(root, context.Diagnostics.ToArray(), templates, 0);
            }

            RawElement rawRoot = Tokenize(source, context, templateName);
            DocumentState state = new(templates, templateName);
            ConvertChildren(rawRoot, root, NamespaceScope.Empty, 1, context, state, true);
            return new UxmlDocument(root, context.Diagnostics.ToArray(), templates,
                context.ElementCount - countBefore);
        }

        private static RawElement Tokenize(string text, ParseContext context, string templateName)
        {
            RuntimeUiLineMap lines = new(text);
            RawElement root = new(null, 1, 1);
            List<RawElement> stack = new() { root };
            int pos = 0;
            while (pos < text.Length)
            {
                int lt = text.IndexOf('<', pos);
                if (lt < 0)
                {
                    AddText(stack[stack.Count - 1], text, pos, text.Length, lines, context, templateName, true);
                    break;
                }

                if (lt > pos)
                {
                    AddText(stack[stack.Count - 1], text, pos, lt, lines, context, templateName, true);
                }

                if (StartsWith(text, lt, "<!--"))
                {
                    int end = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Report(context, lines, lt, RuntimeUiDiagnosticCodes.UxmlMalformed,
                            RuntimeUiDiagnosticSeverity.Error, "a comment is never closed", templateName);
                        pos = text.Length;
                    }
                    else
                    {
                        pos = end + 3;
                    }

                    continue;
                }

                if (StartsWith(text, lt, "<![CDATA["))
                {
                    int end = text.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end;
                    if (end < 0)
                    {
                        Report(context, lines, lt, RuntimeUiDiagnosticCodes.UxmlMalformed,
                            RuntimeUiDiagnosticSeverity.Error, "a CDATA section is never closed", templateName);
                    }

                    AddText(stack[stack.Count - 1], text, lt + 9, stop, lines, context, templateName, false);
                    pos = end < 0 ? text.Length : end + 3;
                    continue;
                }

                if (StartsWith(text, lt, "<?") || StartsWith(text, lt, "<!"))
                {
                    string terminator = text[lt + 1] == '?' ? "?>" : ">";
                    int end = text.IndexOf(terminator, lt + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        Report(context, lines, lt, RuntimeUiDiagnosticCodes.UxmlMalformed,
                            RuntimeUiDiagnosticSeverity.Error, "a declaration is never closed", templateName);
                        pos = text.Length;
                    }
                    else
                    {
                        pos = end + terminator.Length;
                    }

                    continue;
                }

                if (StartsWith(text, lt, "</"))
                {
                    pos = ReadCloseTag(text, lt, lines, stack, context, templateName);
                    continue;
                }

                if (lt + 1 < text.Length && IsNameStart(text[lt + 1]))
                {
                    pos = ReadStartTag(text, lt, lines, stack, context, templateName);
                    continue;
                }

                Report(context, lines, lt, RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Error,
                    "'<' does not start a tag; write &lt; for a literal '<'", templateName);
                AddText(stack[stack.Count - 1], text, lt, lt + 1, lines, context, templateName, false);
                pos = lt + 1;
            }

            for (int i = stack.Count - 1; i >= 1; i--)
            {
                RawElement open = stack[i];
                context.Report(RuntimeUiDiagnosticCodes.UxmlUnclosedElement, RuntimeUiDiagnosticSeverity.Error,
                    open.Line, open.Column, "<" + open.Name + "> is never closed", templateName);
            }

            return root;
        }

        private static int ReadStartTag(string text, int lt, RuntimeUiLineMap lines, List<RawElement> stack,
            ParseContext context, string templateName)
        {
            int pos = lt + 1;
            string name = ReadName(text, ref pos);
            lines.Locate(lt, out int line, out int column);
            RawElement element = new(name, line, column);
            bool selfClosing = false;
            bool closed = false;
            while (pos < text.Length)
            {
                char c = text[pos];
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }

                if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '>')
                {
                    selfClosing = true;
                    closed = true;
                    pos += 2;
                    break;
                }

                if (c == '>')
                {
                    closed = true;
                    pos++;
                    break;
                }

                if (c == '<')
                {
                    break;
                }

                if (!IsNameStart(c))
                {
                    Report(context, lines, pos, RuntimeUiDiagnosticCodes.UxmlMalformed,
                        RuntimeUiDiagnosticSeverity.Error, "unexpected '" + c + "' in <" + name + ">", templateName);
                    pos++;
                    continue;
                }

                int attributeStart = pos;
                string attributeName = ReadName(text, ref pos);
                lines.Locate(attributeStart, out int attributeLine, out int attributeColumn);
                while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                {
                    pos++;
                }

                string value = string.Empty;
                if (pos < text.Length && text[pos] == '=')
                {
                    pos++;
                    while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                    {
                        pos++;
                    }

                    if (pos < text.Length && (text[pos] == '"' || text[pos] == '\''))
                    {
                        char quote = text[pos];
                        int end = text.IndexOf(quote, pos + 1);
                        if (end < 0)
                        {
                            Report(context, lines, pos, RuntimeUiDiagnosticCodes.UxmlMalformed,
                                RuntimeUiDiagnosticSeverity.Error,
                                "the value of '" + attributeName + "' is never closed", templateName);
                            int tagEnd = text.IndexOf('>', pos + 1);
                            end = tagEnd < 0 ? text.Length : tagEnd;
                            value = DecodeEntities(text.Substring(pos + 1, end - pos - 1), context, attributeLine,
                                attributeColumn, templateName);
                            pos = end;
                        }
                        else
                        {
                            value = DecodeEntities(text.Substring(pos + 1, end - pos - 1), context, attributeLine,
                                attributeColumn, templateName);
                            pos = end + 1;
                        }
                    }
                    else
                    {
                        int start = pos;
                        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '>'
                               && !(text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '>'))
                        {
                            pos++;
                        }

                        value = DecodeEntities(text.Substring(start, pos - start), context, attributeLine,
                            attributeColumn, templateName);
                        context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                            attributeLine, attributeColumn,
                            "the value of '" + attributeName + "' is not quoted; it was read as '" + value + "'",
                            templateName);
                    }
                }
                else
                {
                    context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                        attributeLine, attributeColumn, "'" + attributeName + "' has no value", templateName);
                }

                if (element.HasAttribute(attributeName))
                {
                    context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                        attributeLine, attributeColumn,
                        "'" + attributeName + "' is set twice on <" + name + ">; the first value is kept",
                        templateName);
                    continue;
                }

                element.Attributes.Add(new UxmlAttribute(attributeName, value, attributeLine, attributeColumn));
            }

            if (!closed)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Error, line, column,
                    "the tag <" + name + " is never closed with '>'", templateName);
            }

            RawElement parent = stack[stack.Count - 1];
            parent.Children.Add(element);
            if (!selfClosing && closed)
            {
                if (stack.Count > context.Limits.MaxDepth + 1)
                {
                    context.ReportDepthOnce(line, column, templateName);
                }

                stack.Add(element);
            }

            return pos;
        }

        private static int ReadCloseTag(string text, int lt, RuntimeUiLineMap lines, List<RawElement> stack,
            ParseContext context, string templateName)
        {
            int pos = lt + 2;
            string name = ReadName(text, ref pos);
            int end = text.IndexOf('>', pos);
            lines.Locate(lt, out int line, out int column);
            int next = end < 0 ? text.Length : end + 1;
            if (end < 0)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Error, line, column,
                    "the closing tag </" + name + " is never closed with '>'", templateName);
            }

            if (name.Length == 0)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Error, line, column,
                    "a closing tag has no name", templateName);
                return next;
            }

            for (int i = stack.Count - 1; i >= 1; i--)
            {
                if (!string.Equals(stack[i].Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                for (int j = stack.Count - 1; j > i; j--)
                {
                    RawElement unclosed = stack[j];
                    context.Report(RuntimeUiDiagnosticCodes.UxmlUnclosedElement, RuntimeUiDiagnosticSeverity.Error,
                        unclosed.Line, unclosed.Column,
                        "<" + unclosed.Name + "> is not closed before </" + name + "> on line " + line, templateName);
                }

                stack.RemoveRange(i, stack.Count - i);
                return next;
            }

            context.Report(RuntimeUiDiagnosticCodes.UxmlMismatchedCloseTag, RuntimeUiDiagnosticSeverity.Error, line,
                column, "</" + name + "> closes no open element and was ignored", templateName);
            return next;
        }

        private static void AddText(RawElement parent, string text, int start, int end, RuntimeUiLineMap lines,
            ParseContext context, string templateName, bool decode)
        {
            if (end <= start)
            {
                return;
            }

            string raw = text.Substring(start, end - start);
            if (raw.Trim().Length == 0)
            {
                return;
            }

            lines.Locate(start + (raw.Length - raw.TrimStart().Length), out int line, out int column);
            string value = decode ? DecodeEntities(raw, context, line, column, templateName) : raw;
            parent.Children.Add(new RawText(value, line, column));
        }

        private static void ConvertChildren(
            RawElement raw,
            UxmlNode parent,
            NamespaceScope scope,
            int depth,
            ParseContext context,
            DocumentState state,
            bool topLevel)
        {
            StringBuilder text = null;
            int textLine = 0;
            int textColumn = 0;
            for (int i = 0; i < raw.Children.Count; i++)
            {
                object child = raw.Children[i];
                if (child is RawText rawText)
                {
                    if (text == null)
                    {
                        text = new StringBuilder();
                        textLine = rawText.Line;
                        textColumn = rawText.Column;
                    }
                    else
                    {
                        text.Append(' ');
                    }

                    text.Append(rawText.Text.Trim());
                    continue;
                }

                RawElement element = (RawElement)child;
                if (context.LimitReached)
                {
                    return;
                }

                ConvertElement(element, parent, scope.Extend(element.Attributes), depth, context, state, topLevel);
            }

            if (text != null && text.Length > 0 && !topLevel)
            {
                ApplyTextContent(parent, text.ToString(), textLine, textColumn, context, state);
            }
            else if (text != null && text.Length > 0)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlTextContent, RuntimeUiDiagnosticSeverity.Warning, textLine,
                    textColumn, "text outside any element was ignored", state.TemplateName);
            }
        }

        private static void ConvertElement(
            RawElement raw,
            UxmlNode parent,
            NamespaceScope scope,
            int depth,
            ParseContext context,
            DocumentState state,
            bool topLevel)
        {
            ResolveName(raw.Name, scope, out string namespaceUri, out string localName);
            bool isEngine = namespaceUri == UxmlElementCatalog.EngineNamespace;
            if (isEngine && localName == StructuralUxml)
            {
                if (!topLevel)
                {
                    context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                        raw.Line, raw.Column, "a nested <UXML> element was flattened into its parent",
                        state.TemplateName);
                }

                ConvertChildren(raw, parent, scope, depth, context, state, topLevel);
                return;
            }

            if (isEngine && localName == StructuralTemplate)
            {
                DeclareTemplate(raw, context, state);
                return;
            }

            if (isEngine && localName == StructuralStyle)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlStyleReferenceIgnored, RuntimeUiDiagnosticSeverity.Info,
                    raw.Line, raw.Column,
                    "<Style> references are not loaded at runtime; pass the USS text to the screen instead",
                    state.TemplateName);
                return;
            }

            if (isEngine && localName == StructuralOverrides)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning, raw.Line,
                    raw.Column, "<AttributeOverrides> is only valid inside <Instance>", state.TemplateName);
                return;
            }

            if (depth > context.Limits.MaxDepth)
            {
                context.ReportDepthOnce(raw.Line, raw.Column, state.TemplateName);
                return;
            }

            if (!context.TryCountElement(raw.Line, raw.Column, state.TemplateName))
            {
                return;
            }

            if (isEngine && localName == StructuralInstance)
            {
                ConvertInstance(raw, parent, scope, depth, context, state);
                return;
            }

            UxmlNode node;
            if (isEngine && UxmlElementCatalog.TryGetKind(localName, out RuntimeUiElementKind kind))
            {
                node = new UxmlNode(kind, localName, raw.Name, raw.Line, raw.Column);
            }
            else
            {
                node = new UxmlNode(RuntimeUiElementKind.Placeholder, localName, raw.Name, raw.Line, raw.Column);
                ReportUnknownElement(raw, namespaceUri, localName, node, context, state);
            }

            ApplyAttributes(node, raw.Attributes, context, state);
            parent.AddChild(node);
            ConvertChildren(raw, node, scope, depth + 1, context, state, false);
            if (node.Kind == RuntimeUiElementKind.ListView)
            {
                ResolveItemTemplate(node, context, state);
            }
        }

        private static void ReportUnknownElement(RawElement raw, string namespaceUri, string localName, UxmlNode node,
            ParseContext context, DocumentState state)
        {
            string reason;
            string code = RuntimeUiDiagnosticCodes.UxmlUnknownElement;
            if (namespaceUri == UxmlElementCatalog.EditorNamespace)
            {
                code = RuntimeUiDiagnosticCodes.UxmlEditorOnlyElement;
                reason = "<" + raw.Name + "> is an editor-only element and does not exist in a player";
            }
            else if (namespaceUri == UxmlElementCatalog.EngineNamespace && UxmlElementCatalog.IsKnownUnbuilt(localName))
            {
                reason = "<" + raw.Name + "> is a UI Toolkit element this runtime interpreter does not build";
            }
            else if (namespaceUri == UxmlElementCatalog.EngineNamespace)
            {
                reason = "<" + raw.Name + "> is not a UI Toolkit element";
            }
            else
            {
                reason = "<" + raw.Name + "> is a custom element type from '" + namespaceUri +
                         "'; custom types are not available to runtime screens";
            }

            node.PlaceholderReason = reason;
            context.Report(code, RuntimeUiDiagnosticSeverity.Warning, raw.Line, raw.Column,
                reason + "; a placeholder stands in (supported: " +
                string.Join(", ", UxmlElementCatalog.SupportedTypeNames) + ")", state.TemplateName);
        }

        private static void ApplyAttributes(UxmlNode node, List<UxmlAttribute> attributes, ParseContext context,
            DocumentState state)
        {
            for (int i = 0; i < attributes.Count; i++)
            {
                ApplyAttribute(node, attributes[i], context, state.TemplateName, false);
            }
        }

        private static void ApplyAttribute(UxmlNode node, UxmlAttribute attribute, ParseContext context,
            string templateName, bool isOverride)
        {
            string name = attribute.Name;
            if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal)
                                || name.StartsWith("xsi:", StringComparison.Ordinal)
                                || name == "editor-extension-mode")
            {
                return;
            }

            if (name == "name")
            {
                if (isOverride)
                {
                    ReportUnsupportedOverride(attribute, context, templateName);
                    return;
                }

                node.Name = attribute.Value.Trim().Length == 0 ? null : attribute.Value.Trim();
                return;
            }

            if (name == "class")
            {
                if (isOverride)
                {
                    ReportUnsupportedOverride(attribute, context, templateName);
                    return;
                }

                string[] classes = attribute.Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (int c = 0; c < classes.Length; c++)
                {
                    node.AddClass(classes[c]);
                }

                return;
            }

            if (name == "style")
            {
                if (isOverride)
                {
                    ReportUnsupportedOverride(attribute, context, templateName);
                    return;
                }

                RuntimeUiDiagnosticList styleReports = new(context.Limits.MaxDiagnostics);
                node.InlineStyle = UssParser.ParseInlineDeclarations(attribute.Value, attribute.Line,
                    RuntimeUiSource.InlineStyle, styleReports);
                for (int r = 0; r < styleReports.Count; r++)
                {
                    RuntimeUiDiagnostic report = styleReports.Items[r];
                    context.Report(report.Code, report.Severity, report.Line, attribute.Column, report.Message,
                        templateName, RuntimeUiSource.InlineStyle);
                }

                return;
            }

            if (node.IsPlaceholder)
            {
                return;
            }

            if (!UxmlElementCatalog.TryGetAttribute(node.Kind, name, out UxmlAttributeSpec spec))
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlUnsupportedAttribute, RuntimeUiDiagnosticSeverity.Warning,
                    attribute.Line, attribute.Column,
                    "'" + name + "' is not applied to <" + node.ElementType + "> by the runtime interpreter",
                    templateName);
                return;
            }

            if (!spec.TryNormalize(attribute.Value, out string normalized, out string error))
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlInvalidAttributeValue, RuntimeUiDiagnosticSeverity.Warning,
                    attribute.Line, attribute.Column, "'" + name + "' on <" + node.ElementType + ">: " + error,
                    templateName);
                return;
            }

            node.SetAttribute(new UxmlAttribute(name, normalized, attribute.Line, attribute.Column));
        }

        private static void ReportUnsupportedOverride(UxmlAttribute attribute, ParseContext context,
            string templateName)
        {
            context.Report(RuntimeUiDiagnosticCodes.UxmlUnsupportedAttribute, RuntimeUiDiagnosticSeverity.Warning,
                attribute.Line, attribute.Column,
                "'" + attribute.Name + "' cannot be overridden by <AttributeOverrides>", templateName);
        }

        private static void ApplyTextContent(UxmlNode node, string text, int line, int column, ParseContext context,
            DocumentState state)
        {
            bool takesText = node.Kind == RuntimeUiElementKind.Label || node.Kind == RuntimeUiElementKind.Button;
            if (takesText && node.GetAttribute("text") == null)
            {
                node.SetAttribute(new UxmlAttribute("text", text, line, column));
                node.Text = text;
                context.Report(RuntimeUiDiagnosticCodes.UxmlTextContent, RuntimeUiDiagnosticSeverity.Info, line, column,
                    "the text content of <" + node.ElementType + "> was used as its text; the editor importer " +
                    "only reads the text attribute", state.TemplateName);
                return;
            }

            node.Text = text;
            context.Report(RuntimeUiDiagnosticCodes.UxmlTextContent, RuntimeUiDiagnosticSeverity.Warning, line, column,
                "text content of <" + node.ElementType + "> was ignored; use the text attribute", state.TemplateName);
        }

        private static void DeclareTemplate(RawElement raw, ParseContext context, DocumentState state)
        {
            string alias = raw.GetAttribute("name");
            string src = raw.GetAttribute("src") ?? raw.GetAttribute("path");
            if (string.IsNullOrWhiteSpace(alias))
            {
                alias = src;
            }

            if (string.IsNullOrWhiteSpace(alias))
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning, raw.Line,
                    raw.Column, "<Template> needs a name or a src", state.TemplateName);
                return;
            }

            state.Templates[alias.Trim()] = string.IsNullOrWhiteSpace(src) ? alias.Trim() : src.Trim();
        }

        private static void ConvertInstance(RawElement raw, UxmlNode parent, NamespaceScope scope, int depth,
            ParseContext context, DocumentState state)
        {
            UxmlNode container = new(RuntimeUiElementKind.TemplateContainer, "TemplateContainer", raw.Name, raw.Line,
                raw.Column);
            List<UxmlAttribute> attributes = new();
            for (int i = 0; i < raw.Attributes.Count; i++)
            {
                if (raw.Attributes[i].Name != "template")
                {
                    attributes.Add(raw.Attributes[i]);
                }
            }

            string alias = raw.GetAttribute("template");
            container.TemplateName = alias;
            ApplyAttributes(container, attributes, context, state);
            parent.AddChild(container);
            if (string.IsNullOrWhiteSpace(alias))
            {
                MakeMissingTemplatePlaceholder(container, "<Instance> has no template attribute", raw, context, state);
                return;
            }

            UxmlNode templateRoot = LoadTemplate(alias.Trim(), raw.Line, raw.Column, context, state);
            if (templateRoot == null)
            {
                return;
            }

            for (int i = 0; i < templateRoot.Children.Count; i++)
            {
                if (!context.TryCountElements(templateRoot.Children[i].CountDescendantsAndSelf(), raw.Line, raw.Column,
                        state.TemplateName))
                {
                    break;
                }

                container.AddChild(templateRoot.Children[i].DeepClone());
            }

            for (int i = 0; i < raw.Children.Count; i++)
            {
                if (raw.Children[i] is not RawElement child)
                {
                    continue;
                }

                ResolveName(child.Name, scope.Extend(child.Attributes), out string ns, out string local);
                if (ns == UxmlElementCatalog.EngineNamespace && local == StructuralOverrides)
                {
                    ApplyOverrides(container, child, context, state);
                }
                else
                {
                    context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                        child.Line, child.Column,
                        "<" + child.Name + "> inside <Instance> was ignored; only <AttributeOverrides> is allowed",
                        state.TemplateName);
                }
            }
        }

        private static void ApplyOverrides(UxmlNode container, RawElement overrides, ParseContext context,
            DocumentState state)
        {
            string target = overrides.GetAttribute("element-name");
            if (string.IsNullOrWhiteSpace(target))
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                    overrides.Line, overrides.Column, "<AttributeOverrides> needs an element-name", state.TemplateName);
                return;
            }

            List<UxmlNode> matches = new();
            CollectByName(container, target.Trim(), matches);
            if (matches.Count == 0)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning,
                    overrides.Line, overrides.Column,
                    "<AttributeOverrides> names '" + target + "', which the template does not contain",
                    state.TemplateName);
                return;
            }

            for (int i = 0; i < overrides.Attributes.Count; i++)
            {
                UxmlAttribute attribute = overrides.Attributes[i];
                if (attribute.Name == "element-name")
                {
                    continue;
                }

                for (int m = 0; m < matches.Count; m++)
                {
                    ApplyAttribute(matches[m], attribute, context, state.TemplateName, true);
                }
            }
        }

        private static void CollectByName(UxmlNode node, string name, List<UxmlNode> output)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                UxmlNode child = node.Children[i];
                if (child.Name == name)
                {
                    output.Add(child);
                }

                CollectByName(child, name, output);
            }
        }

        private static void ResolveItemTemplate(UxmlNode listView, ParseContext context, DocumentState state)
        {
            string alias = listView.GetAttribute("item-template");
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            UxmlNode templateRoot = LoadTemplate(alias.Trim(), listView.Line, listView.Column, context, state);
            if (templateRoot == null)
            {
                return;
            }

            UxmlNode item = new(RuntimeUiElementKind.TemplateContainer, "TemplateContainer", "TemplateContainer",
                listView.Line, listView.Column) { TemplateName = alias.Trim() };
            for (int i = 0; i < templateRoot.Children.Count; i++)
            {
                item.AddChild(templateRoot.Children[i].DeepClone());
            }

            listView.ItemTemplate = item;
        }

        private static UxmlNode LoadTemplate(string alias, int line, int column, ParseContext context,
            DocumentState state)
        {
            string reference = state.Templates.TryGetValue(alias, out string src) ? src : alias;
            if (context.TemplateCache.TryGetValue(reference, out UxmlNode cached))
            {
                return cached;
            }

            if (context.TemplateStack.Contains(reference) || context.TemplateStack.Count >= context.Limits.MaxTemplateDepth)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlTemplateRecursion, RuntimeUiDiagnosticSeverity.Error, line,
                    column,
                    "the template '" + alias + "' instantiates itself or nests deeper than " +
                    context.Limits.MaxTemplateDepth + " levels", state.TemplateName);
                return null;
            }

            IUxmlTemplateResolver resolver = context.Resolver;
            string text = null;
            bool found = resolver != null
                         && (resolver.TryResolve(reference, out text)
                             || (!string.Equals(reference, alias, StringComparison.Ordinal)
                                 && resolver.TryResolve(alias, out text)));
            if (!found)
            {
                context.Report(RuntimeUiDiagnosticCodes.UxmlTemplateNotFound, RuntimeUiDiagnosticSeverity.Warning, line,
                    column,
                    "the template '" + alias + "' (" + reference + ") is not registered with the runtime UI host",
                    state.TemplateName);
                return null;
            }

            context.TemplateStack.Add(reference);
            int countBefore = context.ElementCount;
            UxmlDocument document = ParseDocument(text, context, alias);
            context.TemplateStack.RemoveAt(context.TemplateStack.Count - 1);

            // WHY: the template's own parse counted its elements; each instance counts its clones again,
            // so the definition must not use up the budget a second time.
            context.RewindElementCount(countBefore);
            context.TemplateCache[reference] = document.Root;
            return document.Root;
        }

        private static void MakeMissingTemplatePlaceholder(UxmlNode container, string reason, RawElement raw,
            ParseContext context, DocumentState state)
        {
            container.Kind = RuntimeUiElementKind.Placeholder;
            container.PlaceholderReason = reason;
            context.Report(RuntimeUiDiagnosticCodes.UxmlTemplateNotFound, RuntimeUiDiagnosticSeverity.Warning,
                raw.Line, raw.Column, reason, state.TemplateName);
        }

        private static void ResolveName(string qualifiedName, NamespaceScope scope, out string namespaceUri,
            out string localName)
        {
            string name = qualifiedName ?? string.Empty;
            int colon = name.IndexOf(':');
            string prefix = colon > 0 ? name.Substring(0, colon) : string.Empty;
            string local = colon > 0 ? name.Substring(colon + 1) : name;
            int dot = local.LastIndexOf('.');
            if (dot > 0)
            {
                namespaceUri = local.Substring(0, dot);
                localName = local.Substring(dot + 1);
                return;
            }

            localName = local;
            string declared = scope.Lookup(prefix);
            if (declared != null)
            {
                namespaceUri = declared;
                return;
            }

            // WHY: hand- and AI-written UXML routinely uses ui:/engine: without declaring them, or no
            // prefix at all; refusing those would turn every such screen into placeholders.
            switch (prefix)
            {
                case "":
                case "ui":
                case "engine":
                    namespaceUri = UxmlElementCatalog.EngineNamespace;
                    break;
                case "uie":
                case "editor":
                    namespaceUri = UxmlElementCatalog.EditorNamespace;
                    break;
                default:
                    namespaceUri = prefix;
                    break;
            }
        }

        private static string DecodeEntities(string text, ParseContext context, int line, int column,
            string templateName)
        {
            int amp = text.IndexOf('&');
            if (amp < 0)
            {
                return text;
            }

            StringBuilder builder = new(text.Length);
            int pos = 0;
            while (amp >= 0)
            {
                builder.Append(text, pos, amp - pos);
                int semicolon = text.IndexOf(';', amp + 1);
                string entity = semicolon > amp && semicolon - amp <= 10
                    ? text.Substring(amp + 1, semicolon - amp - 1)
                    : null;
                string decoded = entity == null ? null : DecodeEntity(entity);
                if (decoded == null)
                {
                    context.Report(RuntimeUiDiagnosticCodes.UxmlMalformed, RuntimeUiDiagnosticSeverity.Warning, line,
                        column, "'&' does not start a known entity; write &amp; for a literal '&'", templateName);
                    builder.Append('&');
                    pos = amp + 1;
                }
                else
                {
                    builder.Append(decoded);
                    pos = semicolon + 1;
                }

                amp = text.IndexOf('&', pos);
            }

            builder.Append(text, pos, text.Length - pos);
            return builder.ToString();
        }

        private static string DecodeEntity(string entity)
        {
            switch (entity)
            {
                case "lt": return "<";
                case "gt": return ">";
                case "amp": return "&";
                case "quot": return "\"";
                case "apos": return "'";
            }

            if (entity.Length > 1 && entity[0] == '#')
            {
                bool hex = entity[1] == 'x' || entity[1] == 'X';
                string digits = hex ? entity.Substring(2) : entity.Substring(1);
                if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int code) && code > 0 && code <= 0x10FFFF
                    && (code < 0xD800 || code > 0xDFFF))
                {
                    return char.ConvertFromUtf32(code);
                }
            }

            return null;
        }

        private static void Report(ParseContext context, RuntimeUiLineMap lines, int offset, string code,
            RuntimeUiDiagnosticSeverity severity, string message, string templateName)
        {
            lines.Locate(offset, out int line, out int column);
            context.Report(code, severity, line, column, message, templateName);
        }

        private static bool StartsWith(string text, int index, string value)
        {
            return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        private static bool IsNameStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static string ReadName(string text, ref int pos)
        {
            int start = pos;
            while (pos < text.Length)
            {
                char c = text[pos];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':')
                {
                    pos++;
                    continue;
                }

                break;
            }

            return text.Substring(start, pos - start);
        }

        private sealed class RawElement
        {
            public RawElement(string name, int line, int column)
            {
                Name = name ?? string.Empty;
                Line = line;
                Column = column;
            }

            public string Name { get; }

            public int Line { get; }

            public int Column { get; }

            public List<UxmlAttribute> Attributes { get; } = new();

            public List<object> Children { get; } = new();

            public bool HasAttribute(string name)
            {
                return GetAttribute(name) != null;
            }

            public string GetAttribute(string name)
            {
                for (int i = 0; i < Attributes.Count; i++)
                {
                    if (string.Equals(Attributes[i].Name, name, StringComparison.Ordinal))
                    {
                        return Attributes[i].Value;
                    }
                }

                return null;
            }
        }

        private sealed class RawText
        {
            public RawText(string text, int line, int column)
            {
                Text = text;
                Line = line;
                Column = column;
            }

            public string Text { get; }

            public int Line { get; }

            public int Column { get; }
        }

        private sealed class NamespaceScope
        {
            public static readonly NamespaceScope Empty = new(null, null, null);

            private readonly NamespaceScope _parent;
            private readonly string _prefix;
            private readonly string _uri;

            private NamespaceScope(NamespaceScope parent, string prefix, string uri)
            {
                _parent = parent;
                _prefix = prefix;
                _uri = uri;
            }

            public NamespaceScope Extend(List<UxmlAttribute> attributes)
            {
                NamespaceScope scope = this;
                for (int i = 0; i < attributes.Count; i++)
                {
                    string name = attributes[i].Name;
                    if (name == "xmlns")
                    {
                        scope = new NamespaceScope(scope, string.Empty, attributes[i].Value.Trim());
                    }
                    else if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                    {
                        scope = new NamespaceScope(scope, name.Substring(6), attributes[i].Value.Trim());
                    }
                }

                return scope;
            }

            public string Lookup(string prefix)
            {
                for (NamespaceScope scope = this; scope != null; scope = scope._parent)
                {
                    if (scope._prefix != null && string.Equals(scope._prefix, prefix, StringComparison.Ordinal))
                    {
                        return scope._uri;
                    }
                }

                return null;
            }
        }

        private sealed class DocumentState
        {
            public DocumentState(Dictionary<string, string> templates, string templateName)
            {
                Templates = templates;
                TemplateName = templateName;
            }

            public Dictionary<string, string> Templates { get; }

            public string TemplateName { get; }
        }

        private sealed class ParseContext
        {
            private bool _depthReported;

            public ParseContext(UxmlParseOptions options)
            {
                Limits = options?.Limits ?? RuntimeUiParseLimits.Default;
                Resolver = options?.TemplateResolver;
                Diagnostics = new RuntimeUiDiagnosticList(Limits.MaxDiagnostics);
            }

            public RuntimeUiParseLimits Limits { get; }

            public IUxmlTemplateResolver Resolver { get; }

            public RuntimeUiDiagnosticList Diagnostics { get; }

            public int ElementCount { get; private set; }

            public bool LimitReached { get; private set; }

            public List<string> TemplateStack { get; } = new();

            public Dictionary<string, UxmlNode> TemplateCache { get; } = new(StringComparer.Ordinal);

            public void Report(string code, RuntimeUiDiagnosticSeverity severity, int line, int column, string message,
                string templateName, RuntimeUiSource source = RuntimeUiSource.Uxml)
            {
                string text = templateName == null ? message : "in template '" + templateName + "': " + message;
                Diagnostics.Add(code, severity, source, line, column, text);
            }

            public void ReportDepthOnce(int line, int column, string templateName)
            {
                if (_depthReported)
                {
                    return;
                }

                _depthReported = true;
                Report(RuntimeUiDiagnosticCodes.UxmlLimitExceeded, RuntimeUiDiagnosticSeverity.Error, line, column,
                    "elements nest deeper than " + Limits.MaxDepth + " levels; deeper elements were not built",
                    templateName);
            }

            public void RewindElementCount(int count)
            {
                if (count < ElementCount)
                {
                    ElementCount = count;
                }
            }

            public bool TryCountElement(int line, int column, string templateName)
            {
                return TryCountElements(1, line, column, templateName);
            }

            public bool TryCountElements(int count, int line, int column, string templateName)
            {
                if (LimitReached)
                {
                    return false;
                }

                if (ElementCount + count > Limits.MaxElements)
                {
                    LimitReached = true;
                    Report(RuntimeUiDiagnosticCodes.UxmlLimitExceeded, RuntimeUiDiagnosticSeverity.Error, line, column,
                        "more than " + Limits.MaxElements + " elements; the rest of the markup was not built",
                        templateName);
                    return false;
                }

                ElementCount += count;
                return true;
            }
        }
    }
}
