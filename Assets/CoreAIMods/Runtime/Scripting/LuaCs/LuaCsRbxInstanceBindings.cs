using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Per-registration mod context for the Roblox Lua surface: capability tier, ownership
    /// attribution (owner mod id + origin tag for the instance ledger), the proxy cache that keeps
    /// one Lua identity per <see cref="RbxInstance"/>, and per-mod once-only diagnostics flags.
    /// </summary>
    internal sealed class LuaCsRbxModContext
    {
        private readonly Dictionary<RbxInstance, LuaValue> _proxyCache = new();
        private readonly LuaTable _instanceMeta;

        public LuaCsRbxModContext(LuaCsRbxApiBindings bindings, LuaCapabilities capabilities,
            string ownerModId, string originTag)
        {
            Bindings = bindings;
            Capabilities = capabilities;
            OwnerModId = ownerModId;
            OriginTag = originTag;
            // WHY: stamp this load's connection generation BEFORE the mod chunk runs, so every Connect
            // the chunk makes is tracked under it. On reload a fresh context bumps the generation first
            // (BuildMod runs before the reload teardown), letting teardown disconnect only the previous
            // generation and keep this chunk's connections. Mirrors the logic-slot keepState exclusion.
            ConnectionGeneration = bindings.Connections?.BeginGeneration(ownerModId) ?? 0;
            _instanceMeta = LuaCsRbxInstanceBindings.BuildInstanceMeta(this);
        }

        public LuaCsRbxApiBindings Bindings { get; }

        public LuaCapabilities Capabilities { get; }

        /// <summary>Teardown owner recorded on created instances; null for one-off consoles.</summary>
        public string OwnerModId { get; }

        /// <summary>Ledger origin recorded on created instances (mod:&lt;id&gt; / console:&lt;n&gt;).</summary>
        public string OriginTag { get; }

        /// <summary>This load's connection-ownership generation; connections opened by this context's
        /// chunk are tracked under it so a reload teardown keeps them and drops the prior generation.</summary>
        public int ConnectionGeneration { get; }

        /// <summary>Deprecation note for Instance.new(className, parent) fires once per mod.</summary>
        public bool HasLoggedInstanceNewParentDeprecation { get; set; }

        /// <summary>DEV-5 task.synchronize/desynchronize no-op note fires once per mod.</summary>
        public bool HasLoggedParallelNoOp { get; set; }

        public bool CanWorldEdit => (Capabilities & LuaCapabilities.WorldEdit) != 0;

        /// <summary>Sink storing BasePart spatial/appearance state in Roblox space (shared world).</summary>
        public IPartPropertySink PartSink => Bindings.PartSink;

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

            LuaValue proxy = new(new LuaCsRbxInstanceProxy(instance, this, _instanceMeta));
            _proxyCache[instance] = proxy;
            return proxy;
        }

        /// <summary>
        /// Records a signal connection this mod opened against the shared connection ledger so the
        /// composition disconnects it on teardown. No-op for the ownerless one-off surface (no mod id).
        /// </summary>
        public void TrackConnection(RbxScriptConnection connection)
        {
            Bindings.Connections?.Track(OwnerModId, ConnectionGeneration, connection);
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

        /// <summary>
        /// WorldEdit check for a property write, taking the class and member separately so the
        /// description string is built only when the check FAILS. Part property writes run per frame,
        /// and the eagerly-concatenated description was an allocation on every successful write.
        /// </summary>
        public void RequireWorldEditForWrite(string className, string member)
        {
            if (CanWorldEdit)
            {
                return;
            }

            RequireWorldEdit("setting " + className + "." + member);
        }
    }

    /// <summary>
    /// Lua member dispatch for <see cref="RbxInstance"/> proxies (roadmap §5.1.3): properties,
    /// navigation, lifecycle, attributes, tags, child-by-name sugar, ServiceProvider members on
    /// the DataModel, BasePart spatial/appearance properties over the part-property sink, and loud
    /// stubs for the surface that lands in later slices (signals/WaitForChild yield → MVP2).
    /// Destroyed instances follow DEV-7 at the Lua boundary: every member access raises
    /// INSTANCE_DESTROYED (the tombstone exception applies only inside destruction-queued handlers,
    /// which arrive with the MVP2 signal system).
    /// </summary>
    internal static class LuaCsRbxInstanceBindings
    {
        /// <summary>BasePart members still awaiting their own slice: Material needs the
        /// material catalog, Orientation/Rotation need Euler decomposition.</summary>
        private static readonly HashSet<string> UnwiredSpatialProperties = new(StringComparer.Ordinal)
        {
            "Material", "Orientation", "Rotation"
        };

        public static LuaTable BuildInstanceMeta(LuaCsRbxModContext context)
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
                    case "ChildAdded": return LuaCsRbxDatatypeBindings.Wrap(self.ChildAdded);
                    case "ChildRemoved": return LuaCsRbxDatatypeBindings.Wrap(self.ChildRemoved);
                    case "DescendantAdded":
                        return LuaCsRbxDatatypeBindings.Wrap(self.DescendantAdded);
                    case "DescendantRemoving":
                        return LuaCsRbxDatatypeBindings.Wrap(self.DescendantRemoving);
                    case "Destroying": return LuaCsRbxDatatypeBindings.Wrap(self.Destroying);
                    case "AncestryChanged":
                        return LuaCsRbxDatatypeBindings.Wrap(self.AncestryChanged);
                    case "AttributeChanged":
                        return LuaCsRbxDatatypeBindings.Wrap(self.AttributeChanged);
                }

                if (methods.TryGetValue(key, out LuaValue method))
                {
                    return method;
                }

                if (TryReadCamera(context, self, key, out LuaValue cameraValue))
                {
                    return cameraValue;
                }

                if (TryReadUserInput(context, self, key, out LuaValue inputValue))
                {
                    return inputValue;
                }

                if (TryReadRunService(context, self, key, out LuaValue runValue))
                {
                    return runValue;
                }

                if (TryReadClickDetector(context, self, key, out LuaValue clickValue))
                {
                    return clickValue;
                }

                if (TryReadSpatial(context, self, key, out LuaValue spatial))
                {
                    return spatial;
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
                        if (IsProtectedSingleton(self))
                        {
                            // WHY: a service's Parent is locked in Roblox — reparenting (or nil-ing)
                            // it would detach it so game:GetService stops resolving it for the world.
                            throw RbxError.BadArgument(
                                self.ClassName + ".Parent is locked — it is a shared singleton",
                                "services and workspace.CurrentCamera are fixed for the world's lifetime");
                        }

                        self.Parent = ReadOptionalInstance(value, "Instance.Parent assignment");
                        return LuaValue.Nil;
                    case "Archivable":
                        ThrowIfDestroyedForLua(self, key);
                        context.RequireWorldEdit("setting Instance.Archivable");
                        self.Archivable = value.ToBoolean();
                        return LuaValue.Nil;
                }

                ThrowIfDestroyedForLua(self, key);
                if (TryWriteCamera(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteUserInput(self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteClickDetector(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                if (TryWriteSpatial(context, self, key, value))
                {
                    return LuaValue.Nil;
                }

                throw RbxError.BadArgument(
                    key + " is not a valid member of " + self.ClassName + " \"" + self.GetFullName() + "\"",
                    "set a writable Instance property (Name, Parent, Archivable, or a BasePart " +
                    "spatial property like Position/Size/CFrame/Color) or use SetAttribute");
            });

            meta[Metamethods.Eq] = Fn("Instance.__eq", ctx =>
                TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy a)
                && TryGetInstance(Arg(ctx, 1), out LuaCsRbxInstanceProxy b)
                && ReferenceEquals(a.Instance, b.Instance));

            meta[Metamethods.ToString] = Fn("Instance.__tostring", ctx => Self(ctx, context).Name);
            return Lock(meta);
        }

        private static Dictionary<string, LuaValue> BuildMethods(LuaCsRbxModContext context)
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
                if (IsProtectedSingleton(self))
                {
                    // WHY: services and the canonical Camera are singletons — Roblox marks them
                    // non-archivable, so Clone yields nil rather than a second live instance.
                    return LuaValue.Nil;
                }

                RbxInstance copy = self.Clone();
                CopyPartSinkState(context.PartSink, self, copy);
                return context.WrapInstance(copy);
            });
            Method("Destroy", (_, self) =>
            {
                context.RequireWorldEdit("Instance:Destroy");
                if (IsProtectedSingleton(self))
                {
                    // WHY: destroying a shared service (cached once at composition, never re-resolved)
                    // would brick input/lighting/etc for EVERY mod in the world; the Camera is the
                    // canonical workspace.CurrentCamera. Roblox locks these against destruction too.
                    throw RbxError.BadArgument(
                        self.ClassName + " cannot be destroyed — it is a shared singleton",
                        "services and workspace.CurrentCamera live for the world's lifetime; "
                        + "never Destroy them");
                }

                self.Destroy();
                return LuaValue.Nil;
            });
            Method("ClearAllChildren", (_, self) =>
            {
                context.RequireWorldEdit("Instance:ClearAllChildren");
                // WHY: game:ClearAllChildren() must not wipe the world's services (Roblox locks
                // them). GetChildren returns a snapshot, so destroying non-protected children while
                // iterating is safe; protected singletons (services/Camera) are left intact.
                foreach (RbxInstance child in self.GetChildren())
                {
                    if (!IsProtectedSingleton(child))
                    {
                        child.Destroy();
                    }
                }

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
            Method("GetAttributeChangedSignal", (ctx, self) => LuaCsRbxDatatypeBindings.Wrap(
                self.GetAttributeChangedSignal(ReadString(ctx, 1, "GetAttributeChangedSignal"))));
            Method("GetPropertyChangedSignal", (ctx, self) => LuaCsRbxDatatypeBindings.Wrap(
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

            // ---- Model pivot ----
            // TODO: MVP2 — Model pivot aggregates child-part CFrames; single-part CFrame is wired.
            Method("PivotTo", (_, self) => throw RbxError.NotImplemented(
                self.ClassName + ":PivotTo", "the Model pivot follow-up",
                "set part.CFrame directly; Model-level pivot aggregation lands in MVP2"));
            Method("GetPivot", (_, self) => throw RbxError.NotImplemented(
                self.ClassName + ":GetPivot", "the Model pivot follow-up",
                "read part.CFrame directly; Model-level pivot aggregation lands in MVP2"));

            return methods;
        }

        private static RbxInstance Self(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            if (TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                "Instance member access expects an Instance as self",
                "call instance methods with a colon, e.g. workspace:FindFirstChild(\"Part\")");
        }

        /// <summary>DEV-7 at the Lua boundary: destroyed instances read as errors, not tombstones.</summary>
        // WHY: services (UserInputService/Lighting/Workspace/…) and the canonical Camera are
        // world-lifetime singletons; the lifecycle bindings refuse to Clone/Destroy them so one mod
        // cannot brick a shared service for every other mod.
        private static bool IsProtectedSingleton(RbxInstance instance)
        {
            return instance.IsService || instance.ClassName == "Camera";
        }

        private static void ThrowIfDestroyedForLua(RbxInstance instance, string memberName)
        {
            if (instance.IsDestroyed)
            {
                throw RbxError.InstanceDestroyed(memberName, instance.Name, instance.Id);
            }
        }

        // WHY: RbxInstance.Clone deep-copies identity/attributes/tags, but BasePart spatial and
        // appearance state lives in the external part sink keyed by id (D2 keeps RbxInstance
        // engine-free). A Roblox-faithful clone must carry that state to the copy's fresh id, so
        // walk source and copy in lockstep — Clone preserves archivable child order, so the trees
        // align — and copy each stored record across.
        // TODO: MVP2 — move this sink-copy into a registry-level clone seam so completeness no
        // longer depends on each Clone call site (the registry already owns the binder/sink).
        private static void CopyPartSinkState(IPartPropertySink sink, RbxInstance source,
            RbxInstance copy)
        {
            if (sink == null || source == null || copy == null)
            {
                return;
            }

            if (sink.TryGetPartProperties(source.Id, out PartProperties properties))
            {
                sink.SetPartProperties(copy.Id, in properties);
            }

            IReadOnlyList<RbxInstance> sourceChildren = source.GetChildren();
            IReadOnlyList<RbxInstance> copyChildren = copy.GetChildren();
            int copyIndex = 0;
            for (int i = 0; i < sourceChildren.Count && copyIndex < copyChildren.Count; i++)
            {
                // WHY: Clone drops Archivable == false subtrees, so a non-archivable source child
                // has no counterpart in the copy — advance only the source side past it.
                if (!sourceChildren[i].Archivable)
                {
                    continue;
                }

                CopyPartSinkState(sink, sourceChildren[i], copyChildren[copyIndex]);
                copyIndex++;
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

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance;
            }

            throw RbxError.BadArgument(
                what + " expects an Instance or nil",
                "pass an Instance, got " + Describe(value));
        }

        private static LuaValue WrapList(LuaCsRbxModContext context,
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
                case RbxVector3 v3: return LuaCsRbxDatatypeBindings.Wrap(v3);
                case RbxVector2 v2: return LuaCsRbxDatatypeBindings.Wrap(v2);
                case RbxColor3 c: return LuaCsRbxDatatypeBindings.Wrap(c);
                case RbxUDim u: return LuaCsRbxDatatypeBindings.Wrap(u);
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
                    // WHY: attributes accept the datatype subset the contract serializes; other
                    // userdata/tables/functions are rejected. Naming the Lua-side type and the exact
                    // supported list keeps the BAD_ARGUMENT fix actionable and Roblox-parity honest.
                    if (TryUnbox(value, out RbxVector3 v3))
                    {
                        return v3;
                    }

                    if (TryUnbox(value, out RbxVector2 v2))
                    {
                        return v2;
                    }

                    if (TryUnbox(value, out RbxColor3 c))
                    {
                        return c;
                    }

                    if (TryUnbox(value, out RbxUDim u))
                    {
                        return u;
                    }

                    throw RbxError.BadArgument(
                        "attribute value of type " + Describe(value) + " is not supported",
                        "pass a string, boolean, number, Vector3, Vector2, Color3, or UDim at argument 2");
            }
        }

        // ---- BasePart spatial/appearance (part-property sink) -------------------------------

        /// <summary>Reads a wired BasePart property from the sink as a Roblox-space datatype; throws
        /// the loud stub for still-unwired members (Material/Orientation/Rotation).</summary>
        private static bool TryReadSpatial(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!self.IsA("BasePart"))
            {
                value = LuaValue.Nil;
                return false;
            }

            PartProperties properties = context.PartSink.GetPartPropertiesOrDefault(self.Id);
            switch (key)
            {
                case "Shape":
                    value = WrapPartType(context, properties.Shape);
                    return true;
                case "Position":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Position);
                    return true;
                case "Size":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Size);
                    return true;
                case "CFrame":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.CFrame);
                    return true;
                case "Color":
                    value = LuaCsRbxDatatypeBindings.Wrap(properties.Color);
                    return true;
                case "Transparency":
                    value = properties.Transparency;
                    return true;
                case "Anchored":
                    value = properties.Anchored;
                    return true;
                case "CanCollide":
                    value = properties.CanCollide;
                    return true;
                default:
                    if (UnwiredSpatialProperties.Contains(key))
                    {
                        throw SpatialStub(key);
                    }

                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>Writes a wired BasePart property through the sink (Roblox Part semantics:
        /// setting Position keeps orientation, setting CFrame sets both).</summary>
        private static bool TryWriteSpatial(LuaCsRbxModContext context, RbxInstance self, string key,
            LuaValue value)
        {
            if (!self.IsA("BasePart"))
            {
                return false;
            }

            IPartPropertySink sink = context.PartSink;
            InstanceId id = self.Id;
            switch (key)
            {
                case "Shape":
                    context.RequireWorldEditForWrite(self.ClassName, "Shape");
                    sink.SetShape(id, ReadPartShapeValue(value));
                    return true;
                case "Position":
                    context.RequireWorldEditForWrite(self.ClassName, "Position");
                    sink.SetPosition(id, ReadVector3Value(value, "Part.Position assignment"));
                    return true;
                case "Size":
                    context.RequireWorldEditForWrite(self.ClassName, "Size");
                    sink.SetSize(id, ReadVector3Value(value, "Part.Size assignment"));
                    return true;
                case "CFrame":
                    context.RequireWorldEditForWrite(self.ClassName, "CFrame");
                    sink.SetCFrame(id, ReadCFrameValue(value, "Part.CFrame assignment"));
                    return true;
                case "Color":
                    context.RequireWorldEditForWrite(self.ClassName, "Color");
                    sink.SetColor(id, ReadColor3Value(value, "Part.Color assignment"));
                    return true;
                case "Transparency":
                    context.RequireWorldEditForWrite(self.ClassName, "Transparency");
                    sink.SetTransparency(id, ReadNumberValue(value, "Part.Transparency assignment"));
                    return true;
                case "Anchored":
                    context.RequireWorldEditForWrite(self.ClassName, "Anchored");
                    sink.SetAnchored(id, value.ToBoolean());
                    return true;
                case "CanCollide":
                    context.RequireWorldEditForWrite(self.ClassName, "CanCollide");
                    sink.SetCanCollide(id, value.ToBoolean());
                    return true;
                default:
                    if (UnwiredSpatialProperties.Contains(key))
                    {
                        context.RequireWorldEditForWrite(self.ClassName, key);
                        throw SpatialStub(key);
                    }

                    return false;
            }
        }

        /// <summary>Part.Shape as its interned Enum.PartType item (values match RbxPartShape).</summary>
        private static LuaValue WrapPartType(LuaCsRbxModContext context, RbxPartShape shape)
        {
            if (context.Bindings.Enums.TryGet("PartType", out RbxEnum partType)
                && partType.TryGetItem(shape.ToString(), out RbxEnumItem item))
            {
                return LuaCsRbxDatatypeBindings.Wrap(item);
            }

            return LuaValue.Nil;
        }

        private static RbxPartShape ReadPartShapeValue(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "PartType")
            {
                return (RbxPartShape)item.Value;
            }

            throw RbxError.BadArgument(
                "Part.Shape assignment expects an Enum.PartType item",
                "pass Enum.PartType.Block/Ball/Cylinder/Wedge/CornerWedge, got "
                + Describe(value));
        }

        // ---- UserInputService (input signals + poll surface over IInputSource) ---------------

        /// <summary>UserInputService members: the input signals, MouseBehavior, and the poll
        /// methods. All input READS are open at the Read tier (no capability gate) — observing
        /// input mutates nothing in the world.</summary>
        private static bool TryReadUserInput(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!(self is RbxUserInputService service))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "InputBegan":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputBegan, context);
                    return true;
                case "InputEnded":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputEnded, context);
                    return true;
                case "InputChanged":
                    value = LuaCsRbxDatatypeBindings.Wrap(service.InputChanged, context);
                    return true;
                case "MouseBehavior":
                    value = service.MouseBehavior != null
                        ? LuaCsRbxDatatypeBindings.Wrap(service.MouseBehavior)
                        : LuaValue.Nil;
                    return true;
                case "IsKeyDown":
                    value = GetUserInputMethods(service).IsKeyDown;
                    return true;
                case "GetKeysPressed":
                    value = GetUserInputMethods(service).GetKeysPressed;
                    return true;
                case "GetMouseLocation":
                    value = GetUserInputMethods(service).GetMouseLocation;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        // WHY: the poll methods close over `service` only (not the per-mod context) and the world
        // has one UserInputService, so their Lua wrappers are built once per service and shared —
        // the skill's flagship loop reads IsKeyDown several times per tick, and a fresh closure per
        // access would be a per-frame allocation. The weak table drops the cache when the service is.
        private static readonly ConditionalWeakTable<RbxUserInputService, UserInputMethods> InputMethodCache = new();

        private sealed class UserInputMethods
        {
            public LuaValue IsKeyDown;
            public LuaValue GetKeysPressed;
            public LuaValue GetMouseLocation;
        }

        private static UserInputMethods GetUserInputMethods(RbxUserInputService service)
        {
            return InputMethodCache.GetValue(service, s => new UserInputMethods
            {
                IsKeyDown = new LuaValue(Fn("UserInputService.IsKeyDown", ctx =>
                {
                    RbxEnumItem keyCode = ReadKeyCodeArg(ctx, 1, "UserInputService:IsKeyDown");
                    return s.IsKeyDown(keyCode.Value);
                })),
                GetKeysPressed = new LuaValue(Fn("UserInputService.GetKeysPressed", _ =>
                {
                    LuaTable list = new();
                    int index = 1;
                    foreach (RbxInputObject input in s.GetKeysPressed())
                    {
                        list[index++] = LuaCsRbxDatatypeBindings.Wrap(input);
                    }

                    return new LuaValue(list);
                })),
                GetMouseLocation = new LuaValue(Fn("UserInputService.GetMouseLocation",
                    _ => LuaCsRbxDatatypeBindings.Wrap(s.GetMouseLocation())))
            });
        }

        /// <summary>UserInputService.MouseBehavior assignment. Roblox lets any script set it, so
        /// no capability gate; MVP1 keeps it state-only.
        /// TODO: apply LockCenter/LockCurrentPosition to the host cursor with the pointer-lock
        /// slice.</summary>
        private static bool TryWriteUserInput(RbxInstance self, string key, LuaValue value)
        {
            if (!(self is RbxUserInputService service) || key != "MouseBehavior")
            {
                return false;
            }

            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "MouseBehavior")
            {
                service.MouseBehavior = item;
                return true;
            }

            throw RbxError.BadArgument(
                "UserInputService.MouseBehavior assignment expects an Enum.MouseBehavior item",
                "pass Enum.MouseBehavior.Default/LockCenter/LockCurrentPosition, got "
                + Describe(value));
        }

        private static RbxEnumItem ReadKeyCodeArg(LuaFunctionExecutionContext ctx, int index,
            string what)
        {
            if (TryUnbox(Arg(ctx, index), out RbxEnumItem item) && item.EnumType.Name == "KeyCode")
            {
                return item;
            }

            throw RbxError.BadArgument(
                what + " expects an Enum.KeyCode item at argument " + index,
                "pass e.g. Enum.KeyCode.Space, got " + Describe(Arg(ctx, index))
                                                     + " at argument " + index);
        }

        // ---- RunService (per-frame game-loop signals over the host Step pump) ----------------

        /// <summary>RunService members: the Heartbeat/Stepped/RenderStepped signals. Reads are open
        /// at the Read tier — connecting a per-frame handler observes the loop, it mutates nothing.</summary>
        private static bool TryReadRunService(LuaCsRbxModContext context, RbxInstance self, string key,
            out LuaValue value)
        {
            if (!(self is RbxRunService runService))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "Heartbeat":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.Heartbeat, context);
                    return true;
                case "Stepped":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.Stepped, context);
                    return true;
                case "RenderStepped":
                    value = LuaCsRbxDatatypeBindings.Wrap(runService.RenderStepped, context);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        // ---- ClickDetector (MouseClick over the host pick pump) ------------------------------

        /// <summary>ClickDetector members: the MouseClick/MouseHoverEnter/MouseHoverLeave signals and
        /// MaxActivationDistance. Signal reads carry the mod context so the returned connection is
        /// tracked for teardown (like RunService/UserInputService); reads are open at the Read tier —
        /// connecting a click handler observes the world, it mutates nothing.</summary>
        private static bool TryReadClickDetector(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (!(self is RbxClickDetector detector))
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "MouseClick":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseClick, context);
                    return true;
                case "MouseHoverEnter":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseHoverEnter, context);
                    return true;
                case "MouseHoverLeave":
                    value = LuaCsRbxDatatypeBindings.Wrap(detector.MouseHoverLeave, context);
                    return true;
                case "MaxActivationDistance":
                    value = detector.MaxActivationDistance;
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        /// <summary>ClickDetector.MaxActivationDistance assignment (studs). Roblox lets any script set
        /// it, but it mutates shared world state, so it takes the WorldEdit gate like part properties.</summary>
        private static bool TryWriteClickDetector(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (!(self is RbxClickDetector detector) || key != "MaxActivationDistance")
            {
                return false;
            }

            context.RequireWorldEdit("setting ClickDetector.MaxActivationDistance");
            detector.MaxActivationDistance = ReadNumberValue(value, "ClickDetector.MaxActivationDistance");
            return true;
        }

        // ---- Camera (workspace.CurrentCamera over the camera rig) ---------------------------

        /// <summary>workspace.CurrentCamera plus the Camera instance's CFrame (over the rig),
        /// CameraType, and CameraSubject. Reads are ungated; writes require WorldEdit.</summary>
        private static bool TryReadCamera(LuaCsRbxModContext context, RbxInstance self,
            string key, out LuaValue value)
        {
            if (key == "CurrentCamera" && self.IsA("Workspace"))
            {
                value = context.WrapInstance(self.FindFirstChildOfClass("Camera"));
                return true;
            }

            if (self.ClassName != "Camera")
            {
                value = LuaValue.Nil;
                return false;
            }

            switch (key)
            {
                case "CFrame":
                    value = LuaCsRbxDatatypeBindings.Wrap(context.Bindings.CameraRig.GetCFrame());
                    return true;
                case "CameraType":
                    RbxEnumItem type = context.Bindings.CameraTypeItem;
                    value = type != null ? LuaCsRbxDatatypeBindings.Wrap(type) : LuaValue.Nil;
                    return true;
                case "CameraSubject":
                    value = context.WrapInstance(context.Bindings.CameraSubject);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        private static bool TryWriteCamera(LuaCsRbxModContext context, RbxInstance self,
            string key, LuaValue value)
        {
            if (self.ClassName != "Camera")
            {
                return false;
            }

            switch (key)
            {
                case "CFrame":
                    context.RequireWorldEdit("setting Camera.CFrame");
                    context.Bindings.CameraRig.SetCFrame(
                        ReadCFrameValue(value, "Camera.CFrame assignment"));
                    return true;
                case "CameraType":
                    context.RequireWorldEdit("setting Camera.CameraType");
                    context.Bindings.CameraTypeItem = ReadCameraTypeValue(value);
                    return true;
                case "CameraSubject":
                    context.RequireWorldEdit("setting Camera.CameraSubject");
                    context.Bindings.SetCameraSubject(
                        ReadOptionalInstance(value, "Camera.CameraSubject assignment"));
                    return true;
                default:
                    return false;
            }
        }

        private static RbxEnumItem ReadCameraTypeValue(LuaValue value)
        {
            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "CameraType")
            {
                return item;
            }

            throw RbxError.BadArgument(
                "Camera.CameraType assignment expects an Enum.CameraType item",
                "pass e.g. Enum.CameraType.Scriptable, got " + Describe(value));
        }

        private static RbxVector3 ReadVector3Value(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxVector3 vector))
            {
                return vector;
            }

            throw RbxError.BadArgument(
                what + " expects a Vector3",
                "pass a Vector3, got " + Describe(value));
        }

        private static RbxCFrame ReadCFrameValue(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxCFrame cframe))
            {
                return cframe;
            }

            throw RbxError.BadArgument(
                what + " expects a CFrame",
                "pass a CFrame, got " + Describe(value));
        }

        private static RbxColor3 ReadColor3Value(LuaValue value, string what)
        {
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw RbxError.BadArgument(
                what + " expects a Color3",
                "pass a Color3, got " + Describe(value));
        }

        private static float ReadNumberValue(LuaValue value, string what)
        {
            if (value.Type == LuaValueType.Number)
            {
                return (float)value.Read<double>();
            }

            throw RbxError.BadArgument(
                what + " expects a number",
                "pass a number, got " + Describe(value));
        }

        private static RbxError SpatialStub(string property)
        {
            // WHY: Shape/Position/Size/CFrame/Color/Transparency/Anchored/CanCollide are wired to
            // the part-property sink; Material needs the material catalog and
            // Orientation/Rotation need Euler decomposition — both later BasePart follow-ups.
            return RbxError.NotImplemented(
                "BasePart." + property,
                "the BasePart material + orientation follow-up",
                "set Shape/CFrame/Position/Size/Color/Transparency/Anchored/CanCollide, which are wired now");
        }
    }
}
