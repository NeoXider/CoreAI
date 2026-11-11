using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// The set of slash commands one chat understands. Packages and games add or remove commands at runtime
    /// without touching package code; names and aliases are matched without regard to case.
    /// </summary>
    public interface IChatCommandRegistry
    {
        /// <summary>Raised after a command was added or removed.</summary>
        event Action Changed;

        /// <summary>Snapshot of the registered commands, sorted by name.</summary>
        IReadOnlyList<IChatCommand> Commands { get; }

        /// <summary>
        /// Adds <paramref name="command"/> under its name and aliases. A name or alias that is already taken
        /// throws <see cref="InvalidOperationException"/> unless <paramref name="replaceExisting"/> is true, which
        /// removes every command holding one of them. Disposing the returned handle removes this registration
        /// (and only it: a command that replaced it stays).
        /// </summary>
        IDisposable Register(IChatCommand command, bool replaceExisting = false);

        /// <summary>Removes the command that owns <paramref name="nameOrAlias"/>; false when there is none.</summary>
        bool Unregister(string nameOrAlias);

        /// <summary>Finds the command that owns <paramref name="nameOrAlias"/> (a leading prefix is ignored).</summary>
        bool TryResolve(string nameOrAlias, out IChatCommand command);
    }

    /// <summary>Default <see cref="IChatCommandRegistry"/>; safe to call from any thread.</summary>
    public sealed class ChatCommandRegistry : IChatCommandRegistry
    {
        /// <summary>Longest accepted command name or alias.</summary>
        public const int MaxNameLength = 32;

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _byKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Entry> _entries = new();
        private IReadOnlyList<IChatCommand> _snapshot;

        /// <inheritdoc />
        public event Action Changed;

        /// <inheritdoc />
        public IReadOnlyList<IChatCommand> Commands
        {
            get
            {
                lock (_gate)
                {
                    if (_snapshot == null)
                    {
                        List<IChatCommand> commands = new(_entries.Count);
                        foreach (Entry entry in _entries)
                        {
                            commands.Add(entry.Command);
                        }

                        commands.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                        _snapshot = commands;
                    }

                    return _snapshot;
                }
            }
        }

        /// <summary>True when <paramref name="name"/> can be a command name or alias: no whitespace, no control characters.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return false;
            }

            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public IDisposable Register(IChatCommand command, bool replaceExisting = false)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            List<string> keys = CollectKeys(command);
            Entry added = new(command, keys);
            lock (_gate)
            {
                List<Entry> conflicts = new();
                foreach (string key in keys)
                {
                    if (_byKey.TryGetValue(key, out Entry owner) && !conflicts.Contains(owner))
                    {
                        if (!replaceExisting)
                        {
                            throw new InvalidOperationException(
                                $"Chat command '{key}' is already registered by '{owner.Command.Name}'. " +
                                "Pass replaceExisting: true to replace it.");
                        }

                        conflicts.Add(owner);
                    }
                }

                foreach (Entry conflict in conflicts)
                {
                    RemoveEntryLocked(conflict);
                }

                foreach (string key in keys)
                {
                    _byKey[key] = added;
                }

                _entries.Add(added);
                _snapshot = null;
            }

            Changed?.Invoke();
            return new Registration(this, added);
        }

        /// <inheritdoc />
        public bool Unregister(string nameOrAlias)
        {
            string key = NormalizeKey(nameOrAlias);
            bool removed;
            lock (_gate)
            {
                removed = key.Length > 0 && _byKey.TryGetValue(key, out Entry owner) && RemoveEntryLocked(owner);
            }

            if (removed)
            {
                Changed?.Invoke();
            }

            return removed;
        }

        /// <inheritdoc />
        public bool TryResolve(string nameOrAlias, out IChatCommand command)
        {
            string key = NormalizeKey(nameOrAlias);
            lock (_gate)
            {
                if (key.Length > 0 && _byKey.TryGetValue(key, out Entry owner))
                {
                    command = owner.Command;
                    return true;
                }
            }

            command = null;
            return false;
        }

        private void Remove(Entry entry)
        {
            bool removed;
            lock (_gate)
            {
                removed = RemoveEntryLocked(entry);
            }

            if (removed)
            {
                Changed?.Invoke();
            }
        }

        private bool RemoveEntryLocked(Entry entry)
        {
            if (!_entries.Remove(entry))
            {
                return false;
            }

            foreach (string key in entry.Keys)
            {
                if (_byKey.TryGetValue(key, out Entry owner) && ReferenceEquals(owner, entry))
                {
                    _byKey.Remove(key);
                }
            }

            _snapshot = null;
            return true;
        }

        private static List<string> CollectKeys(IChatCommand command)
        {
            List<string> keys = new();
            AddKey(keys, command.Name, command.Name);
            IReadOnlyList<string> aliases = command.Aliases;
            if (aliases != null)
            {
                foreach (string alias in aliases)
                {
                    AddKey(keys, alias, command.Name);
                }
            }

            return keys;
        }

        private static void AddKey(List<string> keys, string candidate, string commandName)
        {
            string key = (candidate ?? "").Trim();
            if (!IsValidName(key))
            {
                throw new ArgumentException(
                    $"'{candidate}' is not a valid chat command name for '{commandName}': use 1-{MaxNameLength} " +
                    "characters without spaces.");
            }

            foreach (string existing in keys)
            {
                if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            keys.Add(key);
        }

        private static string NormalizeKey(string nameOrAlias)
        {
            string key = (nameOrAlias ?? "").Trim();
            while (key.Length > 0 && key[0] == ChatCommandParser.DefaultPrefix)
            {
                key = key.Substring(1);
            }

            return key;
        }

        private sealed class Entry
        {
            public Entry(IChatCommand command, List<string> keys)
            {
                Command = command;
                Keys = keys;
            }

            public IChatCommand Command { get; }

            public List<string> Keys { get; }
        }

        private sealed class Registration : IDisposable
        {
            private ChatCommandRegistry _registry;
            private readonly Entry _entry;

            public Registration(ChatCommandRegistry registry, Entry entry)
            {
                _registry = registry;
                _entry = entry;
            }

            public void Dispose()
            {
                ChatCommandRegistry registry = _registry;
                _registry = null;
                registry?.Remove(_entry);
            }
        }
    }
}
