using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Instances.Scripts
{
    /// <summary>
    /// The engine-free checks and stable errors of <c>require</c> (decisions SC-10 and SC-12): who may run a module's code,
    /// which side may reach it, and the messages the AI repairs from. Each check returns null when it passes, else the
    /// error to raise; the Lua boundary attaches the mod prefix.
    /// </summary>
    public static class ModuleRequirePolicy
    {
        /// <summary>
        /// A module runs with its requirer's authority, so its author must be trusted at least as much (SC-10 item 3): the
        /// requirer's own actor, or the host (a null or empty owner). Otherwise NOT_AUTHORITY, because a restricted actor
        /// could otherwise plant code in a module a host script requires.
        /// </summary>
        public static RbxError CheckTrust(string requirerOwnerActorId, string moduleOwnerActorId, string moduleId)
        {
            if (string.IsNullOrEmpty(moduleOwnerActorId)
                || string.Equals(requirerOwnerActorId ?? string.Empty, moduleOwnerActorId, StringComparison.Ordinal))
            {
                return null;
            }

            string requirer = string.IsNullOrEmpty(requirerOwnerActorId) ? "host" : requirerOwnerActorId;
            return new RbxError(RbxErrorCode.NotAuthority,
                "module '" + (moduleId ?? "?") + "' belongs to actor '" + moduleOwnerActorId + "'; a script of actor '"
                + requirer + "' cannot run its code",
                "require a module written by this script's author or by the host, or copy the code into this script");
        }

        /// <summary>
        /// A client script may not require a module stored where clients never receive it (SC-10 item 4, R2.1). In Solo it
        /// would work only because the registry is shared, which teaches coupling that breaks in host mode. The context is
        /// the requiring top-level script's; a module has none of its own.
        /// </summary>
        public static RbxError CheckLocation(ScriptContext requirerContext, ScriptContainer moduleContainer,
            string modulePath)
        {
            if (requirerContext == ScriptContext.Shared)
            {
                throw new ArgumentOutOfRangeException(nameof(requirerContext), requirerContext,
                    "pass the context of the top-level script that requires the module");
            }

            if (requirerContext != ScriptContext.Client || !ScriptContainers.IsServerOnly(moduleContainer))
            {
                return null;
            }

            return new RbxError(RbxErrorCode.ContextViolation,
                "module '" + (modulePath ?? "?") + "' is in " + moduleContainer
                + ", which a client never receives (R2.1), and this script runs as client",
                "move the module to ReplicatedStorage, or ask a server script for its result with a RemoteFunction");
        }

        /// <summary>
        /// CYCLIC_REQUIRE with the path (DEV-1). The message starts with Roblox's guide wording
        /// (<c>scripting/module.md</c>), which the class page contradicts (design note Q11).
        /// </summary>
        public static RbxError CycleError(IReadOnlyList<string> path)
        {
            if (path == null || path.Count == 0)
            {
                throw new ArgumentException("a cycle has a path", nameof(path));
            }

            return new RbxError(RbxErrorCode.CyclicRequire,
                "Requested module was required recursively: " + string.Join(" -> ", path),
                "move what both need into a third module that neither requires, or require one of them inside a function");
        }

        /// <summary>A later require of a module that raised while loading in this environment (R3.8).</summary>
        public static RbxError PreviouslyFailedError(string modulePath, string firstError)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "Requested module experienced an error while loading: " + (modulePath ?? "?") + ": "
                + (firstError ?? "unknown error"),
                "fix the module's error; it loads again after its source changes or this script restarts");
        }

        /// <summary>A module must return exactly one value, and not nil (R3.1, <c>scripting/module.md</c>).</summary>
        public static RbxError ReturnCountError(string modulePath, int count)
        {
            if (count == 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    "one value is valid; use ReturnedNilError for a nil value");
            }

            return new RbxError(RbxErrorCode.BadArgument,
                "module '" + (modulePath ?? "?") + "' returned " + count.ToString(CultureInfo.InvariantCulture)
                + " values; a module returns exactly one",
                "end the module with one return of a table or function");
        }

        /// <summary>A module that returned nil (R3.1).</summary>
        public static RbxError ReturnedNilError(string modulePath)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "module '" + (modulePath ?? "?") + "' returned nil; a module returns exactly one non-nil value",
                "end the module with one return of a table or function");
        }

        /// <summary><c>require(assetId)</c> loads a published model, which CoreAI does not have.</summary>
        public static RbxError AssetIdError()
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "require(assetId) loads a published model, which CoreAI does not have",
                "put the module in the world and require the instance");
        }

        /// <summary>An argument that is neither a ModuleScript nor a string path.</summary>
        public static RbxError ArgumentTypeError(string typeName)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "require expects a ModuleScript or a path string, got " + (typeName ?? "nil"),
                "pass the ModuleScript instance or a path such as \"./Module\"");
        }

        /// <summary>An instance that is not a ModuleScript (a path can reach any instance).</summary>
        public static RbxError NotAModuleScriptError(string path, string className)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "'" + (path ?? "?") + "' is a " + (className ?? "nil") + ", not a ModuleScript",
                "require a ModuleScript");
        }

        /// <summary>A ModuleScript with no source: made with Instance.new or cloned (decision SC-17).</summary>
        public static RbxError NoSourceError(string modulePath)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "ModuleScript '" + (modulePath ?? "?") + "' has no source",
                "give it source from the console, or require the ModuleScript the module source is bound to");
        }

        /// <summary>A ModuleScript whose store entry is unloaded.</summary>
        public static RbxError UnloadedError(string modulePath)
        {
            return new RbxError(RbxErrorCode.BadArgument,
                "ModuleScript '" + (modulePath ?? "?") + "' is unloaded",
                "load the module's source before requiring it");
        }

        /// <summary>A path that does not parse or does not reach an instance (R3.5, R3.6).</summary>
        public static RbxError PathError(string detail)
        {
            return new RbxError(RbxErrorCode.BadArgument, detail ?? "require path is invalid",
                "check the path, or wait for the module with WaitForChild and require the instance");
        }
    }
}
