using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.TextChat
{
    /// <summary>What the host did with a line a script sent through <c>TextChannel:SendAsync</c>.</summary>
    public enum RbxChatSendKind
    {
        /// <summary>The line is an ordinary message: it is delivered to the channel.</summary>
        Delivered = 0,

        /// <summary>The line named a command of the chat, which ran instead of sending it.</summary>
        Command = 1
    }

    /// <summary>The answer of <see cref="IRbxChatHost.RunCommand"/>.</summary>
    public readonly struct RbxChatSendResult
    {
        private readonly string _reply;

        private RbxChatSendResult(RbxChatSendKind kind, string reply, bool failed, string deliveredText)
        {
            Kind = kind;
            _reply = reply ?? string.Empty;
            Failed = failed;
            DeliveredText = deliveredText;
        }

        /// <summary>What happened to the line.</summary>
        public RbxChatSendKind Kind { get; }

        /// <summary>A command's reply for the chat (may be empty); never null.</summary>
        public string Reply => _reply ?? string.Empty;

        /// <summary>True when the command refused or failed; its reply is an error.</summary>
        public bool Failed { get; }

        /// <summary>The text delivered instead of the line (a command that hands the chat a message); null keeps the line.</summary>
        public string DeliveredText { get; }

        /// <summary>The line is delivered, as <paramref name="text"/> when it is not null.</summary>
        public static RbxChatSendResult Deliver(string text = null)
        {
            return new RbxChatSendResult(RbxChatSendKind.Delivered, string.Empty, false, text);
        }

        /// <summary>A command ran; <paramref name="reply"/> is shown as its answer, as an error when <paramref name="failed"/>.</summary>
        public static RbxChatSendResult Command(string reply, bool failed = false)
        {
            return new RbxChatSendResult(RbxChatSendKind.Command, reply, failed, null);
        }
    }

    /// <summary>A line the host posts into the world's chat (NPC speech, a command result that arrives later, a notice).</summary>
    public readonly struct RbxChatHostLine
    {
        /// <summary>Creates the line; null strings become empty.</summary>
        public RbxChatHostLine(string speaker, string text, string metadata)
        {
            Speaker = speaker ?? string.Empty;
            Text = text ?? string.Empty;
            Metadata = metadata ?? string.Empty;
        }

        /// <summary>Who says it; empty for a system line.</summary>
        public string Speaker { get; }

        /// <summary>The text.</summary>
        public string Text { get; }

        /// <summary>What kind of line it is (<see cref="RbxTextChatMetadata"/>).</summary>
        public string Metadata { get; }
    }

    /// <summary>One command of the host's chat (a C# package's command such as <c>/undo</c>), as scripts list it.</summary>
    public sealed class RbxChatHostCommand
    {
        /// <summary>Creates the entry; null strings become empty.</summary>
        public RbxChatHostCommand(string name, IReadOnlyList<string> aliases, string usage, string description)
        {
            Name = name ?? string.Empty;
            Aliases = aliases ?? Array.Empty<string>();
            Usage = usage ?? string.Empty;
            Description = description ?? string.Empty;
        }

        /// <summary>The name without the prefix, for example <c>undo</c>.</summary>
        public string Name { get; }

        /// <summary>Other names without the prefix.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>Argument syntax, for example <c>[role]</c>; may be empty.</summary>
        public string Usage { get; }

        /// <summary>One sentence for a help list.</summary>
        public string Description { get; }
    }

    /// <summary>
    /// The host's in-world chat, seen from a world's <c>TextChatService</c>: it runs the commands of the chat's
    /// registry, offers a player's line to the host's listeners, lists the host's own commands, posts host lines and
    /// lets a script's chat take over from the host's default chat surface. Called on the world's thread.
    /// </summary>
    public interface IRbxChatHost
    {
        /// <summary>
        /// Runs <paramref name="text"/> when it names a command of the chat (<see cref="RbxChatSendKind.Command"/>);
        /// any other line answers <see cref="RbxChatSendKind.Delivered"/>, possibly with the text to deliver instead.
        /// </summary>
        RbxChatSendResult RunCommand(string text);

        /// <summary>
        /// Offers a delivered line of the local player to the host's listeners (an NPC who was addressed, a recorder);
        /// true when one took it, so the player's own agent must not get it.
        /// </summary>
        bool OfferPlayerLine(string text);

        /// <summary>The host's own commands (not the world's <c>TextChatCommand</c>s), sorted by name.</summary>
        IReadOnlyList<RbxChatHostCommand> GetHostCommands();

        /// <summary>Raised after the host's commands changed.</summary>
        event Action HostCommandsChanged;

        /// <summary>Raised for every line the host posts; may be raised from the host's tick, on the world's thread.</summary>
        event Action<RbxChatHostLine> LinePosted;

        /// <summary>Makes the host's default chat surface step aside until the handle is disposed.</summary>
        IDisposable ClaimChat(string owner);
    }

    /// <summary>The <c>Metadata</c> values CoreAI gives the messages it makes.</summary>
    public static class RbxTextChatMetadata
    {
        /// <summary>A system line of the host (an NPC, a notice).</summary>
        public const string System = "CoreAI.System";

        /// <summary>A command's answer.</summary>
        public const string Command = "CoreAI.Command";

        /// <summary>A failure the player should read.</summary>
        public const string Error = "CoreAI.Error";

        /// <summary>The returned message of <c>SendAsync</c> for a line a listener of the host took.</summary>
        public const string Taken = "CoreAI.Taken";
    }

    /// <summary>Roblox <c>Enum.TextChatMessageStatus</c> values (API dump).</summary>
    public static class RbxTextChatMessageStatus
    {
        /// <summary>Unknown.</summary>
        public const int Unknown = 1;

        /// <summary>Delivered.</summary>
        public const int Success = 2;

        /// <summary>Handed to <c>SendingMessage</c>, not delivered yet.</summary>
        public const int Sending = 3;
    }

    /// <summary>
    /// Mirror <c>TextChannel</c>: one of the world's two default channels, <c>RBXGeneral</c> (what players say) and
    /// <c>RBXSystem</c> (system lines). Detached: it is reached through <c>TextChatService.TextChannels</c> and never
    /// saved or replicated (DEV-142).
    /// </summary>
    public sealed class RbxTextChannel : RbxInstance
    {
        internal RbxTextChannel(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextChannel.MessageReceived(incomingMessage)</c>.</summary>
        public RbxScriptSignal MessageReceived => GetOrCreateSignal("MessageReceived");

        /// <summary>The service the channel belongs to.</summary>
        internal RbxTextChatService Service { get; set; }

        internal void FireMessageReceived(RbxTextChatMessage message)
        {
            if (HasSignalConnections("MessageReceived"))
            {
                FireSignal("MessageReceived", message);
            }
        }
    }

    /// <summary>
    /// Mirror <c>TextChatMessage</c> (NotCreatable): one message of the world's chat. Read-only here: CoreAI has no
    /// <c>OnIncomingMessage</c> callback that would rewrite it.
    /// </summary>
    public sealed class RbxTextChatMessage : RbxInstance
    {
        internal RbxTextChatMessage(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>TextChatMessage.Text</c>.</summary>
        public string Text { get; internal set; } = string.Empty;

        /// <summary>Mirror <c>TextChatMessage.PrefixText</c>: the speaker of a host line.</summary>
        public string PrefixText { get; internal set; } = string.Empty;

        /// <summary>Mirror <c>TextChatMessage.Metadata</c>.</summary>
        public string Metadata { get; internal set; } = string.Empty;

        /// <summary>Mirror <c>TextChatMessage.MessageId</c>.</summary>
        public string MessageId { get; internal set; } = string.Empty;

        /// <summary>Mirror <c>TextChatMessage.Status</c> as its <c>Enum.TextChatMessageStatus</c> value.</summary>
        public int Status { get; internal set; } = RbxTextChatMessageStatus.Success;

        /// <summary>Mirror <c>TextChatMessage.TextSource</c>: the sender, nil for a system line.</summary>
        public RbxInstance TextSource { get; internal set; }

        /// <summary>Mirror <c>TextChatMessage.TextChannel</c>.</summary>
        public RbxTextChannel TextChannel { get; internal set; }
    }

    /// <summary>
    /// Roblox <c>TextChatConfigurations</c>, the base of the two configurations below. <c>Enabled</c> false on either
    /// makes CoreAI's own chat step aside, for as long as the script that wrote it runs (DEV-142).
    /// </summary>
    public abstract class RbxTextChatConfiguration : RbxInstance
    {
        private protected RbxTextChatConfiguration(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>Enabled</c> (default true).</summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>The service the configuration belongs to.</summary>
        internal RbxTextChatService Service { get; set; }

        /// <summary>True while the script that set <see cref="Enabled"/> to false still runs.</summary>
        internal Func<bool> WriterAlive { get; private set; }

        /// <summary>
        /// Sets <see cref="Enabled"/> on behalf of a script; <paramref name="writerAlive"/> answers whether that script
        /// still runs (null: for good). The service then claims or releases the host's chat.
        /// </summary>
        public void SetEnabled(bool value, Func<bool> writerAlive)
        {
            ThrowIfDestroyed(nameof(Enabled));
            WriterAlive = value ? null : writerAlive;
            if (Enabled == value)
            {
                return;
            }

            Enabled = value;
            NotifyPropertyChanged(nameof(Enabled));
            Service?.OnConfigurationChanged();
        }
    }

    /// <summary>Mirror <c>ChatWindowConfiguration</c> (NotCreatable): the default chat window's switch.</summary>
    public sealed class RbxChatWindowConfiguration : RbxTextChatConfiguration
    {
        internal RbxChatWindowConfiguration(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }
    }

    /// <summary>
    /// Mirror <c>ChatInputBarConfiguration</c> (NotCreatable): the default input bar's switch, and
    /// <c>KeyboardKeyCode</c>, the extra key that focuses it (recorded for a script's chat to read).
    /// </summary>
    public sealed class RbxChatInputBarConfiguration : RbxTextChatConfiguration
    {
        private int _keyboardKeyCode;

        internal RbxChatInputBarConfiguration(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary>Mirror <c>KeyboardKeyCode</c> as its <c>Enum.KeyCode</c> value (0, <c>Unknown</c>, by default).</summary>
        public int KeyboardKeyCode
        {
            get => _keyboardKeyCode;
            set
            {
                ThrowIfDestroyed(nameof(KeyboardKeyCode));
                if (_keyboardKeyCode == value)
                {
                    return;
                }

                _keyboardKeyCode = value;
                NotifyPropertyChanged(nameof(KeyboardKeyCode));
            }
        }
    }
}
