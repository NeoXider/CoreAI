using System;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// The context a script runs in (MVP4 design note, decisions SC-4 and SC-8). <see cref="Legacy"/> is a script
    /// whose source declares no context: it keeps the pre-MVP4 rules exactly (server side, no CONTEXT_VIOLATION
    /// checks). <see cref="Shared"/> is a ModuleScript, which has no side of its own and runs on the side of the
    /// script that requires it.
    /// </summary>
    public enum ScriptContext
    {
        Legacy = 0,
        Server = 1,
        Client = 2,
        Shared = 3
    }

    /// <summary>The three Roblox script classes (rulebook R1.1).</summary>
    public static class ScriptClassNames
    {
        public const string Script = "Script";
        public const string LocalScript = "LocalScript";
        public const string ModuleScript = "ModuleScript";

        /// <summary>True for <c>Script</c>, <c>LocalScript</c> and <c>ModuleScript</c>; class names are case-sensitive.</summary>
        public static bool IsScriptClass(string className)
        {
            return string.Equals(className, Script, StringComparison.Ordinal)
                   || string.Equals(className, LocalScript, StringComparison.Ordinal)
                   || string.Equals(className, ModuleScript, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The integer values of <c>Enum.RunContext</c> (mirror <c>reference/engine/enums/RunContext.yaml</c>; rulebook R1.4).
    /// WHY constants and not the Datatypes enum registry: the registry entry is step B1's, and this engine-free core
    /// must not wait for it; B1 registers the enum with these same values.
    /// </summary>
    public static class RunContextValues
    {
        public const int Legacy = 0;
        public const int Server = 1;
        public const int Client = 2;
        public const int Plugin = 3;

        /// <summary>True for the four values Roblox defines.</summary>
        public static bool IsDefined(int runContext)
        {
            return runContext >= Legacy && runContext <= Plugin;
        }

        /// <summary>The Roblox item name (<c>Legacy</c>, <c>Server</c>, <c>Client</c>, <c>Plugin</c>), or the number for an undefined value.</summary>
        public static string ToName(int runContext)
        {
            switch (runContext)
            {
                case Legacy: return "Legacy";
                case Server: return "Server";
                case Client: return "Client";
                case Plugin: return "Plugin";
                default: return runContext.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Engine-free rules for script contexts: parsing the <c>@coreai context:</c> header value, deriving the effective
    /// context from an instance's class and <c>RunContext</c> (decision SC-4), and answering which side a context runs on
    /// for a topology (decision SC-9 item 5).
    /// </summary>
    public static class ScriptContexts
    {
        /// <summary>
        /// Parses a declared context. Null, empty or whitespace is <see cref="ScriptContext.Legacy"/> (no declaration);
        /// <c>server</c>, <c>client</c> and <c>shared</c> are accepted case-insensitively. Any other value fails with an
        /// error naming it, which the load raises as BAD_ARGUMENT.
        /// </summary>
        public static bool TryParseDeclared(string text, out ScriptContext context, out string error)
        {
            context = ScriptContext.Legacy;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            string trimmed = text.Trim();
            if (string.Equals(trimmed, "server", StringComparison.OrdinalIgnoreCase))
            {
                context = ScriptContext.Server;
                return true;
            }

            if (string.Equals(trimmed, "client", StringComparison.OrdinalIgnoreCase))
            {
                context = ScriptContext.Client;
                return true;
            }

            if (string.Equals(trimmed, "shared", StringComparison.OrdinalIgnoreCase))
            {
                context = ScriptContext.Shared;
                return true;
            }

            error = "@coreai context '" + trimmed + "' is not server, client or shared";
            return false;
        }

        /// <summary>The header and manifest spelling: empty for <see cref="ScriptContext.Legacy"/>, else the lower-case name.</summary>
        public static string ToDeclaredName(ScriptContext context)
        {
            switch (context)
            {
                case ScriptContext.Legacy: return string.Empty;
                case ScriptContext.Server: return "server";
                case ScriptContext.Client: return "client";
                case ScriptContext.Shared: return "shared";
                default: throw new ArgumentOutOfRangeException(nameof(context), context, null);
            }
        }

        /// <summary>The name used in messages: <c>legacy</c> for an undeclared context, else the declared name.</summary>
        public static string ToDisplayName(ScriptContext context)
        {
            return context == ScriptContext.Legacy ? "legacy" : ToDeclaredName(context);
        }

        /// <summary>
        /// The effective context of a script instance (decision SC-4). A declared context of
        /// <see cref="ScriptContext.Legacy"/> means "not declared": the instance's class and <c>RunContext</c> decide, as
        /// in Roblox. A declared context must agree with the instance; a disagreement, a <c>Plugin</c> run context, a
        /// <c>RunContext</c> on a <c>LocalScript</c> (R1.8) or an unknown class fails with an error the caller raises as
        /// BAD_ARGUMENT.
        /// </summary>
        public static bool TryGetEffective(string className, int runContext, ScriptContext declared,
            out ScriptContext effective, out string error)
        {
            effective = ScriptContext.Legacy;
            error = null;
            if (!ScriptClassNames.IsScriptClass(className))
            {
                error = "'" + (className ?? "nil") + "' is not a script class; expected Script, LocalScript or ModuleScript";
                return false;
            }

            if (string.Equals(className, ScriptClassNames.ModuleScript, StringComparison.Ordinal))
            {
                if (declared == ScriptContext.Legacy || declared == ScriptContext.Shared)
                {
                    effective = ScriptContext.Shared;
                    return true;
                }

                error = "a ModuleScript runs on the side of the script that requires it, so its source cannot declare "
                        + "context: " + ToDeclaredName(declared);
                return false;
            }

            if (!RunContextValues.IsDefined(runContext))
            {
                error = "RunContext " + RunContextValues.ToName(runContext) + " is not an Enum.RunContext value";
                return false;
            }

            if (runContext == RunContextValues.Plugin)
            {
                error = "RunContext Plugin runs a script inside a Studio plugin, which CoreAI does not have";
                return false;
            }

            if (string.Equals(className, ScriptClassNames.LocalScript, StringComparison.Ordinal))
            {
                if (runContext != RunContextValues.Legacy)
                {
                    error = "RunContext cannot be used from a LocalScript; a LocalScript always runs on the client";
                    return false;
                }

                if (declared == ScriptContext.Legacy || declared == ScriptContext.Client)
                {
                    effective = ScriptContext.Client;
                    return true;
                }

                error = "a LocalScript always runs on the client, but its source declares context: "
                        + ToDeclaredName(declared);
                return false;
            }

            ScriptContext fromInstance = FromScriptRunContext(runContext);
            if (declared == ScriptContext.Legacy)
            {
                effective = fromInstance;
                return true;
            }

            if (declared == ScriptContext.Shared)
            {
                error = "context: shared makes a ModuleScript, but this instance is a Script";
                return false;
            }

            if (declared == fromInstance
                || (declared == ScriptContext.Server && fromInstance == ScriptContext.Legacy))
            {
                // WHY: a declared server context may sit on a Legacy RunContext. An imported Legacy Script is a
                // server script by Roblox's rules (R1.5), so declaring it changes the checks, not where it runs.
                effective = declared;
                return true;
            }

            error = "a Script with RunContext " + RunContextValues.ToName(runContext) + " runs as "
                    + ToDisplayName(fromInstance) + ", but its source declares context: " + ToDeclaredName(declared);
            return false;
        }

        /// <summary>The class a new instance gets for a declared context (decision SC-6).</summary>
        public static string ClassNameFor(ScriptContext declared)
        {
            switch (declared)
            {
                case ScriptContext.Legacy:
                case ScriptContext.Server:
                case ScriptContext.Client:
                    return ScriptClassNames.Script;
                case ScriptContext.Shared:
                    return ScriptClassNames.ModuleScript;
                default:
                    throw new ArgumentOutOfRangeException(nameof(declared), declared, null);
            }
        }

        /// <summary>
        /// The <c>RunContext</c> a new <c>Script</c> gets for a declared context (decision SC-6). A ModuleScript has no
        /// run context, so <see cref="ScriptContext.Shared"/> is refused.
        /// </summary>
        public static int RunContextFor(ScriptContext declared)
        {
            switch (declared)
            {
                case ScriptContext.Legacy: return RunContextValues.Legacy;
                case ScriptContext.Server: return RunContextValues.Server;
                case ScriptContext.Client: return RunContextValues.Client;
                case ScriptContext.Shared:
                    throw new ArgumentOutOfRangeException(nameof(declared), declared,
                        "a ModuleScript has no RunContext");
                default:
                    throw new ArgumentOutOfRangeException(nameof(declared), declared, null);
            }
        }

        /// <summary>
        /// True for the server side (<see cref="ScriptContext.Server"/> and <see cref="ScriptContext.Legacy"/>), false
        /// for the client side. A module has no side of its own: pass the context of the script that requires it.
        /// </summary>
        public static bool RunsOnServerSide(ScriptContext effective)
        {
            switch (effective)
            {
                case ScriptContext.Legacy:
                case ScriptContext.Server:
                    return true;
                case ScriptContext.Client:
                    return false;
                case ScriptContext.Shared:
                    throw new ArgumentOutOfRangeException(nameof(effective), effective,
                        "a module runs on its requirer's side; pass the requiring script's context");
                default:
                    throw new ArgumentOutOfRangeException(nameof(effective), effective, null);
            }
        }

        /// <summary>
        /// Whether a process with this topology has the side the context runs on (decision SC-9 item 5): the server side
        /// exists in Solo, Host and DedicatedServer, the client side in Solo, Host and Client.
        /// </summary>
        public static bool IsSideAvailable(ScriptContext effective, RbxNetworkTopology topology)
        {
            bool serverSide = RunsOnServerSide(effective);
            switch (topology)
            {
                case RbxNetworkTopology.Solo:
                case RbxNetworkTopology.Host:
                    return true;
                case RbxNetworkTopology.DedicatedServer:
                    return serverSide;
                case RbxNetworkTopology.Client:
                    return !serverSide;
                default:
                    throw new ArgumentOutOfRangeException(nameof(topology), topology, null);
            }
        }

        /// <summary>
        /// True when two scripts run on the same side of the client-server boundary; a direct call between runs
        /// (<c>mods_call</c>, <c>mods_get</c>, <c>events_emit</c>) is allowed only then (decision SC-13).
        /// </summary>
        public static bool SameSide(ScriptContext first, ScriptContext second)
        {
            return RunsOnServerSide(first) == RunsOnServerSide(second);
        }

        private static ScriptContext FromScriptRunContext(int runContext)
        {
            switch (runContext)
            {
                case RunContextValues.Server: return ScriptContext.Server;
                case RunContextValues.Client: return ScriptContext.Client;
                default: return ScriptContext.Legacy;
            }
        }
    }
}
