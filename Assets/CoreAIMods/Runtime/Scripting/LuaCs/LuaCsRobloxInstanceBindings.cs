using System;
using System.Collections.Generic;
using CoreAI.RobloxApi.Instances;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRobloxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Per-registration mod context for the Roblox Lua surface: capability tier, ownership
    /// attribution (owner mod id + origin tag for the instance ledger), the proxy cache that keeps
    /// one Lua identity per <see cref="RbxInstance"/>, and per-mod once-only diagnostics flags.
    /// </summary>
    internal sealed class LuaCsRobloxModContext
    {
        private readonly Dictionary<RbxInstance, LuaValue> _proxyCache = new();
        private readonly LuaTable _instanceMeta;

        public LuaCsRobloxModContext(LuaCsRobloxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag)
        {
            Bindings = bindings;
            Capabilities = capabilities;
            OwnerModId = ownerModId;
            OriginTag = originTag;
            _instanceMeta = LuaCsRobloxInstanceBindings.BuildInstanceMeta(this);
        }

        public LuaCsRobloxApiBindings Bindings { get; }

        public LuaCapabilities Capabilities { get; }

        /// <summary>Teardown owner recorded on created instances; null for one-off consoles.</summary>
        public string OwnerModId { get; }

        /// <summary>Ledger origin recorded on created instances (mod:&lt;id&gt; / console:&lt;n&gt;).</summary>
        public string OriginTag { get; }

        /// <summary>Deprecation note for Instance.new(className, parent) fires once per mod.</summary>
        public bool HasLoggedInstanceNewParentDeprecation { get; set; }

        /// <summary>DEV-5 task.synchronize/desynchronize no-op note fires once per mod.</summary>
        public bool HasLoggedParallelNoOp { get; set; }

        public bool CanWorldEdit => (Capabilities & LuaCapabilities.WorldEdit) != 0;

        /// <summary>
        /// Wraps an instance keeping one proxy per instance so Lua <c>==</c> and table keys behave
        /// like Roblox reference identity.
        /// TODO: MVP5 — prune destroyed entries during the hot-reload teardown sweep.
        /// </summary>
        public LuaValue WrapInstance(RbxInstance instance)
        {
            if (instance == null)
            {
                return LuaValue.Nil;
            }

            if (_proxyCache.TryGetValue(instance, out LuaValue cached))
            {
                return cached;
            }

            LuaValue proxy = new LuaValue(new LuaCsRobloxInstanceProxy(instance, this, _instanceMeta));
            _proxyCache[instance] = proxy;
            return proxy;
        }

        public void RequireWorldEdit(string what)
        {
            if (!CanWorldEdit)
            {
                throw RbxError.BadArgument(
                    what + " requires the WorldEdit capability, which was not granted to this script",
                    "grant the mod the WorldEdit capability or remove the instance mutation");
            }
        }
    }

    /// <summary>
    /// Lua member dispatch for <see cref="RbxInstance"/> proxies (roadmap §5.1.3): properties,
    /// navigation, lifecycle, attributes, tags, child-by-name sugar, ServiceProvider members on
    /// the DataModel, and loud stubs for the surface that lands in later slices (BasePart spatial
    /// properties → Unity binder task; signals/WaitForChild yield → MVP2). Destroyed instances
    /// follow DEV-7 at the Lua boundary: every member access raises INSTANCE_DESTROYED (the
    /// tombstone exception applies only inside destruction-queued handlers, which arrive with the
    /// MVP2 signal system).
    /// </summary>
    internal static class LuaCsRobloxInstanceBindings
    {
        /// <summary>BasePart properties whose materialization needs the Unity binder slice.</summary>
        private static readonly HashSet<string> BasePartSpatialProperties = new(StringComparer.Ordinal)
        {
            "Position", "Size", "CFrame", "Color", "Transparency", "Anchored", "CanCollide",
            "Shape", "Material", "Orientation", "Rotation"
        };

        public static LuaTable BuildInstanceMeta(LuaCsRobloxModContext context)
        {
            Dictionary<string, LuaValue> methods = BuildMethods(context);

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Instance.__index", ctx =>
            {
                RbxInstance self = Self(ctx, context);
                string key = ReadString(ctx, 1, "Instance member access");
                ThrowIfDestroyedForLua(self, key);

                switch (key)
                {
                    case "Name": return self.Name;
                    case "ClassName": return self.ClassName;
                    case "Parent": return context.WrapInstance(self.Parent);
                    case "Archivable": return self.Archivable;
                    case "ChildAdded": return LuaCsRobloxDatatypeBindings.Wrap(self.ChildAdded);
                    case "ChildRemoved": return LuaCsRobloxDatatypeBindings.Wrap(self.ChildRemoved);
                    case "DescendantAdded":
                        return LuaCsRobloxDatatypeBindings.Wrap(self.DescendantAdded);
                    case "DescendantRemoving":
                        return LuaCsRobloxDatatypeBindings.Wrap(self.DescendantRemoving);
                    case "Destroying": return LuaCsRobloxDatatypeBindings.Wrap(self.Destroying);
                    case "AncestryChanged":
                        return LuaCsRobloxDatatypeBindings.Wrap(self.AncestryChanged);
                    case "AttributeChanged":
                        return LuaCsRobloxDatatypeBindings.Wrap(self.AttributeChanged);
                }

                if (methods.TryGetValue(key, out LuaValue method))
                {
                    return method;
                }

                if (self.IsA("BasePart") && BasePartSpatialProperties.Contains(key))
                {
                    throw SpatialStub(key);
                }

                RbxInstance child = self.FindFirstChild(key);
                if (child != null)
                {
                    return context.WrapInstance(child);
                }

                throw RbxError.BadArgument(
                    key + " is not a valid member of " + self.ClassName + " \"" + self.GetFullName() + "\"",
                    "use FindFirstChild(\"" + key + "\") for children that may not exist yet");
            });

            meta[Metamethods.NewIndex] = Fn("Instance.__newindex", ctx =>
            {
                RbxInstance self = Self(ctx, context);
                string key = ReadString(ctx, 1, "Instance member assignment");
                LuaValue value = Arg(ctx, 2);

                switch (key)
                {
                    case "Name":
                        ThrowIfDestroyedForLua(self, key);
                        context.RequireWorldEdit("setting Instance.Name");
                        self.Name = ReadString(ctx, 2, "Instance.Name assignment");
                        return LuaValue.Nil;
                    case "Parent":
                        // WHY: no destroyed pre-check here — the Domain setter raises the exact
                        // D6 PARENT_LOCKED message for destroyed instances.
                        context.RequireWorldEdit("setting Instance.Parent");
                        self.Parent = ReadOptionalInstance(value, "Instance.Parent assignment");
                        return LuaValue.Nil;
                    case "Archivable":
                        ThrowIfDestroyedForLua(self, key);
                        context.RequireWorldEdit("setting Instance.Archivable");
                        self.Archivable = value.ToBoolean();
                        return LuaValue.Nil;
                }

                ThrowIfDestroyedForLua(self, key);
                if (self.IsA("BasePart") && BasePartSpatialProperties.Contains(key))
                {
                    context.RequireWorldEdit("setting " + self.ClassName + "." + key);
                    throw SpatialStub(key);
                }

                throw RbxError.BadArgument(
                    key + " is not a valid member of " + self.ClassName + " \"" + self.GetFullName() + "\"",
                    "set a writable Instance property (Name, Parent, Archivable) or use SetAttribute");
            });

            meta[Metamethods.Eq] = Fn("Instance.__eq", ctx =>
                TryGetInstance(Arg(ctx, 0), out LuaCsRobloxInstanceProxy a)
                && TryGetInstance(Arg(ctx, 1), out LuaCsRobloxInstanceProxy b)
                && ReferenceEquals(a.Instance, b.Instance));

            meta[Metamethods.ToString] = Fn("Instance.__tostring", ctx => Self(ctx, context).Name);
            return Lock(meta);
        }

        private static Dictionary<string, LuaValue> BuildMethods(LuaCsRobloxModContext context)
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal);

            void Method(string name, Func<LuaFunctionExecutionContext, RbxInstance, LuaValue> body)
            {
                methods[name] = new LuaValue(Fn("Instance." + name, ctx =>
                {
                    RbxInstance self = Self(ctx, context);
                    ThrowIfDestroyedForLua(self, name);
                    return body(ctx, self);
                }));
            }

            // ---- Navigation ----
            Method("FindFirstChild", (ctx, self) => context.WrapInstance(self.FindFirstChild(
                ReadString(ctx, 1, "FindFirstChild"), Arg(ctx, 2).ToBoolean())));
            Method("FindFirstChildOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstChildOfClass(ReadString(ctx, 1, "FindFirstChildOfClass"))));
            Method("FindFirstChildWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstChildWhichIsA(
                    ReadString(ctx, 1, "FindFirstChildWhichIsA"), Arg(ctx, 2).ToBoolean())));
            Method("FindFirstAncestor", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestor(ReadString(ctx, 1, "FindFirstAncestor"))));
            Method("FindFirstAncestorOfClass", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorOfClass(ReadString(ctx, 1, "FindFirstAncestorOfClass"))));
            Method("FindFirstAncestorWhichIsA", (ctx, self) => context.WrapInstance(
                self.FindFirstAncestorWhichIsA(ReadString(ctx, 1, "FindFirstAncestorWhichIsA"))));
            Method("GetChildren", (_, self) => WrapList(context, self.GetChildren()));
            Method("GetDescendants", (_, self) => WrapList(context, self.GetDescendants()));
            Method("IsA", (ctx, self) => self.IsA(ReadString(ctx, 1, "IsA")));
            Method("IsDescendantOf", (ctx, self) => self.IsDescendantOf(
                ReadOptionalInstance(Arg(ctx, 1), "IsDescendantOf")));
            Method("IsAncestorOf", (ctx, self) => self.IsAncestorOf(
                ReadOptionalInstance(Arg(ctx, 1), "IsAncestorOf")));
            Method("GetFullName", (_, self) => self.GetFullName());
            Method("WaitForChild", (ctx, self) =>
            {
                string childName = ReadString(ctx, 1, "WaitForChild");
                RbxInstance child = self.FindFirstChild(childName);
                if (child != null)
                {
                    return context.WrapInstance(child);
                }

                // TODO: MVP2 — scheduler yield
                throw RbxError.NotImplemented(
                    self.ClassName + ":WaitForChild(\"" + childName + "\") with the child absent",
                    "MVP2",
                    "the yield lands in MVP2; create the child first or check with FindFirstChild");
            });

            // ---- Lifecycle ----
            Method("Clone", (_, self) =>
            {
                context.RequireWorldEdit("Instance:Clone");
                return context.WrapInstance(self.Clone());
            });
            Method("Destroy", (_, self) =>
            {
                context.RequireWorldEdit("Instance:Destroy");
                self.Destroy();
                return LuaValue.Nil;
            });
            Method("ClearAllChildren", (_, self) =>
            {
                context.RequireWorldEdit("Instance:ClearAllChildren");
                self.ClearAllChildren();
                return LuaValue.Nil;
            });

            // ---- Attributes / tags ----
            Method("GetAttribute", (ctx, self) => AttributeToLua(
                self.GetAttribute(ReadString(ctx, 1, "GetAttribute"))));
            Method("SetAttribute", (ctx, self) =>
            {
                context.RequireWorldEdit("Instance:SetAttribute");
                self.SetAttribute(
                    ReadString(ctx, 1, "SetAttribute"), AttributeFromLua(Arg(ctx, 2)));
                return LuaValue.Nil;
            });
            Method("GetAttributes", (_, self) =>
            {
                LuaTable table = new();
                foreach (KeyValuePair<string, object> pair in self.GetAttributes())
                {
                    table[pair.Key] = AttributeToLua(pair.Value);
                }

                return new LuaValue(table);
            });
            Method("AddTag", (ctx, self) =>
            {
                context.RequireWorldEdit("Instance:AddTag");
                self.AddTag(ReadString(ctx, 1, "AddTag"));
                return LuaValue.Nil;
            });
            Method("RemoveTag", (ctx, self) =>
            {
                context.RequireWorldEdit("Instance:RemoveTag");
                self.RemoveTag(ReadString(ctx, 1, "RemoveTag"));
                return LuaValue.Nil;
            });
            Method("HasTag", (ctx, self) => self.HasTag(ReadString(ctx, 1, "HasTag")));
            Method("GetTags", (_, self) =>
            {
                LuaTable table = new();
                int index = 1;
                foreach (string tag in self.GetTags())
                {
                    table[index++] = tag;
                }

                return new LuaValue(table);
            });
            Method("GetAttributeChangedSignal", (ctx, self) => LuaCsRobloxDatatypeBindings.Wrap(
                self.GetAttributeChangedSignal(ReadString(ctx, 1, "GetAttributeChangedSignal"))));
            Method("GetPropertyChangedSignal", (ctx, self) => LuaCsRobloxDatatypeBindings.Wrap(
                self.GetPropertyChangedSignal(ReadString(ctx, 1, "GetPropertyChangedSignal"))));

            // ---- ServiceProvider (DataModel) ----
            Method("GetService", (ctx, self) => context.WrapInstance(
                RequireDataModel(self, "GetService").GetService(ReadString(ctx, 1, "GetService"))));
            Method("FindService", (ctx, self) => context.WrapInstance(
                RequireDataModel(self, "FindService").FindService(ReadString(ctx, 1, "FindService"))));
            Method("BindToClose", (ctx, self) =>
            {
                RequireDataModel(self, "BindToClose").BindToClose(null);
                return LuaValue.Nil;
            });

            // ---- Model pivot (Unity binder slice) ----
            // TODO: MVP1 binder task — Model pivot rides the GameObject materialization slice.
            Method("PivotTo", (_, self) => throw RbxError.NotImplemented(
                self.ClassName + ":PivotTo", "the Unity instance-binder slice (MVP1 task 7)",
                "spatial state lands with the GameObject binder; keep layout data in attributes until then"));
            Method("GetPivot", (_, self) => throw RbxError.NotImplemented(
                self.ClassName + ":GetPivot", "the Unity instance-binder slice (MVP1 task 7)",
                "spatial state lands with the GameObject binder; keep layout data in attributes until then"));

            return methods;
        }

        private static RbxInstance Self(LuaFunctionExecutionContext ctx, LuaCsRobloxModContext context)
        {
            if (TryGetInstance(Arg(ctx, 0), out LuaCsRobloxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                "Instance member access expects an Instance as self",
                "call instance methods with a colon, e.g. workspace:FindFirstChild(\"Part\")");
        }

        /// <summary>DEV-7 at the Lua boundary: destroyed instances read as errors, not tombstones.</summary>
        private static void ThrowIfDestroyedForLua(RbxInstance instance, string memberName)
        {
            if (instance.IsDestroyed)
            {
                throw RbxError.InstanceDestroyed(memberName, instance.Name, instance.Id);
            }
        }

        private static RbxDataModel RequireDataModel(RbxInstance instance, string member)
        {
            if (instance is RbxDataModel dataModel)
            {
                return dataModel;
            }

            throw RbxError.BadArgument(
                member + " is not a valid member of " + instance.ClassName
                + " \"" + instance.GetFullName() + "\"",
                "call " + member + " on the game DataModel, e.g. game:" + member + "(...)");
        }

        private static RbxInstance ReadOptionalInstance(LuaValue value, string what)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (TryGetInstance(value, out LuaCsRobloxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                what + " expects an Instance or nil",
                "pass an Instance, got " + Describe(value));
        }

        private static LuaValue WrapList(LuaCsRobloxModContext context,
            IReadOnlyList<RbxInstance> instances)
        {
            LuaTable table = new();
            for (int i = 0; i < instances.Count; i++)
            {
                table[i + 1] = context.WrapInstance(instances[i]);
            }

            return new LuaValue(table);
        }

        private static LuaValue AttributeToLua(object value)
        {
            switch (value)
            {
                case null: return LuaValue.Nil;
                case string s: return s;
                case bool b: return b;
                case double d: return d;
                default: return LuaValue.Nil;
            }
        }

        private static object AttributeFromLua(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil: return null;
                case LuaValueType.Boolean: return value.Read<bool>();
                case LuaValueType.Number: return value.Read<double>();
                case LuaValueType.String: return value.Read<string>();
                default:
                    // WHY: Roblox parity — tables/functions/userdata are rejected by the attribute
                    // contract; naming the Lua-side type keeps the BAD_ARGUMENT fix actionable.
                    throw RbxError.BadArgument(
                        "attribute value of type " + Describe(value) + " is not supported in MVP1",
                        "pass a string, boolean, or number at argument 2");
            }
        }

        private static RbxError SpatialStub(string property)
        {
            // TODO: MVP1 task 7 — InstanceGameObjectBinder materializes BasePart spatial state.
            return RbxError.NotImplemented(
                "BasePart." + property + " materialization",
                "the Unity instance-binder slice (MVP1 task 7)",
                "spatial properties land with the GameObject binder; stage layout in attributes until then");
        }
    }
}
