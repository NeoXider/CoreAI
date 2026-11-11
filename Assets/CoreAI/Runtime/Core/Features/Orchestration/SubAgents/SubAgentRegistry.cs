using System;
using System.Collections.Generic;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>The sub-agents a lead agent may delegate to, looked up by <see cref="SubAgentDefinition.Id"/>.</summary>
    public interface ISubAgentRegistry
    {
        /// <summary>Every registered definition, in registration order.</summary>
        IReadOnlyList<SubAgentDefinition> Definitions { get; }

        /// <summary>Finds a definition by id (case-insensitive).</summary>
        bool TryGet(string id, out SubAgentDefinition definition);
    }

    /// <summary>
    /// In-memory <see cref="ISubAgentRegistry"/> built from data. It refuses a definition whose allowlist names
    /// the configured delegate tool, which catches a host that renamed <c>delegate_task</c> and would otherwise
    /// slip past the check in <see cref="SubAgentDefinition"/>.
    /// <para>
    /// Register before the first lead turn: the delegate tool lists the registered sub-agents in its
    /// description, so a later change also changes the lead's tool contract (and its prompt cache).
    /// </para>
    /// </summary>
    public sealed class SubAgentRegistry : ISubAgentRegistry
    {
        private readonly object _gate = new();
        private readonly string _delegateToolName;
        private SubAgentDefinition[] _definitions = Array.Empty<SubAgentDefinition>();

        /// <summary>Creates a registry, optionally seeded with <paramref name="definitions"/>.</summary>
        /// <param name="definitions">Initial definitions; null means none.</param>
        /// <param name="delegateToolName">Name the delegate tool is offered under; no allowlist may contain it.</param>
        public SubAgentRegistry(
            IEnumerable<SubAgentDefinition> definitions = null,
            string delegateToolName = DelegateTaskTool.DefaultName)
        {
            _delegateToolName = string.IsNullOrWhiteSpace(delegateToolName)
                ? DelegateTaskTool.DefaultName
                : delegateToolName.Trim();
            if (definitions == null)
            {
                return;
            }

            foreach (SubAgentDefinition definition in definitions)
            {
                Register(definition);
            }
        }

        /// <summary>Name of the delegate tool this registry guards against.</summary>
        public string DelegateToolName => _delegateToolName;

        /// <inheritdoc />
        public IReadOnlyList<SubAgentDefinition> Definitions
        {
            get
            {
                lock (_gate)
                {
                    return _definitions;
                }
            }
        }

        /// <summary>Adds <paramref name="definition"/>; throws on a duplicate id or a delegate tool in its allowlist.</summary>
        public void Register(SubAgentDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (definition.AllowsTool(_delegateToolName))
            {
                throw new ArgumentException(
                    $"Sub-agent '{definition.Id}' may never be given the delegate tool '{_delegateToolName}'.",
                    nameof(definition));
            }

            lock (_gate)
            {
                foreach (SubAgentDefinition existing in _definitions)
                {
                    if (string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            $"A sub-agent with id '{definition.Id}' is already registered.", nameof(definition));
                    }
                }

                SubAgentDefinition[] next = new SubAgentDefinition[_definitions.Length + 1];
                Array.Copy(_definitions, next, _definitions.Length);
                next[_definitions.Length] = definition;
                _definitions = next;
            }
        }

        /// <summary>Removes the definition with <paramref name="id"/>; returns whether one was removed.</summary>
        public bool Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (_gate)
            {
                int index = IndexOfLocked(id.Trim());
                if (index < 0)
                {
                    return false;
                }

                List<SubAgentDefinition> next = new(_definitions);
                next.RemoveAt(index);
                _definitions = next.ToArray();
                return true;
            }
        }

        /// <inheritdoc />
        public bool TryGet(string id, out SubAgentDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (_gate)
            {
                int index = IndexOfLocked(id.Trim());
                if (index < 0)
                {
                    return false;
                }

                definition = _definitions[index];
                return true;
            }
        }

        private int IndexOfLocked(string id)
        {
            for (int i = 0; i < _definitions.Length; i++)
            {
                if (string.Equals(_definitions[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
