using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using CoreAI.Mods.Rbx.Instances.Scripts;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The world a joining client receives (MVP4 SC-16, step C5): the server's package filtered for one recipient, and
    /// the check a client runs on every payload before it stages it. A client payload never holds a server or legacy
    /// source, nor anything under <c>ServerScriptService</c> or <c>ServerStorage</c>, because a source that reached a
    /// client would be readable there.
    /// </summary>
    /// <remarks>
    /// WHY its own class beside <see cref="RbxWorldPackageSerializer"/>: the design note names the capture
    /// <c>RbxWorldPackageSerializer.CaptureForRecipient</c>; it filters a finished capture and needs nothing private of
    /// the serializer, so it stays out of the encoder's files.
    /// </remarks>
    public static class RbxClientJoinPayload
    {
        private const string ServerScriptServiceClass = "ServerScriptService";
        private const string ServerStorageClass = "ServerStorage";

        private static readonly FieldInfo[] SnapshotFields =
            typeof(InstanceSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance);

        /// <summary>
        /// The world as <paramref name="recipientActorId"/> may see it: every instance <paramref name="filter"/> shows that
        /// recipient (the default filter is Roblox's replication), under the DataModel's own services, which stay as empty
        /// shells. A visible script bound to a server or legacy source is kept unbound, and <c>Mods/</c> keeps only the
        /// client and shared sources bound to a visible instance. Per-instance state follows the instances; the capture's
        /// diagnostics stay on the server.
        /// </summary>
        public static RbxWorldPackagePayload CaptureForRecipient(RbxWorldPackageCaptureContext context,
            string recipientActorId, IReplicationFilter filter = null)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (string.IsNullOrWhiteSpace(recipientActorId))
            {
                throw new ArgumentException("A join payload is captured for one recipient actor.",
                    nameof(recipientActorId));
            }

            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(context);
            return FilterForRecipient(full, context.Registry, recipientActorId,
                filter ?? DefaultReplicationFilter.Instance);
        }

        /// <summary>
        /// Refuses a payload a client must never stage (SC-16 and the SC-9 amendment): a <c>Mods/</c> entry that declares
        /// no context (legacy) or <c>server</c>, a script bound to a legacy or server <c>Script</c>, or any instance under
        /// <c>ServerScriptService</c> or <c>ServerStorage</c>. Throws <see cref="RbxWorldPackageException"/> naming it.
        /// </summary>
        public static void RefuseServerContent(RbxWorldPackagePayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            IReadOnlyList<RbxWorldModSource> mods = payload.Mods ?? Array.Empty<RbxWorldModSource>();
            for (int index = 0; index < mods.Count; index++)
            {
                RbxWorldModSource mod = mods[index];
                ScriptContext context = DeclaredContextOf(mod);
                if (context == ScriptContext.Legacy || context == ScriptContext.Server)
                {
                    throw new RbxWorldPackageException(
                        "A client payload holds mod '" + (mod?.Manifest?.Id ?? "?") + "', a "
                        + (context == ScriptContext.Legacy ? "legacy (it declares no context)" : "server")
                        + " source; a client receives client and shared sources only.");
                }
            }

            List<InstanceSnapshot> nodes = payload.Tree?.Instances ?? new List<InstanceSnapshot>();
            Dictionary<ulong, InstanceSnapshot> byId = IndexById(nodes);
            for (int index = 0; index < nodes.Count; index++)
            {
                InstanceSnapshot node = nodes[index];
                if (node == null)
                {
                    continue;
                }

                string serverContainer = ServerOnlyAncestor(node, byId);
                if (serverContainer != null)
                {
                    throw new RbxWorldPackageException(
                        "A client payload holds " + node.ClassName + " '" + node.Name + "' under " + serverContainer
                        + ", which a client never receives.");
                }

                if (node.Script != null && !string.IsNullOrEmpty(node.Script.SourceId)
                    && string.Equals(node.ClassName, ScriptClassNames.Script, StringComparison.Ordinal)
                    && (node.Script.RunContext ?? RunContextValues.Legacy) != RunContextValues.Client)
                {
                    throw new RbxWorldPackageException(
                        "A client payload holds Script '" + node.Name + "' bound to source '" + node.Script.SourceId
                        + "' with RunContext " + RunContextValues.ToName(node.Script.RunContext ?? RunContextValues.Legacy)
                        + "; a client receives client and shared sources only.");
                }
            }
        }

        internal static RbxWorldPackagePayload FilterForRecipient(RbxWorldPackagePayload full, InstanceRegistry registry,
            string recipientActorId, IReplicationFilter filter)
        {
            List<InstanceSnapshot> nodes = full.Tree?.Instances ?? new List<InstanceSnapshot>();
            Dictionary<ulong, InstanceSnapshot> byId = IndexById(nodes);
            Dictionary<ulong, bool> kept = new();
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null)
                {
                    IsKept(nodes[index], byId, kept, registry, recipientActorId, filter);
                }
            }

            Dictionary<string, RbxWorldModSource> clientSources = new(StringComparer.Ordinal);
            IReadOnlyList<RbxWorldModSource> mods = full.Mods ?? Array.Empty<RbxWorldModSource>();
            for (int index = 0; index < mods.Count; index++)
            {
                RbxWorldModSource mod = mods[index];
                ScriptContext context = DeclaredContextOf(mod);
                if (mod?.Manifest != null && (context == ScriptContext.Client || context == ScriptContext.Shared))
                {
                    clientSources[mod.Manifest.Id ?? ""] = mod;
                }
            }

            InstanceTreeSnapshot tree = new() { WorldAclVersion = full.Tree?.WorldAclVersion };
            HashSet<string> boundSources = new(StringComparer.Ordinal);
            for (int index = 0; index < nodes.Count; index++)
            {
                InstanceSnapshot node = nodes[index];
                if (node == null || !kept.TryGetValue(node.Id, out bool keep) || !keep)
                {
                    continue;
                }

                InstanceSnapshot copy = node;
                if (node.Script != null && !string.IsNullOrEmpty(node.Script.SourceId))
                {
                    if (clientSources.ContainsKey(node.Script.SourceId))
                    {
                        boundSources.Add(node.Script.SourceId);
                    }
                    else
                    {
                        copy = Copy(node);
                        copy.Script = new ScriptSnapshot
                        {
                            SourceId = "",
                            Enabled = node.Script.Enabled,
                            RunContext = node.Script.RunContext
                        };
                    }
                }

                // WHY: a reference to an instance the recipient does not receive would dangle in its package; it reads as
                // nil there, which is what a Roblox client sees for an instance that never replicated to it.
                if (node.Model != null && node.Model.PrimaryPartId != 0 && !IsKeptId(node.Model.PrimaryPartId, kept))
                {
                    copy = ReferenceEquals(copy, node) ? Copy(node) : copy;
                    copy.Model = Copy(node.Model);
                    copy.Model.PrimaryPartId = 0;
                }

                if (node.Value != null && node.Value.ObjectTargetId != 0 && !IsKeptId(node.Value.ObjectTargetId, kept))
                {
                    copy = ReferenceEquals(copy, node) ? Copy(node) : copy;
                    copy.Value = Copy(node.Value);
                    copy.Value.ObjectTargetId = 0;
                }

                tree.Instances.Add(copy);
            }

            List<RbxWorldModSource> keptMods = new();
            for (int index = 0; index < mods.Count; index++)
            {
                RbxWorldModSource mod = mods[index];
                if (mod?.Manifest != null && boundSources.Contains(mod.Manifest.Id ?? ""))
                {
                    keptMods.Add(mod);
                }
            }

            RbxWorldPackageOptionalState optional = full.OptionalState;
            optional.PointLights = KeepKeys(full.PointLights, kept);
            optional.Gui = KeepKeys(full.Gui, kept);
            optional.TextChatCommands = KeepKeys(full.TextChatCommands, kept);
            optional.SelectionBoxes = KeepKeys(full.SelectionBoxes, kept);
            return new RbxWorldPackagePayload(full.CapturedAtUtc, full.Settings, tree, KeepKeys(full.Parts, kept),
                full.CameraCFrame, keptMods, Array.Empty<RbxWorldPackageDiagnostic>(), optional);
        }

        /// <summary>The context a package source declares: its header's, else its manifest's (SC-5).</summary>
        private static ScriptContext DeclaredContextOf(RbxWorldModSource mod)
        {
            if (mod?.Manifest == null)
            {
                return ScriptContext.Legacy;
            }

            LuaModHeader header = LuaModHeader.Parse(mod.Source ?? "", mod.Manifest.Id ?? "");
            string declared = string.IsNullOrWhiteSpace(header.Context) ? mod.Manifest.Context : header.Context;
            return ScriptContexts.TryParseDeclared(declared, out ScriptContext context, out string _)
                ? context
                : ScriptContext.Legacy;
        }

        private static bool IsKept(InstanceSnapshot node, Dictionary<ulong, InstanceSnapshot> byId,
            Dictionary<ulong, bool> kept, InstanceRegistry registry, string recipientActorId, IReplicationFilter filter)
        {
            if (kept.TryGetValue(node.Id, out bool known))
            {
                return known;
            }

            // WHY a provisional false: a malformed tree with a parent loop ends the walk instead of recursing forever.
            kept[node.Id] = false;
            bool keep;
            if (!byId.TryGetValue(node.ParentId, out InstanceSnapshot parent))
            {
                keep = true;
            }
            else if (!byId.ContainsKey(parent.ParentId))
            {
                // WHY: a service is a shell the client's own DataModel has too; what it holds is what is filtered.
                keep = IsKept(parent, byId, kept, registry, recipientActorId, filter);
            }
            else
            {
                keep = IsKept(parent, byId, kept, registry, recipientActorId, filter)
                       && registry != null
                       && registry.TryGetRecord(new InstanceId(node.Id), out InstanceRecord record)
                       && filter.IsVisibleTo(recipientActorId, record.Instance);
            }

            kept[node.Id] = keep;
            return keep;
        }

        private static bool IsKeptId(ulong id, Dictionary<ulong, bool> kept)
        {
            return kept.TryGetValue(id, out bool keep) && keep;
        }

        private static string ServerOnlyAncestor(InstanceSnapshot node, Dictionary<ulong, InstanceSnapshot> byId)
        {
            HashSet<ulong> seen = new();
            ulong parentId = node.ParentId;
            while (byId.TryGetValue(parentId, out InstanceSnapshot parent) && seen.Add(parentId))
            {
                if (string.Equals(parent.ClassName, ServerScriptServiceClass, StringComparison.Ordinal)
                    || string.Equals(parent.ClassName, ServerStorageClass, StringComparison.Ordinal))
                {
                    return parent.ClassName;
                }

                parentId = parent.ParentId;
            }

            return null;
        }

        private static Dictionary<ulong, InstanceSnapshot> IndexById(List<InstanceSnapshot> nodes)
        {
            Dictionary<ulong, InstanceSnapshot> byId = new();
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null)
                {
                    byId[nodes[index].Id] = nodes[index];
                }
            }

            return byId;
        }

        /// <summary>
        /// A copy of a snapshot object with every public field, so a filtered node never changes the capture it came from.
        /// </summary>
        /// <remarks>WHY reflection: a field a later step adds to the snapshot is carried without editing this list.</remarks>
        private static T Copy<T>(T source) where T : class, new()
        {
            T copy = new();
            FieldInfo[] fields = typeof(T) == typeof(InstanceSnapshot)
                ? SnapshotFields
                : typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int index = 0; index < fields.Length; index++)
            {
                fields[index].SetValue(copy, fields[index].GetValue(source));
            }

            return copy;
        }

        private static IReadOnlyDictionary<InstanceId, TValue> KeepKeys<TValue>(
            IReadOnlyDictionary<InstanceId, TValue> source, Dictionary<ulong, bool> kept)
        {
            Dictionary<InstanceId, TValue> result = new();
            if (source == null)
            {
                return result;
            }

            foreach (KeyValuePair<InstanceId, TValue> pair in source)
            {
                if (IsKeptId(pair.Key.Value, kept))
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }
    }
}
