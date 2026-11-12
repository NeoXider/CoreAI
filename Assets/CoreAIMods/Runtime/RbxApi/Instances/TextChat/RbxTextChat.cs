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
    /// Mirror <c>TextChatService</c>. The messages part (<see cref="AttachChatHost"/>): the detached default channels,
    /// <c>SendAsync</c>, <c>DisplaySystemMessage</c>, <c>MessageReceived</c>, <c>SendingMessage</c>, the two
    /// configurations whose <c>Enabled = false</c> makes the host's own chat step aside, and the host's commands
    /// (DEV-142). The commands part: every <see cref="RbxTextChatCommand"/> below it that is
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
        private IRbxChatHost _host;
        private Func<RbxInstance> _hostTextSource;
        private Action<RbxChatHostLine> _onHostLine;
        private Action _onHostCommandsChanged;
        private IDisposable _chatClaim;
        private RbxInstance _channelsFolder;
        private RbxTextChannel _general;
        private RbxTextChannel _system;
        private RbxChatWindowConfiguration _window;
        private RbxChatInputBarConfiguration _inputBar;
        private RbxInstance _hostCommandsFolder;
        private readonly List<RbxTextChatCommand> _hostCommandEntries = new();
        private bool _hostCommandsDirty = true;
        private long _messageCounter;

        internal RbxTextChatService(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextChatService.MessageReceived(textChatMessage)</c>: every message shown on this machine.</summary>
        public RbxScriptSignal MessageReceived => GetOrCreateSignal("MessageReceived");

        /// <summary>Mirror <c>TextChatService.SendingMessage(textChatMessage)</c>: fired by <c>SendAsync</c> before delivery.</summary>
        public RbxScriptSignal SendingMessage => GetOrCreateSignal("SendingMessage");

        /// <summary>The <c>TextChannels</c> folder with <c>RBXGeneral</c> and <c>RBXSystem</c> (detached, DEV-142).</summary>
        public RbxInstance TextChannels
        {
            get
            {
                EnsureChannels();
                return _channelsFolder;
            }
        }

        /// <summary><c>TextChannels.RBXGeneral</c>: what players say.</summary>
        public RbxTextChannel GeneralChannel
        {
            get
            {
                EnsureChannels();
                return _general;
            }
        }

        /// <summary><c>TextChannels.RBXSystem</c>: system lines.</summary>
        public RbxTextChannel SystemChannel
        {
            get
            {
                EnsureChannels();
                return _system;
            }
        }

        /// <summary>Mirror <c>TextChatService.ChatWindowConfiguration</c> (detached).</summary>
        public RbxChatWindowConfiguration ChatWindowConfiguration
        {
            get
            {
                EnsureConfigurations();
                return _window;
            }
        }

        /// <summary>Mirror <c>TextChatService.ChatInputBarConfiguration</c> (detached).</summary>
        public RbxChatInputBarConfiguration ChatInputBarConfiguration
        {
            get
            {
                EnsureConfigurations();
                return _inputBar;
            }
        }

        /// <summary>
        /// The host's own commands as read-only <c>TextChatCommand</c>s in a detached <c>TextChatCommands</c> folder:
        /// <c>PrimaryAlias</c>, <c>SecondaryAlias</c> and the attributes <c>Description</c> and <c>Usage</c>. Writing
        /// them changes nothing in the host. Empty without an attached host. A script's own <c>TextChatCommand</c>
        /// parented here is a command of the chat, as in Roblox, and stays when the host's list changes.
        /// </summary>
        public RbxInstance TextChatCommands
        {
            get
            {
                RefreshHostCommands();
                return _hostCommandsFolder;
            }
        }

        /// <summary>True while a script holds a configuration's <c>Enabled</c> at false and the host's chat stepped aside.</summary>
        public bool IsChatClaimed => _chatClaim != null;

        /// <summary>True while a chat host is attached.</summary>
        public bool HasChatHost => _host != null;

        /// <summary>
        /// Connects the host's in-world chat: <c>SendAsync</c> goes through <paramref name="host"/>, host lines arrive as
        /// system messages, and <paramref name="textSource"/> answers the local player's <c>TextSource</c>. Replaces an
        /// earlier host; a claim the world held moves to the new one.
        /// </summary>
        public void AttachChatHost(IRbxChatHost host, Func<RbxInstance> textSource)
        {
            DetachChatHost();
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _hostTextSource = textSource;
            _onHostLine ??= OnHostLine;
            _onHostCommandsChanged ??= () => _hostCommandsDirty = true;
            _host.LinePosted += _onHostLine;
            _host.HostCommandsChanged += _onHostCommandsChanged;
            _hostCommandsDirty = true;
            OnConfigurationChanged();
        }

        /// <summary>Disconnects the chat host and releases a claim on its chat.</summary>
        public void DetachChatHost()
        {
            if (_host != null)
            {
                _host.LinePosted -= _onHostLine;
                _host.HostCommandsChanged -= _onHostCommandsChanged;
            }

            _chatClaim?.Dispose();
            _chatClaim = null;
            _host = null;
            _hostTextSource = null;
            _hostCommandsDirty = true;
        }

        /// <summary>
        /// Mirror <c>TextChannel:SendAsync(message, metadata)</c> for the local player: fires <c>SendingMessage</c>, then
        /// a line naming a command runs it (its reply arrives in <c>RBXSystem</c>), a line a host listener takes stops
        /// there, and any other line is delivered. The returned message's <c>Metadata</c> says which
        /// (<see cref="RbxTextChatMetadata.Command"/>, <see cref="RbxTextChatMetadata.Taken"/>, or the metadata given).
        /// </summary>
        public RbxTextChatMessage SendAsync(RbxTextChannel channel, string text, string metadata)
        {
            ThrowIfDestroyed("SendAsync");
            RbxTextChannel target = channel ?? GeneralChannel;
            RbxInstance source = _hostTextSource?.Invoke() ?? _textSource?.Invoke();
            RbxTextChatMessage message = NewMessage(target, text, metadata, string.Empty, source,
                RbxTextChatMessageStatus.Sending);
            if (HasSignalConnections("SendingMessage"))
            {
                FireSignal("SendingMessage", message);
            }

            RbxChatSendResult result = _host?.RunCommand(message.Text) ?? RunWorldCommand(message.Text, source);
            message.Status = RbxTextChatMessageStatus.Success;
            if (result.Kind == RbxChatSendKind.Command)
            {
                message.Metadata = RbxTextChatMetadata.Command;
                if (result.Reply.Length > 0)
                {
                    DisplaySystemMessage(SystemChannel, result.Reply,
                        result.Failed ? RbxTextChatMetadata.Error : RbxTextChatMetadata.Command, string.Empty);
                }

                return message;
            }

            if (result.DeliveredText != null)
            {
                message.Text = result.DeliveredText;
            }

            // WHY delivered before the listeners hear it: a villager who answers at once (a line without a model)
            // must speak after the line it answers; a taken line was still said in the channel, only the AI does
            // not get it.
            Deliver(message);
            if (_host != null && message.Text.Length > 0 && _host.OfferPlayerLine(message.Text))
            {
                message.Metadata = RbxTextChatMetadata.Taken;
            }

            return message;
        }

        /// <summary>
        /// Mirror <c>TextChannel:DisplaySystemMessage(systemMessage, metadata)</c>: a message shown on this machine only,
        /// with <paramref name="prefix"/> as its <c>PrefixText</c>.
        /// </summary>
        public RbxTextChatMessage DisplaySystemMessage(RbxTextChannel channel, string text, string metadata,
            string prefix)
        {
            ThrowIfDestroyed("DisplaySystemMessage");
            RbxTextChatMessage message = NewMessage(channel ?? SystemChannel, text, metadata, prefix, null,
                RbxTextChatMessageStatus.Success);
            Deliver(message);
            return message;
        }

        /// <summary>
        /// Gives the host's chat back when the script that switched a configuration off no longer runs: its
        /// <c>Enabled</c> returns to true. The world calls it once per frame.
        /// </summary>
        public void CheckConfigurationWriters()
        {
            Revert(_window);
            Revert(_inputBar);
        }

        internal void OnConfigurationChanged()
        {
            bool wanted = _host != null && !IsDestroyed
                                        && ((_window != null && !_window.Enabled)
                                            || (_inputBar != null && !_inputBar.Enabled));
            if (wanted && _chatClaim == null)
            {
                _chatClaim = _host.ClaimChat("world chat (TextChatService)");
            }
            else if (!wanted && _chatClaim != null)
            {
                _chatClaim.Dispose();
                _chatClaim = null;
            }
        }

        private static void Revert(RbxTextChatConfiguration configuration)
        {
            if (configuration == null || configuration.Enabled || configuration.IsDestroyed)
            {
                return;
            }

            Func<bool> alive = configuration.WriterAlive;
            if (alive != null && !alive())
            {
                configuration.SetEnabled(true, null);
            }
        }

        /// <summary>
        /// Without a host, the world's own <c>TextChatCommand</c>s still answer <c>SendAsync</c>, as in Roblox: a line
        /// naming an enabled command fires its <c>Triggered</c> instead of being sent.
        /// </summary>
        private RbxChatSendResult RunWorldCommand(string text, RbxInstance source)
        {
            string line = (text ?? string.Empty).Trim();
            if (line.Length >= 2 && line[0] == '/' && line[1] == '/')
            {
                return RbxChatSendResult.Deliver(line.Substring(1));
            }

            if (line.Length < 2 || line[0] != '/')
            {
                return RbxChatSendResult.Deliver();
            }

            int end = line.IndexOfAny(new[] { ' ', '\t' }, 1);
            string name = end < 0 ? line.Substring(1) : line.Substring(1, end - 1);
            RbxTextChatCommand found = FindWorldCommand(GetDescendants(), name)
                                       ?? (_hostCommandsFolder != null
                                           ? FindWorldCommand(_hostCommandsFolder.GetDescendants(), name)
                                           : null);
            if (found == null)
            {
                return RbxChatSendResult.Deliver();
            }

            found.FireTriggered(source, line);
            return RbxChatSendResult.Command(string.Empty);
        }

        private RbxTextChatCommand FindWorldCommand(IReadOnlyList<RbxInstance> candidates, string name)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] is RbxTextChatCommand command && command.Enabled
                    && !_hostCommandEntries.Contains(command)
                    && (string.Equals(Normalize(command.PrimaryAlias), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Normalize(command.SecondaryAlias), name, StringComparison.OrdinalIgnoreCase)))
                {
                    return command;
                }
            }

            return null;
        }

        private void OnHostLine(RbxChatHostLine line)
        {
            if (IsDestroyed || line.Text.Length == 0)
            {
                return;
            }

            DisplaySystemMessage(SystemChannel, line.Text,
                line.Metadata.Length == 0 ? RbxTextChatMetadata.System : line.Metadata, line.Speaker);
        }

        private void Deliver(RbxTextChatMessage message)
        {
            message.TextChannel?.FireMessageReceived(message);
            if (HasSignalConnections("MessageReceived"))
            {
                FireSignal("MessageReceived", message);
            }
        }

        private RbxTextChatMessage NewMessage(RbxTextChannel channel, string text, string metadata, string prefix,
            RbxInstance source, int status)
        {
            RbxTextChatMessage message = new(DescriptorOf("TextChatMessage", "Instance"))
            {
                Text = text ?? string.Empty,
                Metadata = metadata ?? string.Empty,
                PrefixText = prefix ?? string.Empty,
                TextSource = source,
                TextChannel = channel,
                Status = status,
                MessageId = "coreai-" + (++_messageCounter).ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            message.Name = "TextChatMessage";
            return message;
        }

        private void EnsureChannels()
        {
            if (_channelsFolder != null)
            {
                return;
            }

            _channelsFolder = new RbxInstance(DescriptorOf("Folder", "Instance")) { Name = "TextChannels" };
            _general = new RbxTextChannel(DescriptorOf("TextChannel", "Instance")) { Name = "RBXGeneral", Service = this };
            _system = new RbxTextChannel(DescriptorOf("TextChannel", "Instance")) { Name = "RBXSystem", Service = this };
            _general.Parent = _channelsFolder;
            _system.Parent = _channelsFolder;
        }

        private void EnsureConfigurations()
        {
            if (_window != null)
            {
                return;
            }

            _window = new RbxChatWindowConfiguration(DescriptorOf("ChatWindowConfiguration", "TextChatConfigurations"))
            {
                Name = "ChatWindowConfiguration", Service = this
            };
            _inputBar = new RbxChatInputBarConfiguration(
                DescriptorOf("ChatInputBarConfiguration", "TextChatConfigurations"))
            {
                Name = "ChatInputBarConfiguration", Service = this
            };
        }

        private void RefreshHostCommands()
        {
            if (_hostCommandsFolder == null)
            {
                _hostCommandsFolder = new RbxInstance(DescriptorOf("Folder", "Instance")) { Name = "TextChatCommands" };
            }

            if (!_hostCommandsDirty)
            {
                return;
            }

            _hostCommandsDirty = false;
            // WHY only the entries made here: a script may put its own TextChatCommand in this folder, the Roblox
            // place for commands, and it stays a command of the chat (Evaluate counts the folder as inside).
            for (int i = 0; i < _hostCommandEntries.Count; i++)
            {
                _hostCommandEntries[i].Parent = null;
            }

            _hostCommandEntries.Clear();

            IReadOnlyList<RbxChatHostCommand> commands = _host?.GetHostCommands();
            if (commands == null)
            {
                return;
            }

            for (int i = 0; i < commands.Count; i++)
            {
                RbxChatHostCommand entry = commands[i];
                RbxTextChatCommand command = new(DescriptorOf("TextChatCommand", "Instance")) { Name = entry.Name };
                command.PrimaryAlias = "/" + entry.Name;
                command.SecondaryAlias = entry.Aliases.Count > 0 ? "/" + entry.Aliases[0] : string.Empty;
                command.SetAttribute("Description", entry.Description);
                command.SetAttribute("Usage", entry.Usage);
                command.Parent = _hostCommandsFolder;
                _hostCommandEntries.Add(command);
            }
        }

        private ClassDescriptor DescriptorOf(string className, string baseClassName)
        {
            return Registry != null && Registry.Catalog.TryGet(className, out ClassDescriptor descriptor)
                ? descriptor
                : new ClassDescriptor(className, baseClassName, false, false, false);
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
            DetachChatHost();
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
                          && name.Length > 0
                          && (command.IsDescendantOf(this)
                              || (_hostCommandsFolder != null && command.IsDescendantOf(_hostCommandsFolder)));
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
