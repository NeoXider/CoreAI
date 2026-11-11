using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Supplies the UXML text of a template. Runtime screens cannot load project files, so templates are
    /// registered as text (by a mod, the AI or the host game) and looked up by reference.
    /// </summary>
    public interface IUxmlTemplateResolver
    {
        /// <summary>Returns the UXML text for a <c>Template src</c> or template name.</summary>
        bool TryResolve(string reference, out string uxmlText);
    }

    /// <summary>
    /// A name-to-text template store. A lookup tries the reference as given, then without a query
    /// string, then its file name, then the file name without extension, so <c>src="UI/Slot.uxml"</c>
    /// finds a template registered as <c>Slot</c>.
    /// </summary>
    public sealed class UxmlTemplateRegistry : IUxmlTemplateResolver
    {
        private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);

        /// <summary>Number of registered templates.</summary>
        public int Count => _templates.Count;

        /// <summary>Registered template names.</summary>
        public IEnumerable<string> Names => _templates.Keys;

        /// <summary>Registers or replaces a template.</summary>
        public void Register(string name, string uxmlText)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A template needs a name.", nameof(name));
            }

            _templates[name.Trim()] = uxmlText ?? string.Empty;
        }

        /// <summary>Removes a template.</summary>
        public bool Remove(string name)
        {
            return name != null && _templates.Remove(name.Trim());
        }

        /// <summary>Removes every template.</summary>
        public void Clear()
        {
            _templates.Clear();
        }

        /// <inheritdoc />
        public bool TryResolve(string reference, out string uxmlText)
        {
            uxmlText = null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                return false;
            }

            string candidate = reference.Trim();
            if (_templates.TryGetValue(candidate, out uxmlText))
            {
                return true;
            }

            int query = candidate.IndexOfAny(new[] { '?', '#' });
            if (query >= 0)
            {
                candidate = candidate.Substring(0, query);
                if (_templates.TryGetValue(candidate, out uxmlText))
                {
                    return true;
                }
            }

            int slash = candidate.LastIndexOfAny(new[] { '/', '\\' });
            string fileName = slash >= 0 ? candidate.Substring(slash + 1) : candidate;
            if (_templates.TryGetValue(fileName, out uxmlText))
            {
                return true;
            }

            int dot = fileName.LastIndexOf('.');
            return dot > 0 && _templates.TryGetValue(fileName.Substring(0, dot), out uxmlText);
        }
    }
}
