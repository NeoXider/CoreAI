using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>One side of the client-server boundary.</summary>
    public enum ScriptSide
    {
        Server,
        Client
    }

    /// <summary>
    /// One side-only member (decision SC-13). <see cref="Member"/> is <c>Class.Member</c> for one member, a bare class
    /// name for the class and all its members (<c>UserInputService</c>), or a CoreAI global (<c>store_set</c>).
    /// </summary>
    public sealed class ScriptContextRule
    {
        /// <summary>The member key the binding site asks about.</summary>
        public string Member { get; }

        /// <summary>The only side the member may be used from.</summary>
        public ScriptSide Side { get; }

        /// <summary>Where the rule comes from (roadmap section and mirror page), shown in the error.</summary>
        public string RuleRef { get; }

        /// <summary>One actionable suggestion, present tense, shown after <c>| fix:</c>.</summary>
        public string Fix { get; }

        public ScriptContextRule(string member, ScriptSide side, string ruleRef, string fix)
        {
            if (string.IsNullOrWhiteSpace(member) || member.Trim() != member || member.IndexOf(' ') >= 0)
            {
                throw new ArgumentException("a rule member is a non-empty key without spaces", nameof(member));
            }

            int firstDot = member.IndexOf('.');
            if (firstDot == 0 || firstDot == member.Length - 1
                || (firstDot > 0 && member.IndexOf('.', firstDot + 1) >= 0))
            {
                throw new ArgumentException("a rule member is 'Class', 'Class.Member' or a global name", nameof(member));
            }

            if (side != ScriptSide.Server && side != ScriptSide.Client)
            {
                throw new ArgumentOutOfRangeException(nameof(side), side, null);
            }

            if (string.IsNullOrWhiteSpace(ruleRef))
            {
                throw new ArgumentException("every rule names where it comes from", nameof(ruleRef));
            }

            if (string.IsNullOrWhiteSpace(fix))
            {
                throw new ArgumentException("every rule carries a fix", nameof(fix));
            }

            Member = member;
            Side = side;
            RuleRef = ruleRef;
            Fix = fix;
        }
    }

    /// <summary>
    /// The side-only members of decision SC-13, as data. The composition builds <see cref="CreateDefault"/>, lets packages
    /// (P2 chat, P18 UxmlService) <see cref="Add"/> their rows, then <see cref="Freeze"/>s it before any binding is built;
    /// each binding site asks <see cref="Check"/> for the running script's context. Legacy scripts are never checked:
    /// they keep the pre-MVP4 rules (decision SC-8).
    /// </summary>
    public sealed class ScriptContextRuleTable
    {
        private const string ClientFix =
            "move this code into a client script (context: client) and send the result to the server with a RemoteEvent";

        private const string ServerFix =
            "move this code into a server script (context: server) and ask it from the client with a RemoteEvent "
            + "or RemoteFunction";

        private readonly Dictionary<string, ScriptContextRule> _byMember =
            new Dictionary<string, ScriptContextRule>(StringComparer.Ordinal);

        private readonly List<ScriptContextRule> _ordered = new List<ScriptContextRule>();

        /// <summary>True once <see cref="Freeze"/> ran; a frozen table refuses <see cref="Add"/>.</summary>
        public bool IsFrozen { get; private set; }

        /// <summary>Every rule, in the order it was added.</summary>
        public IReadOnlyList<ScriptContextRule> Rules => _ordered;

        /// <summary>
        /// The MVP4 rows: remotes (§3.2), client-only input, render and camera members, server-only containers and HTTP,
        /// and the CoreAI globals that hold authoritative state. Not frozen, so packages can add theirs.
        /// </summary>
        public static ScriptContextRuleTable CreateDefault()
        {
            ScriptContextRuleTable table = new ScriptContextRuleTable();
            string[] remoteEventClasses = { "RemoteEvent", "UnreliableRemoteEvent" };
            foreach (string remoteClass in remoteEventClasses)
            {
                string reference = "§3.2; " + remoteClass + ".yaml";
                table.Add(new ScriptContextRule(remoteClass + ".FireServer", ScriptSide.Client, reference,
                    "a server script reaches clients with FireClient or FireAllClients"));
                table.Add(new ScriptContextRule(remoteClass + ".OnClientEvent", ScriptSide.Client, reference,
                    "a server script listens to OnServerEvent; move this handler into a client script"));
                table.Add(new ScriptContextRule(remoteClass + ".OnServerEvent", ScriptSide.Server, reference,
                    "a client script listens to OnClientEvent; move this handler into a server script"));
                table.Add(new ScriptContextRule(remoteClass + ".FireClient", ScriptSide.Server, reference,
                    "a client script sends to the server with FireServer"));
                table.Add(new ScriptContextRule(remoteClass + ".FireAllClients", ScriptSide.Server, reference,
                    "a client script sends to the server with FireServer"));
            }

            const string remoteFunctionRef = "§3.2; RemoteFunction.yaml";
            table.Add(new ScriptContextRule("RemoteFunction.InvokeServer", ScriptSide.Client, remoteFunctionRef,
                "a server script asks a client with InvokeClient, or better fires a RemoteEvent"));
            table.Add(new ScriptContextRule("RemoteFunction.OnClientInvoke", ScriptSide.Client, remoteFunctionRef,
                "a server script answers with OnServerInvoke; move this callback into a client script"));
            table.Add(new ScriptContextRule("RemoteFunction.OnServerInvoke", ScriptSide.Server, remoteFunctionRef,
                "a client script answers with OnClientInvoke; move this callback into a server script"));
            table.Add(new ScriptContextRule("RemoteFunction.InvokeClient", ScriptSide.Server, remoteFunctionRef,
                "a client script asks the server with InvokeServer"));

            table.Add(new ScriptContextRule("UserInputService", ScriptSide.Client, "§3.2; UserInputService.yaml",
                ClientFix));
            const string renderRef = "§3.2; RunService.yaml";
            table.Add(new ScriptContextRule("RunService.PreRender", ScriptSide.Client, renderRef, ClientFix));
            table.Add(new ScriptContextRule("RunService.RenderStepped", ScriptSide.Client, renderRef, ClientFix));
            table.Add(new ScriptContextRule("RunService.BindToRenderStep", ScriptSide.Client, renderRef, ClientFix));
            table.Add(new ScriptContextRule("RunService.UnbindFromRenderStep", ScriptSide.Client, renderRef,
                ClientFix));
            const string cameraRef = "§3.2; Camera.yaml: the camera exists only where frames are drawn";
            table.Add(new ScriptContextRule("Camera", ScriptSide.Client, cameraRef, ClientFix));
            table.Add(new ScriptContextRule("camera_follow", ScriptSide.Client, cameraRef, ClientFix));
            table.Add(new ScriptContextRule("camera_set_cframe", ScriptSide.Client, cameraRef, ClientFix));

            const string containerFix =
                "put what clients need in ReplicatedStorage, or ask a server script for it with a RemoteFunction";
            table.Add(new ScriptContextRule("ServerStorage", ScriptSide.Server,
                "R2.1; RunService.yaml: a client never receives ServerStorage", containerFix));
            table.Add(new ScriptContextRule("ServerScriptService", ScriptSide.Server,
                "R2.1: a client never receives ServerScriptService", containerFix));
            const string httpRef = "HttpService.yaml: requests are made from servers";
            table.Add(new ScriptContextRule("HttpService.GetAsync", ScriptSide.Server, httpRef, ServerFix));
            table.Add(new ScriptContextRule("HttpService.PostAsync", ScriptSide.Server, httpRef, ServerFix));
            table.Add(new ScriptContextRule("HttpService.RequestAsync", ScriptSide.Server, httpRef, ServerFix));

            const string stateRef = "SC-13: authoritative state lives on the server";
            table.Add(new ScriptContextRule("store_set", ScriptSide.Server, stateRef, ServerFix));
            table.Add(new ScriptContextRule("store_get", ScriptSide.Server, stateRef, ServerFix));
            table.Add(new ScriptContextRule("logic_define", ScriptSide.Server, stateRef, ServerFix));
            table.Add(new ScriptContextRule("logic_reset", ScriptSide.Server, stateRef, ServerFix));
            return table;
        }

        /// <summary>Adds a rule. Throws when the table is frozen or the member already has a rule.</summary>
        public void Add(ScriptContextRule rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            if (IsFrozen)
            {
                throw new InvalidOperationException(
                    "the script context rule table is frozen; add rules at composition, before the bindings are built");
            }

            if (_byMember.ContainsKey(rule.Member))
            {
                throw new ArgumentException("'" + rule.Member + "' already has a context rule", nameof(rule));
            }

            _byMember.Add(rule.Member, rule);
            _ordered.Add(rule);
        }

        /// <summary>Makes the table read-only. Idempotent.</summary>
        public void Freeze()
        {
            IsFrozen = true;
        }

        /// <summary>
        /// Finds the rule for a member: the exact key first, then the class part before the first dot
        /// (<c>UserInputService.InputBegan</c> falls back to <c>UserInputService</c>).
        /// </summary>
        public bool TryGet(string member, out ScriptContextRule rule)
        {
            rule = null;
            if (string.IsNullOrEmpty(member))
            {
                return false;
            }

            if (_byMember.TryGetValue(member, out rule))
            {
                return true;
            }

            int dot = member.IndexOf('.');
            return dot > 0 && _byMember.TryGetValue(member.Substring(0, dot), out rule);
        }

        /// <summary>
        /// Null when the member may be used from this context, else the CONTEXT_VIOLATION to raise. A legacy context is
        /// never checked. Module code is checked with its requirer's context, so <see cref="ScriptContext.Shared"/> is a
        /// caller bug and throws.
        /// </summary>
        public RbxError Check(string member, ScriptContext context)
        {
            if (context == ScriptContext.Shared)
            {
                throw new ArgumentOutOfRangeException(nameof(context), context,
                    "module code is checked with the requiring script's context");
            }

            if (context == ScriptContext.Legacy || !TryGet(member, out ScriptContextRule rule))
            {
                return null;
            }

            ScriptSide actual = ScriptContexts.RunsOnServerSide(context) ? ScriptSide.Server : ScriptSide.Client;
            return actual == rule.Side ? null : Violation(member, rule, context);
        }

        /// <summary>
        /// The CONTEXT_VIOLATION for using <paramref name="member"/> from the wrong side, in the §5.2.7 format without the
        /// mod prefix, which the Lua boundary attaches: <c>CONTEXT_VIOLATION: UserInputService.InputBegan is client-only
        /// (§3.2; UserInputService.yaml) and this script runs as server | fix: ...</c>.
        /// </summary>
        public static RbxError Violation(string member, ScriptContextRule rule, ScriptContext actual)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            string side = rule.Side == ScriptSide.Client ? "client" : "server";
            string message = (string.IsNullOrEmpty(member) ? rule.Member : member) + " is " + side + "-only ("
                             + rule.RuleRef + ") and this script runs as " + ScriptContexts.ToDisplayName(actual);
            return new RbxError(RbxErrorCode.ContextViolation, message, rule.Fix);
        }
    }
}
