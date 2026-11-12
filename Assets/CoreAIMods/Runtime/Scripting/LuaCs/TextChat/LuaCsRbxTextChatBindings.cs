using System;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.TextChat;
using CoreAI.Scripting.LuaCs;
using Lua;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The messaging half of <c>TextChatService</c> for Lua (roadmap P2, the Roblox subset): the service's
    /// <c>MessageReceived</c>, <c>SendingMessage</c>, <c>TextChannels</c>, the two configurations and the host's
    /// <c>TextChatCommands</c>; a channel's <c>SendAsync</c>, <c>DisplaySystemMessage</c> and
    /// <c>MessageReceived</c>; a configuration's <c>Enabled</c>, whose false belongs to the writing mod (DEV-142).
    /// Called from the GUI member hook of <c>Instance.__index</c> and <c>__newindex</c>; the plain properties of
    /// <c>TextChatMessage</c> are declared in its table.
    /// </summary>
    internal static class LuaCsRbxTextChatBindings
    {
        private static readonly LuaFunction SendAsyncFn = Fn("TextChannel:SendAsync", ctx =>
        {
            RbxTextChannel channel = RequireChannel(ctx, "SendAsync", out LuaCsRbxModContext context);
            string text = ReadString(ctx, 1, "TextChannel:SendAsync", 1);
            string metadata = OptionalString(ctx, 2, "TextChannel:SendAsync", 2);
            RbxTextChatMessage message = channel.Service.SendAsync(channel, text, metadata);
            return context.WrapInstance(message);
        });

        private static readonly LuaFunction DisplaySystemMessageFn = Fn("TextChannel:DisplaySystemMessage", ctx =>
        {
            RbxTextChannel channel = RequireChannel(ctx, "DisplaySystemMessage", out LuaCsRbxModContext context);
            string text = ReadString(ctx, 1, "TextChannel:DisplaySystemMessage", 1);
            string metadata = OptionalString(ctx, 2, "TextChannel:DisplaySystemMessage", 2);
            RbxTextChatMessage message = channel.Service.DisplaySystemMessage(channel, text, metadata, string.Empty);
            return context.WrapInstance(message);
        });

        /// <summary>The members answered here, by declaring class, for the member tables of the GUI hook.</summary>
        internal static readonly (string ClassName, string Member)[] Members =
        {
            ("TextChatService", "MessageReceived"), ("TextChatService", "SendingMessage"),
            ("TextChatService", "TextChannels"), ("TextChatService", "ChatWindowConfiguration"),
            ("TextChatService", "ChatInputBarConfiguration"), ("TextChatService", "TextChatCommands"),
            ("TextChannel", "MessageReceived"), ("TextChannel", "SendAsync"), ("TextChannel", "DisplaySystemMessage"),
            ("TextChatConfigurations", "Enabled")
        };

        /// <summary>True for the instances this table answers.</summary>
        public static bool Answers(RbxInstance instance)
        {
            return instance is RbxTextChatService || instance is RbxTextChannel || instance is RbxTextChatConfiguration;
        }

        /// <summary>Answers a member read; false leaves the key to the other hooks.</summary>
        public static bool TryRead(LuaCsRbxModContext context, RbxInstance self, string key, out LuaValue value)
        {
            value = LuaValue.Nil;
            switch (self)
            {
                case RbxTextChatService service:
                    switch (key)
                    {
                        case "MessageReceived":
                            value = LuaCsRbxDatatypeBindings.Wrap(service.MessageReceived, context);
                            return true;
                        case "SendingMessage":
                            value = LuaCsRbxDatatypeBindings.Wrap(service.SendingMessage, context);
                            return true;
                        case "TextChannels":
                            value = context.WrapInstance(service.TextChannels);
                            return true;
                        case "ChatWindowConfiguration":
                            value = context.WrapInstance(service.ChatWindowConfiguration);
                            return true;
                        case "ChatInputBarConfiguration":
                            value = context.WrapInstance(service.ChatInputBarConfiguration);
                            return true;
                        case "TextChatCommands":
                            value = context.WrapInstance(service.TextChatCommands);
                            return true;
                        default:
                            return false;
                    }
                case RbxTextChannel channel:
                    switch (key)
                    {
                        case "MessageReceived":
                            value = LuaCsRbxDatatypeBindings.Wrap(channel.MessageReceived, context);
                            return true;
                        case "SendAsync":
                            value = SendAsyncFn;
                            return true;
                        case "DisplaySystemMessage":
                            value = DisplaySystemMessageFn;
                            return true;
                        default:
                            return false;
                    }
                case RbxTextChatConfiguration configuration when key == "Enabled":
                    value = configuration.Enabled;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Answers a member write: a configuration's <c>Enabled</c>. A false written by a loaded mod lasts while that
        /// mod runs, and the default chat comes back when it unloads; a one-off chunk's false lasts for good.
        /// </summary>
        public static bool TryWrite(LuaCsRbxModContext context, RbxInstance self, string key, LuaValue value)
        {
            // WHY here and not through the property table: the configurations are detached (never world content),
            // so the world's edit permission and the mutation record do not apply to them.
            if (self is RbxChatInputBarConfiguration bar && key == "KeyboardKeyCode")
            {
                bar.KeyboardKeyCode = ReadAssignedEnumItem(value, context.Bindings.Enums, "KeyCode", self.ClassName, key)
                    .Value;
                return true;
            }

            if (!(self is RbxTextChatConfiguration configuration) || key != "Enabled")
            {
                return false;
            }

            if (value.Type != LuaValueType.Boolean)
            {
                throw RbxError.BadArgument(
                    "Unable to assign property Enabled. bool expected, got " + value.Type.ToString().ToLowerInvariant(),
                    "write " + self.ClassName + ".Enabled = false (or true)");
            }

            bool enabled = value.Read<bool>();
            configuration.SetEnabled(enabled, enabled ? null : WriterLifetime(context));
            return true;
        }

        private static Func<bool> WriterLifetime(LuaCsRbxModContext context)
        {
            if (string.IsNullOrWhiteSpace(context.OwnerModId))
            {
                return null;
            }

            // WHY a connection the mod's context owns: the context disconnects every connection it tracks when the
            // mod unloads, which is exactly the moment the switch must be given back.
            RbxScriptSignal lifetime = new("TextChatConfigurations.WriterLifetime");
            lifetime.BindScheduler(context.Bindings.Scheduler);
            RbxScriptConnection connection = lifetime.Connect((Action<object[]>)(_ => { }));
            context.TrackConnection(connection);
            return () => connection.Connected;
        }

        private static RbxTextChannel RequireChannel(LuaFunctionExecutionContext ctx, string method,
            out LuaCsRbxModContext context)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !(proxy.Instance is RbxTextChannel channel)
                || channel.Service == null)
            {
                throw RbxError.BadArgument(
                    "TextChannel:" + method + " must be called with a colon on a TextChannel",
                    "write TextChatService.TextChannels.RBXGeneral:" + method + "(text)");
            }

            context = proxy.Context;
            return channel;
        }

        private static string OptionalString(LuaFunctionExecutionContext ctx, int index, string what, int argumentNumber)
        {
            return Arg(ctx, index).Type == LuaValueType.Nil ? string.Empty : ReadString(ctx, index, what, argumentNumber);
        }
    }
}
