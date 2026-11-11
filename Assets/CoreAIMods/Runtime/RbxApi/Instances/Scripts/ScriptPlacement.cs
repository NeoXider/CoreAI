using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>Where the script instance of a source lives (MVP4 step C1, rules R1 and R2).</summary>
    public enum ScriptPlacementKind
    {
        /// <summary>
        /// Rule R1: a source that declares neither <c>context:</c> nor <c>parent:</c> and has no bound instance keeps the
        /// pre-MVP4 placement, <c>ServerScriptService/&lt;id&gt; (Folder)/&lt;id&gt; (Script)</c>: mod-owned runtime
        /// infrastructure that every start builds again and no save keeps.
        /// </summary>
        Synthetic,

        /// <summary>
        /// Rule R2: a world-owned instance bound to the source (SC-1, SC-6), placed before the main chunk runs, kept by
        /// unload and reload, saved with the world and never charged to an instance quota.
        /// </summary>
        Durable
    }

    /// <summary>
    /// The facts <see cref="ScriptPlacementPlanner.TryPlan"/> decides from: the source's header directives, the context
    /// it ran or was stored with, its folder, and the instance already bound to it.
    /// WHY public fields: a parameter bag filled by one caller and read by one function, as <see cref="ScriptRunInputs"/>.
    /// </summary>
    public struct ScriptPlacementRequest
    {
        /// <summary>The store entry (mod id) being loaded.</summary>
        public string SourceId;

        /// <summary>The header's <c>context:</c> value as written; empty when the header has none.</summary>
        public string HeaderContext;

        /// <summary>The header's <c>parent:</c> value as written; empty when the header has none.</summary>
        public string HeaderParent;

        /// <summary>
        /// The context the source last ran with, or the one its stored manifest records, read leniently; empty for none.
        /// It decides only when neither the header nor a bound instance does (decision SC-5).
        /// </summary>
        public string FallbackContext;

        /// <summary>The mod's folder path (the manifest <c>Category</c> split on <c>/</c>); null or empty is the top level.</summary>
        public IReadOnlyList<string> FolderSegments;

        /// <summary>
        /// The folder the bound instance was placed under by default when it was last placed: the previous source's folder.
        /// Null means the same as <see cref="FolderSegments"/>.
        /// </summary>
        public IReadOnlyList<string> PreviousFolderSegments;

        /// <summary>A live world-owned instance is bound to the source.</summary>
        public bool HasBoundInstance;

        /// <summary>The bound instance's class; ignored without one.</summary>
        public string BoundClassName;

        /// <summary>The bound instance's <c>RunContext</c>; ignored without one and for a ModuleScript.</summary>
        public int BoundRunContext;
    }

    /// <summary>
    /// What one load does with the script instance of its source: keep the synthetic legacy placement, or place a durable
    /// bound instance (create it, or reuse the bound one and bring its <c>RunContext</c> and default placement up to date).
    /// </summary>
    public sealed class ScriptPlacementPlan
    {
        /// <summary>The plan of a source that keeps the pre-MVP4 synthetic placement (rule R1).</summary>
        public static readonly ScriptPlacementPlan Synthetic = new(ScriptPlacementKind.Synthetic, ScriptContext.Legacy,
            null, RunContextValues.Legacy, false, null, null, null);

        private ScriptPlacementPlan(ScriptPlacementKind kind, ScriptContext context, string className, int runContext,
            bool reusesBoundInstance, IReadOnlyList<string> parentPath, IReadOnlyList<string> defaultPath,
            IReadOnlyList<string> previousDefaultPath)
        {
            Kind = kind;
            Context = context;
            ClassName = className;
            RunContext = runContext;
            ReusesBoundInstance = reusesBoundInstance;
            ParentPath = parentPath;
            DefaultPath = defaultPath;
            PreviousDefaultPath = previousDefaultPath;
        }

        public ScriptPlacementKind Kind { get; }

        /// <summary>
        /// The context the load runs with: the declared one, or <see cref="ScriptContext.Legacy"/> for the synthetic
        /// placement and for a durable script whose source and instance declare none.
        /// </summary>
        public ScriptContext Context { get; }

        /// <summary>The bound instance's class (<c>Script</c>, <c>LocalScript</c>); null for the synthetic placement.</summary>
        public string ClassName { get; }

        /// <summary>The <c>RunContext</c> the bound <c>Script</c> gets; <c>Legacy</c> for every other class.</summary>
        public int RunContext { get; }

        /// <summary>True when the load keeps the instance already bound to its source; false when it creates one.</summary>
        public bool ReusesBoundInstance { get; }

        /// <summary>
        /// The header's <c>parent:</c> path as names from <c>game</c> (the first one a service), applied at every load
        /// (SC-6); null when the header has none and the instance goes to <see cref="DefaultPath"/>.
        /// </summary>
        public IReadOnlyList<string> ParentPath { get; }

        /// <summary>
        /// Without a <c>parent:</c>: the default container's class (SC-6) followed by the folder names a new instance goes
        /// under, created as world-owned Folders where missing; null with a <c>parent:</c> or for the synthetic placement.
        /// </summary>
        public IReadOnlyList<string> DefaultPath { get; }

        /// <summary>
        /// For a reused instance without a <c>parent:</c>: the default path of its current context and its previous
        /// folder. An instance still exactly there moves to <see cref="DefaultPath"/>; one a player or script moved
        /// elsewhere stays where it is. Null otherwise.
        /// </summary>
        public IReadOnlyList<string> PreviousDefaultPath { get; }

        internal static ScriptPlacementPlan Durable(ScriptContext context, string className, int runContext,
            bool reusesBoundInstance, IReadOnlyList<string> parentPath, IReadOnlyList<string> defaultPath,
            IReadOnlyList<string> previousDefaultPath)
        {
            return new ScriptPlacementPlan(ScriptPlacementKind.Durable, context, className, runContext,
                reusesBoundInstance, parentPath, defaultPath, previousDefaultPath);
        }
    }

    /// <summary>
    /// The engine-free placement rules of MVP4 step C1: which context a load runs with (SC-5: the header when it declares
    /// one, else the bound instance, else the context the source last ran or was stored with, else legacy), whether the
    /// source keeps the synthetic placement (R1) or gets a durable bound instance (R2), and where that instance goes
    /// (SC-6). The refusals are the load's: an unknown context value and a malformed <c>parent:</c> path are BAD_ARGUMENT;
    /// <c>context: shared</c> and a change between script classes are NOT_IMPLEMENTED until the steps that build them.
    /// </summary>
    public static class ScriptPlacementPlanner
    {
        /// <summary>The step that brings <c>require</c> and with it ModuleScript sources.</summary>
        public const string SharedContextStep = "MVP4 step C4";

        /// <summary>The step that replaces a bound instance whose class a new context changes (SC-4).</summary>
        public const string ClassChangeStep = "MVP4 step C1-6";

        /// <summary>
        /// Plans one load. Never touches a world: the caller supplies the bound instance's facts, and applies the plan.
        /// </summary>
        public static bool TryPlan(in ScriptPlacementRequest request, out ScriptPlacementPlan plan, out RbxError refusal)
        {
            plan = null;
            string sourceId = request.SourceId ?? string.Empty;
            if (!TryResolveContext(in request, out ScriptContext context, out refusal))
            {
                return false;
            }

            IReadOnlyList<string> parentPath = null;
            if (!string.IsNullOrWhiteSpace(request.HeaderParent)
                && !TryParseParentPath(request.HeaderParent, out parentPath, out string parentError))
            {
                refusal = RbxError.BadArgument("@coreai parent '" + request.HeaderParent.Trim() + "': " + parentError,
                    "write the path from game, for example parent: Workspace/Door or parent: @game/ServerScriptService");
                return false;
            }

            ScriptContext boundContext = ScriptContext.Legacy;
            if (request.HasBoundInstance)
            {
                ScriptContexts.TryGetEffective(request.BoundClassName, request.BoundRunContext, ScriptContext.Legacy,
                    out boundContext, out string _);
            }

            if (context == ScriptContext.Shared)
            {
                refusal = RbxError.NotImplemented(
                    "mod '" + sourceId + "' declares context: shared, a ModuleScript that runs when a script requires it; "
                    + "require", SharedContextStep,
                    "declare context: server or context: client for now, and share values through attributes or value "
                    + "objects");
                return false;
            }

            if (!request.HasBoundInstance)
            {
                if (context == ScriptContext.Legacy && parentPath == null)
                {
                    plan = ScriptPlacementPlan.Synthetic;
                    return true;
                }

                plan = ScriptPlacementPlan.Durable(context, ScriptContexts.ClassNameFor(context),
                    ScriptContexts.RunContextFor(context), false, parentPath,
                    parentPath == null ? DefaultPath(context, request.FolderSegments) : null, null);
                return true;
            }

            if (!ScriptContexts.TryGetEffective(request.BoundClassName, request.BoundRunContext, context,
                    out ScriptContext _, out string _)
                && !IsRunContextChange(request.BoundClassName, context))
            {
                refusal = RbxError.NotImplemented(
                    "replacing the " + request.BoundClassName + " bound to mod '" + sourceId + "' with "
                    + ScriptContexts.ClassNameFor(context) + " (context: " + ScriptContexts.ToDisplayName(context)
                    + ")", ClassChangeStep,
                    "keep the context the script has, or forget the mod and load it again with the new context");
                return false;
            }

            int runContext = TargetRunContext(request.BoundClassName, request.BoundRunContext, context);
            IReadOnlyList<string> defaultPath = null;
            IReadOnlyList<string> previousDefaultPath = null;
            if (parentPath == null)
            {
                defaultPath = DefaultPath(context, request.FolderSegments);
                previousDefaultPath = DefaultPath(boundContext,
                    request.PreviousFolderSegments ?? request.FolderSegments);
            }

            plan = ScriptPlacementPlan.Durable(context, request.BoundClassName, runContext, true, parentPath,
                defaultPath, previousDefaultPath);
            return true;
        }

        /// <summary>
        /// The context a load of the source runs with (SC-5): the header's when it declares one, else the bound
        /// instance's (its class and <c>RunContext</c>), else <see cref="ScriptPlacementRequest.FallbackContext"/>, else
        /// <see cref="ScriptContext.Legacy"/>. Refuses, as <see cref="TryPlan"/> does, a header value that is not a
        /// context and a bound instance that cannot run. A restore asks this before it decides whether to start a
        /// source at all (a ModuleScript source is not started as a script).
        /// </summary>
        public static bool TryResolveContext(in ScriptPlacementRequest request, out ScriptContext context,
            out RbxError refusal)
        {
            refusal = null;
            if (!ScriptContexts.TryParseDeclared(request.HeaderContext, out context, out string contextError))
            {
                refusal = RbxError.BadArgument(contextError,
                    "write context: server, context: client or context: shared in the @coreai header, or leave the key "
                    + "out to run as a legacy script");
                return false;
            }

            if (context != ScriptContext.Legacy)
            {
                return true;
            }

            if (!request.HasBoundInstance)
            {
                context = ParseLenient(request.FallbackContext);
                return true;
            }

            if (!ScriptContexts.TryGetEffective(request.BoundClassName, request.BoundRunContext, ScriptContext.Legacy,
                    out context, out string boundError))
            {
                refusal = RbxError.BadArgument("the script instance bound to mod '" + (request.SourceId ?? string.Empty)
                                               + "' cannot run: " + boundError,
                    "set its RunContext to Server or Client, or forget the mod and load it again");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses a <c>parent:</c> value (SC-6): a <c>/</c>-separated path from <c>game</c>, a leading <c>@game/</c>
        /// accepted, with the component rules of <c>@game/</c> require paths (<see cref="RequirePath"/>: no empty and no
        /// <c>.</c> component). A path written from the top cannot go up, so a <c>..</c> component is refused, and so is a
        /// path that names nothing.
        /// </summary>
        public static bool TryParseParentPath(string text, out IReadOnlyList<string> path, out string error)
        {
            path = null;
            error = null;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.StartsWith("@game/", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(6);
            }

            if (trimmed.Length == 0)
            {
                error = "the path names no instance; it starts below game with a service, such as Workspace";
                return false;
            }

            string[] components = trimmed.Split('/');
            List<string> names = new List<string>(components.Length);
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                if (component.Length == 0)
                {
                    error = "the path has an empty component";
                    return false;
                }

                if (component == ".")
                {
                    error = "the path has a '.' component";
                    return false;
                }

                if (component == "..")
                {
                    error = "the path has a '..' component; it is written from game down and cannot go up";
                    return false;
                }

                names.Add(component);
            }

            path = names.AsReadOnly();
            return true;
        }

        /// <summary>
        /// The default placement of a new instance with no <c>parent:</c> (SC-6): the class of its container service
        /// (<see cref="ScriptRunRules.DefaultContainerClass"/>) followed by the folder names.
        /// </summary>
        public static IReadOnlyList<string> DefaultPath(ScriptContext context, IReadOnlyList<string> folderSegments)
        {
            int folders = folderSegments?.Count ?? 0;
            List<string> path = new List<string>(1 + folders) { ScriptRunRules.DefaultContainerClass(context) };
            for (int index = 0; index < folders; index++)
            {
                string segment = folderSegments[index];
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    path.Add(segment);
                }
            }

            return path.AsReadOnly();
        }

        private static ScriptContext ParseLenient(string context)
        {
            return ScriptContexts.TryParseDeclared(context, out ScriptContext parsed, out string _)
                ? parsed
                : ScriptContext.Legacy;
        }

        /// <summary>
        /// True when a declared context the instance disagrees with only needs a new <c>RunContext</c>: a Script that
        /// becomes a server or client script keeps its class.
        /// </summary>
        private static bool IsRunContextChange(string boundClassName, ScriptContext context)
        {
            return string.Equals(boundClassName, ScriptClassNames.Script, StringComparison.Ordinal)
                   && (context == ScriptContext.Server || context == ScriptContext.Client);
        }

        /// <summary>
        /// The <c>RunContext</c> a reused instance gets: unchanged for a LocalScript, a ModuleScript or an undeclared
        /// context; for a Script, the declared context's value, except that a declared server context keeps a Legacy
        /// <c>RunContext</c> (an imported Legacy Script is a server script by R1.5, ScriptContexts.TryGetEffective).
        /// </summary>
        private static int TargetRunContext(string boundClassName, int boundRunContext, ScriptContext context)
        {
            if (!string.Equals(boundClassName, ScriptClassNames.Script, StringComparison.Ordinal)
                || context == ScriptContext.Legacy)
            {
                return string.Equals(boundClassName, ScriptClassNames.ModuleScript, StringComparison.Ordinal)
                    ? RunContextValues.Legacy
                    : boundRunContext;
            }

            if (context == ScriptContext.Server && boundRunContext == RunContextValues.Legacy)
            {
                return RunContextValues.Legacy;
            }

            return ScriptContexts.RunContextFor(context);
        }
    }
}
