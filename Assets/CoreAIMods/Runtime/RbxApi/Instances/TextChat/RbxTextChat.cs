using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.TextChat
{
    /// <summary>
    /// The in-world chat's command list, seen from the engine-free Roblox layer: the host adapts its chat command
    /// registry to it, so a <see cref="RbxTextChatCommand"/> under <see cref="RbxTextChatService"/> becomes a slash
    /// command of that chat. Names are given without the slash.
    /// </summary>
    public interface IRbxChatCommandSink
    {
        /// <summary>True when the chat already answers <paramref name="alias"/> (a game's own command or an earlier one).</summary>
        bool IsTaken(string alias);

        /// <summary>
        /// Adds a command answering <paramref name="name"/> and, when not null, <paramref name="secondaryAlias"/>;
        /// <paramref name="trigger"/> receives the whole line the player sent. Disposing the handle removes it.
        /// Throws <see cref="ArgumentException"/> for a name the chat cannot hold.
        /// </summary>
        IDisposable Add(string name, string secondaryAlias, Action<string> trigger);
    }

    /// <summary>
    /// Mirror <c>TextSource</c> (NotCreatable): who sent a chat line. <c>UserId</c> is the sending player's, and
    /// <c>CanSend</c> (default true) is recorded. CoreAI has no <c>TextChannel</c>, so a TextSource has no parent.
    /// </summary>
    public sealed class RbxTextSource : RbxInstance
    {
        private bool _canSend = true;

        internal RbxTextSource(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextSource.UserId</c> (read-only).</summary>
        public long UserId { get; internal set; }

        /// <summary>Mirror <c>TextSource.CanSend</c>.</summary>
        public bool CanSend
        {
            get => _canSend;
            set
            {
                ThrowIfDestroyed(nameof(CanSend));
                if (_canSend == value)
                {
                    return;
                }

                _canSend = value;
                Registry?.AdvanceRevision(Id, nameof(CanSend));
                NotifyPropertyChanged(nameof(CanSend));
            }
        }
    }

    /// <summary>
    /// Mirror <c>TextChatCommand</c>: a slash command a script adds by parenting it under <c>TextChatService</c>.
    /// While it is there and <see cref="Enabled"/>, the in-world chat answers <see cref="PrimaryAlias"/> and
    /// <see cref="SecondaryAlias"/> (a leading slash is optional), and a matching line fires <see cref="Triggered"/>
    /// with the sender's <c>TextSource</c> and the whole line instead of reaching the chat.
    /// </summary>
    public sealed class RbxTextChatCommand : RbxInstance
    {
        private string _primaryAlias = string.Empty;
        private string _secondaryAlias = string.Empty;
        private bool _enabled = true;
        private bool _autocompleteVisible = true;

        internal RbxTextChatCommand(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextChatCommand.Triggered(originTextSource, unfilteredText)</c>.</summary>
        public RbxScriptSignal Triggered => GetOrCreateSignal("Triggered");

        /// <summary>Mirror <c>TextChatCommand.PrimaryAlias</c>, for example <c>/time</c>.</summary>
        public string PrimaryAlias
        {
            get => _primaryAlias;
            set => SetText(ref _primaryAlias, value, nameof(PrimaryAlias));
        }

        /// <summary>Mirror <c>TextChatCommand.SecondaryAlias</c>.</summary>
        public string SecondaryAlias
        {
            get => _secondaryAlias;
            set => SetText(ref _secondaryAlias, value, nameof(SecondaryAlias));
        }

        /// <summary>Mirror <c>TextChatCommand.Enabled</c>: a disabled command is not answered.</summary>
        public bool Enabled
        {
            get => _enabled;
            set => SetBool(ref _enabled, value, nameof(Enabled));
        }

        /// <summary>Mirror <c>TextChatCommand.AutocompleteVisible</c> (recorded; the chat lists every command).</summary>
        public bool AutocompleteVisible
        {
            get => _autocompleteVisible;
            set => SetBool(ref _autocompleteVisible, value, nameof(AutocompleteVisible));
        }

        /// <summary>The service registering this command; told after a change that can move its registration.</summary>
        internal Action<RbxTextChatCommand> StateChanged { get; set; }

        internal void FireTriggered(RbxInstance textSource, string text)
        {
            if (HasSignalConnections("Triggered"))
            {
                FireSignal("Triggered", textSource, text ?? string.Empty);
            }
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxTextChatCommand command = (RbxTextChatCommand)copy;
            command._primaryAlias = _primaryAlias;
            command._secondaryAlias = _secondaryAlias;
            command._enabled = _enabled;
            command._autocompleteVisible = _autocompleteVisible;
        }

        /// <summary>
        /// Puts a saved <c>text_chat_command</c> block back: the same values the setters take, the property signals of
        /// what changed and the chat's update, and no revision advance (the revision is part of the saved world).
        /// </summary>
        public void RestoreDurableState(string primaryAlias, string secondaryAlias, bool enabled, bool autocompleteVisible)
        {
            ThrowIfDestroyed(nameof(PrimaryAlias));
            bool changed = Restore(ref _primaryAlias, primaryAlias ?? string.Empty, nameof(PrimaryAlias));
            changed |= Restore(ref _secondaryAlias, secondaryAlias ?? string.Empty, nameof(SecondaryAlias));
            changed |= Restore(ref _enabled, enabled, nameof(Enabled));
            changed |= Restore(ref _autocompleteVisible, autocompleteVisible, nameof(AutocompleteVisible));
            if (changed)
            {
                StateChanged?.Invoke(this);
            }
        }

        private bool Restore<T>(ref T field, T value, string member)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            NotifyPropertyChanged(member);
            return true;
        }

        private void SetText(ref string field, string value, string member)
        {
            ThrowIfDestroyed(member);
            string text = value ?? string.Empty;
            if (string.Equals(field, text, StringComparison.Ordinal))
            {
                return;
            }

            field = text;
            Changed(member);
        }

        private void SetBool(ref bool field, bool value, string member)
        {
            ThrowIfDestroyed(member);
            if (field == value)
            {
                return;
            }

            field = value;
            Changed(member);
        }

        private void Changed(string member)
        {
            Registry?.AdvanceRevision(Id, member);
            NotifyPropertyChanged(member);
            StateChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Mirror <c>TextChatService</c>, the commands part: every <see cref="RbxTextChatCommand"/> below it that is
    /// enabled and has an alias is a command of the in-world chat while a host attached the chat
    /// (<see cref="AttachCommandSink"/>). A command goes away when it is destroyed (with its mod), disabled or moved
    /// out; an alias the chat already answers is kept by its first owner and the newcomer is reported once.
    /// <c>ChatVersion</c> is <c>TextChatService</c>; <c>CreateDefaultCommands</c> and
    /// <c>CreateDefaultTextChannels</c> are recorded (CoreAI's chat has its own commands and one channel).
    /// </summary>
    public sealed class RbxTextChatService : RbxInstance
    {
        private readonly Dictionary<RbxTextChatCommand, Registration> _registered = new();
        private readonly HashSet<RbxTextChatCommand> _commands = new();
        private readonly HashSet<RbxTextChatCommand> _reported = new();
        private readonly List<RbxTextChatCommand> _scratch = new();
        private IRbxChatCommandSink _sink;
        private Func<RbxInstance> _textSource;
        private Action<string> _log;
        private InstanceRegistry _subscribed;
        private Action<RbxTextChatCommand> _onCommandChanged;

        internal RbxTextChatService(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextChatService.ChatVersion</c>: always <c>Enum.ChatVersion.TextChatService</c> (1).</summary>
        public int ChatVersion => 1;

        /// <summary>Mirror <c>TextChatService.CreateDefaultCommands</c> (default true; recorded).</summary>
        public bool CreateDefaultCommands { get; internal set; } = true;

        /// <summary>Mirror <c>TextChatService.CreateDefaultTextChannels</c> (default true; recorded).</summary>
        public bool CreateDefaultTextChannels { get; internal set; } = true;

        /// <summary>The commands now answered by the attached chat.</summary>
        public int RegisteredCommandCount => _registered.Count;

        /// <summary>
        /// Connects the in-world chat: <paramref name="textSource"/> answers the sender handed to
        /// <c>Triggered</c> (the local player's TextSource), and <paramref name="log"/> receives one line per
        /// refused command. Replaces an earlier sink.
        /// </summary>
        public void AttachCommandSink(IRbxChatCommandSink sink, Func<RbxInstance> textSource, Action<string> log)
        {
            DetachCommandSink();
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _textSource = textSource;
            _log = log;
            InstanceRegistry registry = Registry;
            if (registry != null && !ReferenceEquals(registry, _subscribed))
            {
                _subscribed = registry;
                registry.Registered += OnRegistered;
                registry.Unregistered += OnUnregistered;
                registry.ParentChanged += OnParentChanged;
                IReadOnlyList<RbxInstance> live = registry.GetLiveInstances();
                for (int index = 0; index < live.Count; index++)
                {
                    if (live[index] is RbxTextChatCommand command)
                    {
                        Track(command);
                    }
                }
            }

            EvaluateAll();
        }

        /// <summary>Removes every command from the chat and forgets the sink.</summary>
        public void DetachCommandSink()
        {
            foreach (KeyValuePair<RbxTextChatCommand, Registration> pair in _registered)
            {
                pair.Value.Handle.Dispose();
            }

            _registered.Clear();
            _sink = null;
            _textSource = null;
        }

        /// <inheritdoc />
        protected override void DisconnectCustomSignals()
        {
            base.DisconnectCustomSignals();
            DetachCommandSink();
            if (_subscribed != null)
            {
                _subscribed.Registered -= OnRegistered;
                _subscribed.Unregistered -= OnUnregistered;
                _subscribed.ParentChanged -= OnParentChanged;
                _subscribed = null;
            }

            foreach (RbxTextChatCommand command in _commands)
            {
                command.StateChanged = null;
            }

            _commands.Clear();
        }

        private void OnRegistered(InstanceRecord record)
        {
            if (record?.Instance is RbxTextChatCommand command)
            {
                Track(command);
                Evaluate(command);
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record?.Instance is RbxTextChatCommand command && _commands.Remove(command))
            {
                command.StateChanged = null;
                _reported.Remove(command);
                bool held = _registered.ContainsKey(command);
                Release(command);
                if (held)
                {
                    // WHY: a command that lost the alias to this one may take it now.
                    EvaluateAll();
                }
            }
        }

        private void OnParentChanged(RbxInstance instance)
        {
            if (_commands.Count > 0)
            {
                EvaluateAll();
            }
        }

        private void Track(RbxTextChatCommand command)
        {
            if (_commands.Add(command))
            {
                _onCommandChanged ??= Evaluate;
                command.StateChanged = _onCommandChanged;
            }
        }

        private void EvaluateAll()
        {
            _scratch.Clear();
            _scratch.AddRange(_commands);
            for (int index = 0; index < _scratch.Count; index++)
            {
                Evaluate(_scratch[index]);
            }

            _scratch.Clear();
        }

        private void Evaluate(RbxTextChatCommand command)
        {
            string name = Normalize(command.PrimaryAlias);
            bool wanted = _sink != null && !IsDestroyed && !command.IsDestroyed && command.Enabled
                          && name.Length > 0 && command.IsDescendantOf(this);
            string second = Normalize(command.SecondaryAlias);
            string secondary = wanted && second.Length > 0
                                      && !string.Equals(second, name, StringComparison.OrdinalIgnoreCase)
                ? second
                : null;
            if (_registered.TryGetValue(command, out Registration current))
            {
                if (wanted && string.Equals(current.Name, name, StringComparison.Ordinal)
                           && string.Equals(current.Secondary, secondary, StringComparison.Ordinal))
                {
                    return;
                }

                Release(command);
            }

            if (!wanted)
            {
                return;
            }

            if (_sink.IsTaken(name))
            {
                Report(command, "'/" + name + "' is already a chat command; the command registered first is kept");
                return;
            }

            if (secondary != null && _sink.IsTaken(secondary))
            {
                Report(command, "'/" + secondary + "' is already a chat command, so only '/" + name + "' triggers "
                                + command.GetFullName());
                secondary = null;
            }

            IDisposable handle;
            try
            {
                handle = _sink.Add(name, secondary, text => OnTriggered(command, text));
            }
            catch (ArgumentException exception)
            {
                Report(command, exception.Message);
                return;
            }

            _registered[command] = new Registration(name, secondary, handle);
        }

        private void Release(RbxTextChatCommand command)
        {
            if (_registered.TryGetValue(command, out Registration registration))
            {
                _registered.Remove(command);
                registration.Handle.Dispose();
            }
        }

        private void OnTriggered(RbxTextChatCommand command, string text)
        {
            if (command.IsDestroyed || !command.Enabled)
            {
                return;
            }

            command.FireTriggered(_textSource?.Invoke(), text);
        }

        private void Report(RbxTextChatCommand command, string message)
        {
            if (_reported.Add(command))
            {
                _log?.Invoke("[TextChatService] " + command.GetFullName() + ": " + message);
            }
        }

        private static string Normalize(string alias)
        {
            string text = (alias ?? string.Empty).Trim();
            while (text.Length > 0 && text[0] == '/')
            {
                text = text.Substring(1);
            }

            return text;
        }

        private readonly struct Registration
        {
            public Registration(string name, string secondary, IDisposable handle)
            {
                Name = name;
                Secondary = secondary;
                Handle = handle;
            }

            public string Name { get; }

            public string Secondary { get; }

            public IDisposable Handle { get; }
        }
    }
}
