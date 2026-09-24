using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua surface for the pure-spec Roblox datatypes (roadmap §5.1.3): constructor globals
    /// (<c>Vector3.new</c>, <c>CFrame.Angles</c>, <c>Color3.fromRGB</c>, ...), operator
    /// metamethods, Roblox <c>tostring</c> formats, the interned <c>Enum</c> registry, and
    /// deterministic <c>Random</c>. Values cross the seam as tagged userdata with shared locked
    /// metatables (§5.1.5); the metatables are capability-free so they are process-wide statics.
    /// </summary>
    internal static class LuaCsRbxDatatypeBindings
    {
        private static readonly LuaTable Vector3Meta = BuildVector3Meta();
        private static readonly LuaTable Vector2Meta = BuildVector2Meta();
        private static readonly LuaTable CFrameMeta = BuildCFrameMeta();
        private static readonly LuaTable Color3Meta = BuildColor3Meta();
        private static readonly LuaTable UDimMeta = BuildUDimMeta();
        private static readonly LuaTable UDim2Meta = BuildUDim2Meta();
        private static readonly LuaTable RandomMeta = BuildRandomMeta();
        private static readonly LuaTable EnumItemMeta = BuildEnumItemMeta();
        private static readonly LuaTable EnumTypeMeta = BuildEnumTypeMeta();
        private static readonly LuaTable SignalMeta = BuildSignalMeta();
        private static readonly LuaTable ConnectionMeta = BuildConnectionMeta();
        private static readonly LuaTable InputObjectMeta = BuildInputObjectMeta();

        // WHY: enum types/items are interned by the registry; interning the wrappers as well makes
        // raw identity (rawequal) match Roblox in addition to the __eq metamethod.
        private static readonly ConditionalWeakTable<object, LuaCsRbxValueBox> EnumWrappers = new();

        // ---- Wrap entry points --------------------------------------------------------------

        public static LuaValue Wrap(RbxVector3 value)
        {
            return Box(value, Vector3Meta);
        }

        public static LuaValue Wrap(RbxVector2 value)
        {
            return Box(value, Vector2Meta);
        }

        public static LuaValue Wrap(RbxCFrame value)
        {
            return Box(value, CFrameMeta);
        }

        public static LuaValue Wrap(RbxColor3 value)
        {
            return Box(value, Color3Meta);
        }

        public static LuaValue Wrap(RbxUDim value)
        {
            return Box(value, UDimMeta);
        }

        public static LuaValue Wrap(RbxUDim2 value)
        {
            return Box(value, UDim2Meta);
        }

        public static LuaValue Wrap(RbxRandom value)
        {
            return Box(value, RandomMeta);
        }

        public static LuaValue Wrap(RbxScriptSignal value)
        {
            return Box(value, SignalMeta);
        }

        /// <summary>
        /// Wraps a signal with the acting mod context used for scheduling and teardown ownership.
        /// </summary>
        public static LuaValue Wrap(RbxScriptSignal value, LuaCsRbxModContext owner)
        {
            if (owner != null)
            {
                value.BindScheduler(owner.Bindings.Scheduler);
            }

            return new LuaValue(new LuaCsRbxValueBox(value, SignalMeta, owner));
        }

        public static LuaValue Wrap(RbxScriptConnection value)
        {
            return Box(value, ConnectionMeta);
        }

        public static LuaValue Wrap(RbxInputObject value)
        {
            return Box(value, InputObjectMeta);
        }

        public static LuaValue Wrap(RbxEnumItem item)
        {
            return new LuaValue(EnumWrappers.GetValue(item,
                key => new LuaCsRbxValueBox(key, EnumItemMeta)));
        }

        public static LuaValue Wrap(RbxEnum enumType)
        {
            return new LuaValue(EnumWrappers.GetValue(enumType,
                key => new LuaCsRbxValueBox(key, EnumTypeMeta)));
        }

        // ---- Constructor globals (fresh tables per state) -----------------------------------

        public static LuaValue BuildVector3Global()
        {
            LuaTable t = new();
            t["new"] = Fn("Vector3.new", ctx => Wrap(new RbxVector3(
                ReadFloatOr(ctx, 0, 0f, "Vector3.new", 1),
                ReadFloatOr(ctx, 1, 0f, "Vector3.new", 2),
                ReadFloatOr(ctx, 2, 0f, "Vector3.new", 3))));
            t["zero"] = Wrap(RbxVector3.Zero);
            t["one"] = Wrap(RbxVector3.One);
            t["xAxis"] = Wrap(RbxVector3.XAxis);
            t["yAxis"] = Wrap(RbxVector3.YAxis);
            t["zAxis"] = Wrap(RbxVector3.ZAxis);
            t["FromNormalId"] = Fn("Vector3.FromNormalId",
                ctx => Wrap(RbxVector3.FromNormalId(ReadEnumItem(ctx, 0, "Vector3.FromNormalId"))));
            t["FromAxis"] = Fn("Vector3.FromAxis",
                ctx => Wrap(RbxVector3.FromAxis(ReadEnumItem(ctx, 0, "Vector3.FromAxis"))));
            return new LuaValue(t);
        }

        public static LuaValue BuildVector2Global()
        {
            LuaTable t = new();
            t["new"] = Fn("Vector2.new", ctx => Wrap(new RbxVector2(
                ReadFloatOr(ctx, 0, 0f, "Vector2.new", 1),
                ReadFloatOr(ctx, 1, 0f, "Vector2.new", 2))));
            t["zero"] = Wrap(RbxVector2.Zero);
            t["one"] = Wrap(RbxVector2.One);
            t["xAxis"] = Wrap(RbxVector2.XAxis);
            t["yAxis"] = Wrap(RbxVector2.YAxis);
            return new LuaValue(t);
        }

        public static LuaValue BuildCFrameGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("CFrame.new", CFrameNew);
            t["identity"] = Wrap(RbxCFrame.Identity);
            t["lookAt"] = Fn("CFrame.lookAt", ctx => Wrap(RbxCFrame.LookAt(
                ReadVector3(ctx, 0, "CFrame.lookAt"),
                ReadVector3(ctx, 1, "CFrame.lookAt"),
                OptionalVector3(ctx, 2))));
            t["lookAlong"] = Fn("CFrame.lookAlong", ctx => Wrap(RbxCFrame.LookAlong(
                ReadVector3(ctx, 0, "CFrame.lookAlong"),
                ReadVector3(ctx, 1, "CFrame.lookAlong"),
                OptionalVector3(ctx, 2))));
            t["Angles"] = Fn("CFrame.Angles", ctx => Wrap(RbxCFrame.Angles(
                ReadFloat(ctx, 0, "CFrame.Angles"),
                ReadFloat(ctx, 1, "CFrame.Angles"),
                ReadFloat(ctx, 2, "CFrame.Angles"))));
            t["fromEulerAngles"] = Fn("CFrame.fromEulerAngles", ctx => Wrap(RbxCFrame.FromEulerAngles(
                ReadFloat(ctx, 0, "CFrame.fromEulerAngles"),
                ReadFloat(ctx, 1, "CFrame.fromEulerAngles"),
                ReadFloat(ctx, 2, "CFrame.fromEulerAngles"),
                ReadRotationOrder(ctx, 3))));
            t["fromEulerAnglesXYZ"] = Fn("CFrame.fromEulerAnglesXYZ", ctx => Wrap(
                RbxCFrame.FromEulerAnglesXYZ(
                    ReadFloat(ctx, 0, "CFrame.fromEulerAnglesXYZ"),
                    ReadFloat(ctx, 1, "CFrame.fromEulerAnglesXYZ"),
                    ReadFloat(ctx, 2, "CFrame.fromEulerAnglesXYZ"))));
            t["fromEulerAnglesYXZ"] = Fn("CFrame.fromEulerAnglesYXZ", ctx => Wrap(
                RbxCFrame.FromEulerAnglesYXZ(
                    ReadFloat(ctx, 0, "CFrame.fromEulerAnglesYXZ"),
                    ReadFloat(ctx, 1, "CFrame.fromEulerAnglesYXZ"),
                    ReadFloat(ctx, 2, "CFrame.fromEulerAnglesYXZ"))));
            t["fromOrientation"] = Fn("CFrame.fromOrientation", ctx => Wrap(RbxCFrame.FromOrientation(
                ReadFloat(ctx, 0, "CFrame.fromOrientation"),
                ReadFloat(ctx, 1, "CFrame.fromOrientation"),
                ReadFloat(ctx, 2, "CFrame.fromOrientation"))));
            t["fromAxisAngle"] = Fn("CFrame.fromAxisAngle", ctx => Wrap(RbxCFrame.FromAxisAngle(
                ReadVector3(ctx, 0, "CFrame.fromAxisAngle"),
                ReadFloat(ctx, 1, "CFrame.fromAxisAngle"))));
            t["fromMatrix"] = Fn("CFrame.fromMatrix", ctx => Wrap(RbxCFrame.FromMatrix(
                ReadVector3(ctx, 0, "CFrame.fromMatrix"),
                ReadVector3(ctx, 1, "CFrame.fromMatrix"),
                ReadVector3(ctx, 2, "CFrame.fromMatrix"),
                OptionalVector3(ctx, 3))));
            t["fromRotationBetweenVectors"] = Fn("CFrame.fromRotationBetweenVectors", ctx => Wrap(
                RbxCFrame.FromRotationBetweenVectors(
                    ReadVector3(ctx, 0, "CFrame.fromRotationBetweenVectors", 1),
                    ReadVector3(ctx, 1, "CFrame.fromRotationBetweenVectors", 2))));
            return new LuaValue(t);
        }

        public static LuaValue BuildColor3Global()
        {
            LuaTable t = new();
            t["new"] = Fn("Color3.new", ctx => Wrap(new RbxColor3(
                ReadFloatOr(ctx, 0, 0f, "Color3.new", 1),
                ReadFloatOr(ctx, 1, 0f, "Color3.new", 2),
                ReadFloatOr(ctx, 2, 0f, "Color3.new", 3))));
            t["fromRGB"] = Fn("Color3.fromRGB", ctx => Wrap(RbxColor3.FromRGB(
                ReadFloatOr(ctx, 0, 0f, "Color3.fromRGB", 1),
                ReadFloatOr(ctx, 1, 0f, "Color3.fromRGB", 2),
                ReadFloatOr(ctx, 2, 0f, "Color3.fromRGB", 3))));
            t["fromHSV"] = Fn("Color3.fromHSV", ctx => Wrap(RbxColor3.FromHSV(
                ReadFloat(ctx, 0, "Color3.fromHSV"),
                ReadFloat(ctx, 1, "Color3.fromHSV"),
                ReadFloat(ctx, 2, "Color3.fromHSV"))));
            t["fromHex"] = Fn("Color3.fromHex",
                ctx => Wrap(RbxColor3.FromHex(ReadString(ctx, 0, "Color3.fromHex"))));
            // WHY: deprecated in the mirror ("functionally equivalent to Color3:ToHSV()") but still
            // callable in Roblox, so corpus scripts that use the static form keep working.
            t["toHSV"] = new LuaValue(FnMulti("Color3.toHSV", ctx =>
                HsvValues(ReadColor3(ctx, 0, "Color3.toHSV", 1))));
            return new LuaValue(t);
        }

        public static LuaValue BuildUDimGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("UDim.new", ctx => Wrap(new RbxUDim(
                ReadFloatOr(ctx, 0, 0f, "UDim.new", 1), ReadOffset(ctx, 1, "UDim.new", 2))));
            return new LuaValue(t);
        }

        public static LuaValue BuildUDim2Global()
        {
            LuaTable t = new();
            t["new"] = Fn("UDim2.new", ctx =>
            {
                if (TryUnbox(Arg(ctx, 0), out RbxUDim x) && TryUnbox(Arg(ctx, 1), out RbxUDim y))
                {
                    return Wrap(new RbxUDim2(x, y));
                }

                return Wrap(new RbxUDim2(
                    ReadFloatOr(ctx, 0, 0f, "UDim2.new", 1), ReadOffset(ctx, 1, "UDim2.new", 2),
                    ReadFloatOr(ctx, 2, 0f, "UDim2.new", 3), ReadOffset(ctx, 3, "UDim2.new", 4)));
            });
            t["fromScale"] = Fn("UDim2.fromScale", ctx => Wrap(RbxUDim2.FromScale(
                ReadFloatOr(ctx, 0, 0f, "UDim2.fromScale", 1),
                ReadFloatOr(ctx, 1, 0f, "UDim2.fromScale", 2))));
            t["fromOffset"] = Fn("UDim2.fromOffset", ctx => Wrap(RbxUDim2.FromOffset(
                ReadOffset(ctx, 0, "UDim2.fromOffset", 1), ReadOffset(ctx, 1, "UDim2.fromOffset", 2))));
            return new LuaValue(t);
        }

        public static LuaValue BuildRandomGlobal()
        {
            LuaTable t = new();
            // WHY nil and not "not a number" picks the unseeded generator: the seed is an optional
            // number, so "7" seeds like 7 and a table is refused instead of silently ignored.
            t["new"] = Fn("Random.new", ctx => Arg(ctx, 0).Type == LuaValueType.Nil
                ? Wrap(new RbxRandom())
                : Wrap(new RbxRandom(ClampRandomSeed(ReadDouble(ctx, 0, "Random.new", 1)))));
            return new LuaValue(t);
        }

        public static LuaValue BuildEnumGlobal(RbxEnumRegistry registry)
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Enum.__index", ctx =>
            {
                string key = ReadString(ctx, 1, "Enum access");
                if (key == "GetEnums")
                {
                    return new LuaValue(Fn("Enum.GetEnums", inner =>
                    {
                        LuaTable list = new();
                        int index = 1;
                        foreach (RbxEnum item in registry.GetEnums())
                        {
                            list[index++] = Wrap(item);
                        }

                        return new LuaValue(list);
                    }));
                }

                return Wrap(registry.Get(key));
            });
            meta[Metamethods.ToString] = Fn("Enum.__tostring", _ => "Enum");
            Lock(meta);

            LuaTable t = new();
            t.Metatable = meta;
            return new LuaValue(t);
        }

        // ---- TweenInfo ----------------------------------------------------------------------

        /// <summary>
        /// Carries a TweenInfo plus its resolved enum items: the shared meta reads the items
        /// back out, so `info.EasingStyle` is the interned Enum.EasingStyle item like Roblox.
        /// </summary>
        private sealed class TweenInfoBox
        {
            public TweenInfoBox(RbxTweenInfo info, RbxEnumItem style, RbxEnumItem direction)
            {
                Info = info;
                Style = style;
                Direction = direction;
            }

            public RbxTweenInfo Info { get; }

            public RbxEnumItem Style { get; }

            public RbxEnumItem Direction { get; }
        }

        private static readonly LuaTable TweenInfoMeta = BuildTweenInfoMeta();

        /// <summary>Wraps a TweenInfo with its enum items resolved from the registry.</summary>
        public static LuaValue Wrap(RbxTweenInfo value, RbxEnumRegistry registry)
        {
            return Box(ResolveTweenInfoBox(value, registry), TweenInfoMeta);
        }

        /// <summary>Reads a TweenInfo userdata (argument after self is 1-based here).</summary>
        public static RbxTweenInfo ReadTweenInfo(LuaValue value, string what, int argumentNumber)
        {
            if (TryUnbox(value, out TweenInfoBox box) && box.Info != null)
            {
                return box.Info;
            }

            throw RbxError.BadArgument(
                what + " expects a TweenInfo at argument " + argumentNumber,
                "pass TweenInfo.new(...) at argument " + argumentNumber
                + ", got " + Describe(value));
        }

        public static LuaValue BuildTweenInfoGlobal(RbxEnumRegistry registry)
        {
            LuaTable t = new();
            t["new"] = Fn("TweenInfo.new", ctx => Wrap(new RbxTweenInfo(
                ReadTweenTime(Arg(ctx, 0)),
                ReadTweenStyle(Arg(ctx, 1), registry),
                ReadTweenDirection(Arg(ctx, 2), registry),
                ReadTweenRepeatCount(Arg(ctx, 3)),
                ReadTweenReverses(Arg(ctx, 4)),
                ReadTweenDelay(Arg(ctx, 5))), registry));
            return new LuaValue(t);
        }

        private static TweenInfoBox ResolveTweenInfoBox(RbxTweenInfo info,
            RbxEnumRegistry registry)
        {
            return new TweenInfoBox(info, ResolveTweenEnumItem(registry, "EasingStyle",
                info.EasingStyle.ToString()), ResolveTweenEnumItem(registry,
                "EasingDirection", info.EasingDirection.ToString()));
        }

        private static RbxEnumItem ResolveTweenEnumItem(RbxEnumRegistry registry,
            string enumName, string itemName)
        {
            if (registry.TryGet(enumName, out RbxEnum enumType)
                && enumType.TryGetItem(itemName, out RbxEnumItem item))
            {
                return item;
            }

            throw RbxError.BadArgument(
                "TweenInfo cannot resolve Enum." + enumName + "." + itemName,
                "use the default enum registry, which ships " + enumName + " with TweenService");
        }

        private static LuaTable BuildTweenInfoMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("TweenInfo.__index", ctx =>
            {
                TweenInfoBox self = SelfTweenInfo(ctx);
                string key = ReadString(ctx, 1, "TweenInfo member access");
                switch (key)
                {
                    case "Time": return self.Info.Time;
                    case "EasingStyle": return Wrap(self.Style);
                    case "EasingDirection": return Wrap(self.Direction);
                    case "RepeatCount": return (double)self.Info.RepeatCount;
                    case "Reverses": return self.Info.Reverses;
                    case "DelayTime": return self.Info.DelayTime;
                    default: throw NotAMember(key, "TweenInfo");
                }
            });
            meta[Metamethods.NewIndex] = Fn("TweenInfo.__newindex",
                _ => throw ReadOnlyMember("TweenInfo"));
            meta[Metamethods.Eq] = Fn("TweenInfo.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out TweenInfoBox a)
                && TryUnbox(Arg(ctx, 1), out TweenInfoBox b)
                && a.Info.Equals(b.Info));
            meta[Metamethods.ToString] = Fn("TweenInfo.__tostring", _ => "TweenInfo");
            return Lock(meta);
        }

        private static TweenInfoBox SelfTweenInfo(LuaFunctionExecutionContext ctx)
        {
            LuaValue value = Arg(ctx, 0);
            if (TryUnbox(value, out TweenInfoBox box) && box.Info != null)
            {
                return box;
            }

            throw RbxError.BadArgument(
                "TweenInfo member access expects a TweenInfo as self",
                "call TweenInfo members with a colon, got " + Describe(value));
        }

        private static double ReadTweenTime(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultTime;
            }

            if (!TryCoerceNumber(value, out double time))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for time at argument 1",
                    "pass a duration in seconds, got " + Describe(value) + " at argument 1");
            }

            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a finite time at argument 1",
                    "pass a duration in seconds, e.g. TweenInfo.new(1)");
            }

            return time;
        }

        private static double ReadTweenDelay(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultDelayTime;
            }

            if (!TryCoerceNumber(value, out double delay))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for delayTime at argument 6",
                    "pass a delay in seconds, got " + Describe(value) + " at argument 6");
            }

            if (double.IsNaN(delay) || double.IsInfinity(delay))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a finite delayTime at argument 6",
                    "pass a delay in seconds, e.g. TweenInfo.new(1, nil, nil, 0, false, 0.5)");
            }

            return delay;
        }

        private static RbxEasingStyle ReadTweenStyle(LuaValue value, RbxEnumRegistry registry)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultEasingStyle;
            }

            return (RbxEasingStyle)ReadTweenEnumValue(value, registry, "EasingStyle", 2);
        }

        private static RbxEasingDirection ReadTweenDirection(LuaValue value,
            RbxEnumRegistry registry)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultEasingDirection;
            }

            return (RbxEasingDirection)ReadTweenEnumValue(value, registry, "EasingDirection", 3);
        }

        private static int ReadTweenEnumValue(LuaValue value, RbxEnumRegistry registry,
            string enumName, int argumentNumber)
        {
            if (!TryUnbox(value, out RbxEnumItem item) || item.EnumType == null
                || item.EnumType.Name != enumName)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects Enum." + enumName + " at argument " + argumentNumber,
                    "pass Enum." + enumName + ".Quad, got " + Describe(value)
                    + " at argument " + argumentNumber);
            }

            if (!registry.TryGet(enumName, out RbxEnum enumType)
                || !enumType.TryGetItem(item.Name, out RbxEnumItem _))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new got an unknown Enum." + enumName + " item '" + item.Name + "'",
                    "use one of Enum." + enumName + ":GetEnumItems()");
            }

            return item.Value;
        }

        private static int ReadTweenRepeatCount(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultRepeatCount;
            }

            if (!TryCoerceNumber(value, out double count))
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for repeatCount at argument 4",
                    "pass an integer repeat count, got " + Describe(value) + " at argument 4");
            }

            if (double.IsNaN(count) || double.IsInfinity(count)
                || count != Math.Floor(count) || count > int.MaxValue || count < int.MinValue)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a finite integer repeatCount at argument 4",
                    "pass an integer like 0, or -1 to repeat indefinitely");
            }

            return (int)count;
        }

        private static bool ReadTweenReverses(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return RbxTweenInfo.DefaultReverses;
            }

            if (value.Type != LuaValueType.Boolean)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a boolean for reverses at argument 5",
                    "pass true or false, got " + Describe(value) + " at argument 5");
            }

            return value.Read<bool>();
        }

        // ---- Vector3 ------------------------------------------------------------------------

        private static LuaTable BuildVector3Meta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Dot"] = new LuaValue(Fn("Vector3.Dot", ctx =>
                    Self3(ctx).Dot(ReadVector3(ctx, 1, "Vector3:Dot", 1)))),
                ["Cross"] = new LuaValue(Fn("Vector3.Cross", ctx =>
                    Wrap(Self3(ctx).Cross(ReadVector3(ctx, 1, "Vector3:Cross", 1))))),
                ["Lerp"] = new LuaValue(Fn("Vector3.Lerp", ctx => Wrap(Self3(ctx).Lerp(
                    ReadVector3(ctx, 1, "Vector3:Lerp", 1), ReadFloat(ctx, 2, "Vector3:Lerp", 2))))),
                ["Angle"] = new LuaValue(Fn("Vector3.Angle", ctx => Self3(ctx).Angle(
                    ReadVector3(ctx, 1, "Vector3:Angle", 1),
                    OptionalVector3(ctx, 2, "Vector3:Angle", 2)))),
                ["FuzzyEq"] = new LuaValue(Fn("Vector3.FuzzyEq", ctx => Self3(ctx).FuzzyEq(
                    ReadVector3(ctx, 1, "Vector3:FuzzyEq", 1),
                    ReadFloatOr(ctx, 2, 1e-5f, "Vector3:FuzzyEq", 2)))),
                ["Abs"] = new LuaValue(Fn("Vector3.Abs", ctx => Wrap(Self3(ctx).Abs()))),
                ["Ceil"] = new LuaValue(Fn("Vector3.Ceil", ctx => Wrap(Self3(ctx).Ceil()))),
                ["Floor"] = new LuaValue(Fn("Vector3.Floor", ctx => Wrap(Self3(ctx).Floor()))),
                ["Sign"] = new LuaValue(Fn("Vector3.Sign", ctx => Wrap(Self3(ctx).Sign()))),
                ["Max"] = new LuaValue(Fn("Vector3.Max", ctx =>
                    Wrap(Self3(ctx).Max(ReadVector3(ctx, 1, "Vector3:Max", 1))))),
                ["Min"] = new LuaValue(Fn("Vector3.Min", ctx =>
                    Wrap(Self3(ctx).Min(ReadVector3(ctx, 1, "Vector3:Min", 1)))))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Vector3.__index", ctx =>
            {
                RbxVector3 self = Self3(ctx);
                string key = ReadString(ctx, 1, "Vector3 member access");
                switch (key)
                {
                    case "X": return self.X;
                    case "Y": return self.Y;
                    case "Z": return self.Z;
                    case "Magnitude": return self.Magnitude;
                    case "Unit": return Wrap(self.Unit);
                    default:
                        return methods.TryGetValue(key, out LuaValue method)
                            ? method
                            : throw NotAMember(key, "Vector3");
                }
            });
            meta[Metamethods.NewIndex] = Fn("Vector3.__newindex",
                _ => throw ReadOnlyMember("Vector3"));
            meta[Metamethods.Add] = Fn("Vector3.__add", ctx => Wrap(
                ReadVector3(ctx, 0, "Vector3 +") + ReadVector3(ctx, 1, "Vector3 +")));
            meta[Metamethods.Sub] = Fn("Vector3.__sub", ctx => Wrap(
                ReadVector3(ctx, 0, "Vector3 -") - ReadVector3(ctx, 1, "Vector3 -")));
            meta[Metamethods.Unm] = Fn("Vector3.__unm", ctx => Wrap(-Self3(ctx)));
            // WHY a numeric string is a scalar operand: Roblox's Vector3 is Luau's native vector,
            // whose arithmetic converts the other operand with luaV_tonumber, so v * "2" doubles v.
            meta[Metamethods.Mul] = Fn("Vector3.__mul", ctx =>
            {
                if (TryCoerceNumber(Arg(ctx, 0), out double leftScalar))
                {
                    return Wrap((float)leftScalar * ReadVector3(ctx, 1, "Vector3 *"));
                }

                RbxVector3 left = ReadVector3(ctx, 0, "Vector3 *");
                return TryCoerceNumber(Arg(ctx, 1), out double rightScalar)
                    ? Wrap(left * (float)rightScalar)
                    : Wrap(left * ReadVector3(ctx, 1, "Vector3 *"));
            });
            meta[Metamethods.Div] = Fn("Vector3.__div", ctx =>
            {
                RbxVector3 left = ReadVector3(ctx, 0, "Vector3 /");
                return TryCoerceNumber(Arg(ctx, 1), out double scalar)
                    ? Wrap(left / (float)scalar)
                    : Wrap(left / ReadVector3(ctx, 1, "Vector3 /"));
            });
            meta[Metamethods.Eq] = Fn("Vector3.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxVector3 a)
                && TryUnbox(Arg(ctx, 1), out RbxVector3 b) && a == b);
            meta[Metamethods.ToString] = Fn("Vector3.__tostring", ctx => Self3(ctx).ToString());
            return Lock(meta);
        }

        private static RbxVector3 Self3(LuaFunctionExecutionContext ctx)
        {
            return ReadVector3(ctx, 0, "Vector3 method");
        }

        // ---- Vector2 ------------------------------------------------------------------------

        private static LuaTable BuildVector2Meta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Dot"] = new LuaValue(Fn("Vector2.Dot", ctx =>
                    Self2(ctx).Dot(ReadVector2(ctx, 1, "Vector2:Dot", 1)))),
                ["Cross"] = new LuaValue(Fn("Vector2.Cross", ctx =>
                    Self2(ctx).Cross(ReadVector2(ctx, 1, "Vector2:Cross", 1)))),
                ["Lerp"] = new LuaValue(Fn("Vector2.Lerp", ctx => Wrap(Self2(ctx).Lerp(
                    ReadVector2(ctx, 1, "Vector2:Lerp", 1), ReadFloat(ctx, 2, "Vector2:Lerp", 2))))),
                ["Angle"] = new LuaValue(Fn("Vector2.Angle", ctx => Self2(ctx).Angle(
                    ReadVector2(ctx, 1, "Vector2:Angle", 1),
                    ReadOptionalBoolean(ctx, 2, "Vector2:Angle", 2)))),
                ["FuzzyEq"] = new LuaValue(Fn("Vector2.FuzzyEq", ctx => Self2(ctx).FuzzyEq(
                    ReadVector2(ctx, 1, "Vector2:FuzzyEq", 1),
                    ReadFloatOr(ctx, 2, 1e-5f, "Vector2:FuzzyEq", 2)))),
                ["Abs"] = new LuaValue(Fn("Vector2.Abs", ctx => Wrap(Self2(ctx).Abs()))),
                ["Ceil"] = new LuaValue(Fn("Vector2.Ceil", ctx => Wrap(Self2(ctx).Ceil()))),
                ["Floor"] = new LuaValue(Fn("Vector2.Floor", ctx => Wrap(Self2(ctx).Floor()))),
                ["Sign"] = new LuaValue(Fn("Vector2.Sign", ctx => Wrap(Self2(ctx).Sign()))),
                ["Max"] = new LuaValue(Fn("Vector2.Max", ctx =>
                    Wrap(Self2(ctx).Max(ReadVector2(ctx, 1, "Vector2:Max", 1))))),
                ["Min"] = new LuaValue(Fn("Vector2.Min", ctx =>
                    Wrap(Self2(ctx).Min(ReadVector2(ctx, 1, "Vector2:Min", 1)))))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Vector2.__index", ctx =>
            {
                RbxVector2 self = Self2(ctx);
                string key = ReadString(ctx, 1, "Vector2 member access");
                switch (key)
                {
                    case "X": return self.X;
                    case "Y": return self.Y;
                    case "Magnitude": return self.Magnitude;
                    case "Unit": return Wrap(self.Unit);
                    default:
                        return methods.TryGetValue(key, out LuaValue method)
                            ? method
                            : throw NotAMember(key, "Vector2");
                }
            });
            meta[Metamethods.NewIndex] = Fn("Vector2.__newindex",
                _ => throw ReadOnlyMember("Vector2"));
            meta[Metamethods.Add] = Fn("Vector2.__add", ctx => Wrap(
                ReadVector2(ctx, 0, "Vector2 +") + ReadVector2(ctx, 1, "Vector2 +")));
            meta[Metamethods.Sub] = Fn("Vector2.__sub", ctx => Wrap(
                ReadVector2(ctx, 0, "Vector2 -") - ReadVector2(ctx, 1, "Vector2 -")));
            meta[Metamethods.Unm] = Fn("Vector2.__unm", ctx => Wrap(-Self2(ctx)));
            // WHY a numeric string is a scalar operand, as for Vector3: Roblox reads the number operand of
            // a datatype operator the way luaL_checknumber reads a parameter, so v * "2" doubles v there
            // too, and one script must not scale a Vector3 by "2" but fail on a Vector2 (C1-08).
            meta[Metamethods.Mul] = Fn("Vector2.__mul", ctx =>
            {
                if (TryCoerceNumber(Arg(ctx, 0), out double leftScalar))
                {
                    return Wrap((float)leftScalar * ReadVector2(ctx, 1, "Vector2 *"));
                }

                RbxVector2 left = ReadVector2(ctx, 0, "Vector2 *");
                return TryCoerceNumber(Arg(ctx, 1), out double rightScalar)
                    ? Wrap(left * (float)rightScalar)
                    : Wrap(left * ReadVector2(ctx, 1, "Vector2 *"));
            });
            meta[Metamethods.Div] = Fn("Vector2.__div", ctx =>
            {
                RbxVector2 left = ReadVector2(ctx, 0, "Vector2 /");
                return TryCoerceNumber(Arg(ctx, 1), out double scalar)
                    ? Wrap(left / (float)scalar)
                    : Wrap(left / ReadVector2(ctx, 1, "Vector2 /"));
            });
            meta[Metamethods.Eq] = Fn("Vector2.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxVector2 a)
                && TryUnbox(Arg(ctx, 1), out RbxVector2 b) && a == b);
            meta[Metamethods.ToString] = Fn("Vector2.__tostring", ctx => Self2(ctx).ToString());
            return Lock(meta);
        }

        private static RbxVector2 Self2(LuaFunctionExecutionContext ctx)
        {
            return ReadVector2(ctx, 0, "Vector2 method");
        }

        private static RbxVector2 ReadVector2(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadVector2(ctx, index, what, index + 1);
        }

        private static RbxVector2 ReadVector2(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxVector2 vector))
            {
                return vector;
            }

            throw ExpectedArgument(what, "a Vector2", value, argumentNumber);
        }

        // ---- CFrame -------------------------------------------------------------------------

        private static LuaTable BuildCFrameMeta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Inverse"] = new LuaValue(Fn("CFrame.Inverse", ctx => Wrap(SelfCf(ctx).Inverse()))),
                ["ToWorldSpace"] = new LuaValue(Fn("CFrame.ToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).ToWorldSpace(ReadCFrame(ctx, 1, "CFrame:ToWorldSpace", 1))))),
                ["ToObjectSpace"] = new LuaValue(Fn("CFrame.ToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).ToObjectSpace(ReadCFrame(ctx, 1, "CFrame:ToObjectSpace", 1))))),
                ["PointToWorldSpace"] = new LuaValue(Fn("CFrame.PointToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).PointToWorldSpace(
                        ReadVector3(ctx, 1, "CFrame:PointToWorldSpace", 1))))),
                ["PointToObjectSpace"] = new LuaValue(Fn("CFrame.PointToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).PointToObjectSpace(
                        ReadVector3(ctx, 1, "CFrame:PointToObjectSpace", 1))))),
                ["VectorToWorldSpace"] = new LuaValue(Fn("CFrame.VectorToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).VectorToWorldSpace(
                        ReadVector3(ctx, 1, "CFrame:VectorToWorldSpace", 1))))),
                ["VectorToObjectSpace"] = new LuaValue(Fn("CFrame.VectorToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).VectorToObjectSpace(
                        ReadVector3(ctx, 1, "CFrame:VectorToObjectSpace", 1))))),
                ["Lerp"] = new LuaValue(Fn("CFrame.Lerp", ctx => Wrap(SelfCf(ctx).Lerp(
                    ReadCFrame(ctx, 1, "CFrame:Lerp", 1), ReadFloat(ctx, 2, "CFrame:Lerp", 2))))),
                ["Orthonormalize"] = new LuaValue(Fn("CFrame.Orthonormalize",
                    ctx => Wrap(SelfCf(ctx).Orthonormalize()))),
                ["FuzzyEq"] = new LuaValue(Fn("CFrame.FuzzyEq", ctx => SelfCf(ctx).FuzzyEq(
                    ReadCFrame(ctx, 1, "CFrame:FuzzyEq", 1),
                    ReadFloatOr(ctx, 2, 1e-5f, "CFrame:FuzzyEq", 2)))),
                ["GetComponents"] = new LuaValue(FnMulti("CFrame.GetComponents",
                    ctx => ComponentValues(SelfCf(ctx)))),
                // WHY: the mirror keeps the lowercase spelling as "Equivalent to GetComponents()".
                ["components"] = new LuaValue(FnMulti("CFrame.components",
                    ctx => ComponentValues(SelfCf(ctx)))),
                ["ToEulerAnglesXYZ"] = new LuaValue(FnMulti("CFrame.ToEulerAnglesXYZ",
                    ctx => AngleValues(SelfCf(ctx).ToEulerAnglesXYZ()))),
                ["ToEulerAnglesYXZ"] = new LuaValue(FnMulti("CFrame.ToEulerAnglesYXZ",
                    ctx => AngleValues(SelfCf(ctx).ToEulerAnglesYXZ()))),
                ["ToOrientation"] = new LuaValue(FnMulti("CFrame.ToOrientation",
                    ctx => AngleValues(SelfCf(ctx).ToOrientation()))),
                ["ToEulerAngles"] = new LuaValue(FnMulti("CFrame.ToEulerAngles",
                    ctx => AngleValues(SelfCf(ctx).ToEulerAngles(
                        ReadRotationOrder(ctx, 1, "CFrame:ToEulerAngles", 1))))),
                ["ToAxisAngle"] = new LuaValue(FnMulti("CFrame.ToAxisAngle", ctx =>
                {
                    (RbxVector3 axis, float angle) = SelfCf(ctx).ToAxisAngle();
                    return new LuaValue[] { Wrap(axis), angle };
                })),
                ["AngleBetween"] = new LuaValue(Fn("CFrame.AngleBetween", ctx =>
                    SelfCf(ctx).AngleBetween(ReadCFrame(ctx, 1, "CFrame:AngleBetween", 1))))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("CFrame.__index", ctx =>
            {
                RbxCFrame self = SelfCf(ctx);
                string key = ReadString(ctx, 1, "CFrame member access");
                switch (key)
                {
                    case "Position": return Wrap(self.Position);
                    case "X": return self.X;
                    case "Y": return self.Y;
                    case "Z": return self.Z;
                    case "Rotation": return Wrap(self.Rotation);
                    case "XVector": return Wrap(self.XVector);
                    case "YVector": return Wrap(self.YVector);
                    case "ZVector": return Wrap(self.ZVector);
                    case "RightVector": return Wrap(self.RightVector);
                    case "UpVector": return Wrap(self.UpVector);
                    case "LookVector": return Wrap(self.LookVector);
                    default:
                        return methods.TryGetValue(key, out LuaValue method)
                            ? method
                            : throw NotAMember(key, "CFrame");
                }
            });
            meta[Metamethods.NewIndex] = Fn("CFrame.__newindex",
                _ => throw ReadOnlyMember("CFrame"));
            meta[Metamethods.Mul] = Fn("CFrame.__mul", ctx =>
            {
                RbxCFrame left = ReadCFrame(ctx, 0, "CFrame *");
                LuaValue b = Arg(ctx, 1);
                if (TryUnbox(b, out RbxCFrame rightCf))
                {
                    return Wrap(left * rightCf);
                }

                if (TryUnbox(b, out RbxVector3 rightVec))
                {
                    return Wrap(left * rightVec);
                }

                throw RbxError.BadArgument(
                    "CFrame * expects a CFrame or Vector3 at argument 2",
                    "pass a CFrame or Vector3, got " + Describe(b) + " at argument 2");
            });
            meta[Metamethods.Add] = Fn("CFrame.__add", ctx => Wrap(
                ReadCFrame(ctx, 0, "CFrame +") + ReadVector3(ctx, 1, "CFrame +")));
            meta[Metamethods.Sub] = Fn("CFrame.__sub", ctx => Wrap(
                ReadCFrame(ctx, 0, "CFrame -") - ReadVector3(ctx, 1, "CFrame -")));
            meta[Metamethods.Eq] = Fn("CFrame.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxCFrame a)
                && TryUnbox(Arg(ctx, 1), out RbxCFrame b) && a == b);
            meta[Metamethods.ToString] = Fn("CFrame.__tostring", ctx => SelfCf(ctx).ToString());
            return Lock(meta);
        }

        private static RbxCFrame SelfCf(LuaFunctionExecutionContext ctx)
        {
            return ReadCFrame(ctx, 0, "CFrame method");
        }

        private static LuaValue[] ComponentValues(RbxCFrame cframe)
        {
            float[] components = cframe.GetComponents();
            LuaValue[] values = new LuaValue[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                values[i] = components[i];
            }

            return values;
        }

        private static LuaValue[] AngleValues((float rx, float ry, float rz) angles)
        {
            return new LuaValue[] { angles.rx, angles.ry, angles.rz };
        }

        private static LuaValue CFrameNew(LuaFunctionExecutionContext ctx)
        {
            int count = ctx.ArgumentCount;
            if (count == 0)
            {
                return Wrap(RbxCFrame.Identity);
            }

            if (TryUnbox(Arg(ctx, 0), out RbxVector3 pos))
            {
                if (count >= 2 && TryUnbox(Arg(ctx, 1), out RbxVector3 lookAt))
                {
                    // WHY: deprecated CFrame.new(pos, lookAt) overload kept for tutorial-corpus scripts.
                    return Wrap(RbxCFrame.FromPositionLookAt(pos, lookAt));
                }

                return Wrap(RbxCFrame.FromPosition(pos));
            }

            switch (count)
            {
                case 3:
                    return Wrap(RbxCFrame.FromPosition(
                        ReadFloat(ctx, 0, "CFrame.new"),
                        ReadFloat(ctx, 1, "CFrame.new"),
                        ReadFloat(ctx, 2, "CFrame.new")));
                case 7:
                    return Wrap(RbxCFrame.FromQuaternion(
                        ReadFloat(ctx, 0, "CFrame.new"), ReadFloat(ctx, 1, "CFrame.new"),
                        ReadFloat(ctx, 2, "CFrame.new"), ReadFloat(ctx, 3, "CFrame.new"),
                        ReadFloat(ctx, 4, "CFrame.new"), ReadFloat(ctx, 5, "CFrame.new"),
                        ReadFloat(ctx, 6, "CFrame.new")));
                case 12:
                    return Wrap(new RbxCFrame(
                        ReadFloat(ctx, 0, "CFrame.new"), ReadFloat(ctx, 1, "CFrame.new"),
                        ReadFloat(ctx, 2, "CFrame.new"), ReadFloat(ctx, 3, "CFrame.new"),
                        ReadFloat(ctx, 4, "CFrame.new"), ReadFloat(ctx, 5, "CFrame.new"),
                        ReadFloat(ctx, 6, "CFrame.new"), ReadFloat(ctx, 7, "CFrame.new"),
                        ReadFloat(ctx, 8, "CFrame.new"), ReadFloat(ctx, 9, "CFrame.new"),
                        ReadFloat(ctx, 10, "CFrame.new"), ReadFloat(ctx, 11, "CFrame.new")));
                default:
                    throw RbxError.BadArgument(
                        "CFrame.new does not accept " + count + " arguments",
                        "call CFrame.new(), CFrame.new(x, y, z), CFrame.new(pos), " +
                        "CFrame.new(x, y, z, qx, qy, qz, qw), or the 12-component overload");
            }
        }

        // ---- Color3 -------------------------------------------------------------------------

        private static LuaTable BuildColor3Meta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Lerp"] = new LuaValue(Fn("Color3.Lerp", ctx => Wrap(SelfColor(ctx).Lerp(
                    ReadColor3(ctx, 1, "Color3:Lerp", 1), ReadFloat(ctx, 2, "Color3:Lerp", 2))))),
                ["ToHSV"] = new LuaValue(FnMulti("Color3.ToHSV", ctx => HsvValues(SelfColor(ctx)))),
                ["ToHex"] = new LuaValue(Fn("Color3.ToHex", ctx => SelfColor(ctx).ToHex()))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Color3.__index", ctx =>
            {
                RbxColor3 self = SelfColor(ctx);
                string key = ReadString(ctx, 1, "Color3 member access");
                switch (key)
                {
                    case "R": return self.R;
                    case "G": return self.G;
                    case "B": return self.B;
                    default:
                        return methods.TryGetValue(key, out LuaValue method)
                            ? method
                            : throw NotAMember(key, "Color3");
                }
            });
            meta[Metamethods.NewIndex] = Fn("Color3.__newindex",
                _ => throw ReadOnlyMember("Color3"));
            meta[Metamethods.Eq] = Fn("Color3.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxColor3 a)
                && TryUnbox(Arg(ctx, 1), out RbxColor3 b) && a == b);
            meta[Metamethods.ToString] = Fn("Color3.__tostring", ctx => SelfColor(ctx).ToString());
            return Lock(meta);
        }

        private static RbxColor3 SelfColor(LuaFunctionExecutionContext ctx)
        {
            return ReadColor3(ctx, 0, "Color3 method");
        }

        private static RbxColor3 ReadColor3(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadColor3(ctx, index, what, index + 1);
        }

        private static RbxColor3 ReadColor3(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw ExpectedArgument(what, "a Color3", value, argumentNumber);
        }

        private static LuaValue[] HsvValues(RbxColor3 color)
        {
            (float h, float s, float v) = color.ToHSV();
            return new LuaValue[] { h, s, v };
        }

        // ---- UDim / UDim2 -------------------------------------------------------------------

        private static LuaTable BuildUDimMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("UDim.__index", ctx =>
            {
                RbxUDim self = ReadUDim(ctx, 0, "UDim method");
                string key = ReadString(ctx, 1, "UDim member access");
                switch (key)
                {
                    case "Scale": return self.Scale;
                    case "Offset": return self.Offset;
                    default: throw NotAMember(key, "UDim");
                }
            });
            meta[Metamethods.NewIndex] = Fn("UDim.__newindex", _ => throw ReadOnlyMember("UDim"));
            meta[Metamethods.Add] = Fn("UDim.__add", ctx =>
                Wrap(ReadUDim(ctx, 0, "UDim +") + ReadUDim(ctx, 1, "UDim +")));
            meta[Metamethods.Sub] = Fn("UDim.__sub", ctx =>
                Wrap(ReadUDim(ctx, 0, "UDim -") - ReadUDim(ctx, 1, "UDim -")));
            meta[Metamethods.Unm] = Fn("UDim.__unm", ctx => Wrap(-ReadUDim(ctx, 0, "UDim -")));
            meta[Metamethods.Eq] = Fn("UDim.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxUDim a)
                && TryUnbox(Arg(ctx, 1), out RbxUDim b) && a == b);
            meta[Metamethods.ToString] = Fn("UDim.__tostring",
                ctx => ReadUDim(ctx, 0, "UDim tostring").ToString());
            return Lock(meta);
        }

        private static RbxUDim ReadUDim(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxUDim udim))
            {
                return udim;
            }

            throw RbxError.BadArgument(
                what + " expects a UDim at argument " + (index + 1),
                "pass a UDim, got " + Describe(value) + " at argument " + (index + 1));
        }

        private static LuaTable BuildUDim2Meta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Lerp"] = new LuaValue(Fn("UDim2.Lerp", ctx => Wrap(
                    ReadUDim2(ctx, 0, "UDim2:Lerp").Lerp(
                        ReadUDim2(ctx, 1, "UDim2:Lerp", 1), ReadFloat(ctx, 2, "UDim2:Lerp", 2)))))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("UDim2.__index", ctx =>
            {
                RbxUDim2 self = ReadUDim2(ctx, 0, "UDim2 method");
                string key = ReadString(ctx, 1, "UDim2 member access");
                switch (key)
                {
                    case "X": return Wrap(self.X);
                    case "Y": return Wrap(self.Y);
                    case "Width": return Wrap(self.Width);
                    case "Height": return Wrap(self.Height);
                    default:
                        return methods.TryGetValue(key, out LuaValue method)
                            ? method
                            : throw NotAMember(key, "UDim2");
                }
            });
            meta[Metamethods.NewIndex] = Fn("UDim2.__newindex", _ => throw ReadOnlyMember("UDim2"));
            meta[Metamethods.Add] = Fn("UDim2.__add", ctx =>
                Wrap(ReadUDim2(ctx, 0, "UDim2 +") + ReadUDim2(ctx, 1, "UDim2 +")));
            meta[Metamethods.Sub] = Fn("UDim2.__sub", ctx =>
                Wrap(ReadUDim2(ctx, 0, "UDim2 -") - ReadUDim2(ctx, 1, "UDim2 -")));
            meta[Metamethods.Unm] = Fn("UDim2.__unm", ctx => Wrap(-ReadUDim2(ctx, 0, "UDim2 -")));
            meta[Metamethods.Eq] = Fn("UDim2.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxUDim2 a)
                && TryUnbox(Arg(ctx, 1), out RbxUDim2 b) && a == b);
            meta[Metamethods.ToString] = Fn("UDim2.__tostring",
                ctx => ReadUDim2(ctx, 0, "UDim2 tostring").ToString());
            return Lock(meta);
        }

        private static RbxUDim2 ReadUDim2(LuaFunctionExecutionContext ctx, int index, string what)
        {
            return ReadUDim2(ctx, index, what, index + 1);
        }

        private static RbxUDim2 ReadUDim2(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxUDim2 udim2))
            {
                return udim2;
            }

            throw ExpectedArgument(what, "a UDim2", value, argumentNumber);
        }

        /// <summary>
        /// Reads a UDim offset (optional, default 0). Roblox stores offsets as 32-bit integers; a
        /// fractional offset truncates toward zero, and a non-finite or out-of-range one is a
        /// BAD_ARGUMENT instead of a platform-dependent cast.
        /// </summary>
        /// <remarks>
        /// WHY not a bare (int) cast: casting NaN or 1e10 to int is unspecified in C#, and x64
        /// yields int.MinValue while ARM64 saturates, so one script produced different layouts on
        /// desktop and on mobile.
        /// </remarks>
        private static int ReadOffset(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return 0;
            }

            if (!TryCoerceNumber(value, out double number))
            {
                throw ExpectedArgument(what, "a number", value, argumentNumber);
            }

            double truncated = Math.Truncate(number);
            if (double.IsNaN(number) || truncated < int.MinValue || truncated > int.MaxValue)
            {
                throw RbxError.BadArgument(
                    what + " expects a finite offset in the 32-bit integer range at argument "
                         + argumentNumber,
                    "pass a whole pixel offset between " + int.MinValue + " and " + int.MaxValue
                    + ", got " + number.ToString(CultureInfo.InvariantCulture)
                    + " at argument " + argumentNumber);
            }

            return (int)truncated;
        }

        // ---- Random -------------------------------------------------------------------------

        private static LuaTable BuildRandomMeta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["NextNumber"] = new LuaValue(Fn("Random.NextNumber", ctx =>
                {
                    RbxRandom self = SelfRandom(ctx);
                    return ctx.ArgumentCount >= 3
                        ? self.NextNumber(
                            ReadDouble(ctx, 1, "Random:NextNumber", 1),
                            ReadDouble(ctx, 2, "Random:NextNumber", 2))
                        : self.NextNumber();
                })),
                ["NextInteger"] = new LuaValue(Fn("Random.NextInteger", ctx =>
                    (double)SelfRandom(ctx).NextInteger(
                        ReadWholeNumber(ctx, 1, "Random:NextInteger", 1),
                        ReadWholeNumber(ctx, 2, "Random:NextInteger", 2)))),
                ["NextUnitVector"] = new LuaValue(Fn("Random.NextUnitVector",
                    ctx => Wrap(SelfRandom(ctx).NextUnitVector()))),
                ["Clone"] = new LuaValue(Fn("Random.Clone", ctx => Wrap(SelfRandom(ctx).Clone()))),
                ["Shuffle"] = new LuaValue(Fn("Random.Shuffle", ctx =>
                {
                    RbxRandom self = SelfRandom(ctx);
                    LuaValue arg = Arg(ctx, 1);
                    if (arg.Type != LuaValueType.Table)
                    {
                        throw RbxError.BadArgument(
                            "Random:Shuffle expects a table at argument 1",
                            "pass an array-like table, got " + Describe(arg) + " at argument 1");
                    }

                    LuaTable table = arg.Read<LuaTable>();
                    // WHY: Fisher-Yates over the array part, driven by the same deterministic stream
                    // as NextInteger so seeded shuffles reproduce.
                    for (int i = table.ArrayLength; i >= 2; i--)
                    {
                        int j = (int)self.NextInteger(1, i);
                        (table[i], table[j]) = (table[j], table[i]);
                    }

                    return LuaValue.Nil;
                }))
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Random.__index", ctx =>
            {
                string key = ReadString(ctx, 1, "Random member access");
                return methods.TryGetValue(key, out LuaValue method)
                    ? method
                    : throw NotAMember(key, "Random");
            });
            meta[Metamethods.NewIndex] = Fn("Random.__newindex", _ => throw ReadOnlyMember("Random"));
            meta[Metamethods.ToString] = Fn("Random.__tostring", _ => "Random");
            return Lock(meta);
        }

        /// <summary>Luau's exact-integer range: every whole number in it is a distinct double.</summary>
        private const double MaxSafeInteger = 9007199254740991d;

        /// <summary>
        /// Reads a Random bound: truncated toward zero (the mirror's Random:NextInteger rule), and a
        /// non-finite value or one outside the exact-integer range is a BAD_ARGUMENT instead of an
        /// unspecified double-to-long cast.
        /// </summary>
        private static long ReadWholeNumber(LuaFunctionExecutionContext ctx, int index, string what,
            int argumentNumber)
        {
            double number = ReadDouble(ctx, index, what, argumentNumber);
            double truncated = Math.Truncate(number);
            if (double.IsNaN(number) || truncated < -MaxSafeInteger || truncated > MaxSafeInteger)
            {
                throw RbxError.BadArgument(
                    what + " expects a finite whole number within +/-" + MaxSafeInteger.ToString("R",
                        CultureInfo.InvariantCulture) + " at argument " + argumentNumber,
                    "pass an integer bound, got " + number.ToString(CultureInfo.InvariantCulture)
                    + " at argument " + argumentNumber);
            }

            return (long)truncated;
        }

        /// <summary>
        /// Random.new(seed): the mirror says a seed outside [-9007199254740991, 9007199254740991]
        /// is clamped to 0; NaN is treated the same, since no whole number can be read from it.
        /// </summary>
        private static double ClampRandomSeed(double seed)
        {
            return double.IsNaN(seed) || seed < -MaxSafeInteger || seed > MaxSafeInteger ? 0d : seed;
        }

        private static RbxRandom SelfRandom(LuaFunctionExecutionContext ctx)
        {
            if (TryUnbox(Arg(ctx, 0), out RbxRandom random))
            {
                return random;
            }

            throw RbxError.BadArgument(
                "Random method expects a Random as self",
                "call methods with a colon, e.g. rng:NextNumber()");
        }

        // ---- Enum ---------------------------------------------------------------------------

        private static LuaTable BuildEnumItemMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("EnumItem.__index", ctx =>
            {
                RbxEnumItem self = ReadEnumItem(ctx, 0, "EnumItem member access");
                string key = ReadString(ctx, 1, "EnumItem member access");
                switch (key)
                {
                    case "Name": return self.Name;
                    case "Value": return self.Value;
                    case "EnumType": return Wrap(self.EnumType);
                    default: throw NotAMember(key, "EnumItem");
                }
            });
            meta[Metamethods.NewIndex] = Fn("EnumItem.__newindex",
                _ => throw ReadOnlyMember("EnumItem"));
            meta[Metamethods.Eq] = Fn("EnumItem.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxEnumItem a)
                && TryUnbox(Arg(ctx, 1), out RbxEnumItem b) && ReferenceEquals(a, b));
            meta[Metamethods.ToString] = Fn("EnumItem.__tostring",
                ctx => ReadEnumItem(ctx, 0, "EnumItem tostring").ToString());
            return Lock(meta);
        }

        private static readonly LuaValue EnumGetEnumItemsFn = new(Fn("Enum.GetEnumItems", ctx =>
        {
            RbxEnum target = ReadEnumType(ctx, 0, "Enum:GetEnumItems");
            LuaTable list = new();
            int index = 1;
            foreach (RbxEnumItem item in target.GetEnumItems())
            {
                list[index++] = Wrap(item);
            }

            return new LuaValue(list);
        }));

        private static readonly LuaValue EnumFromNameFn = new(Fn("Enum.FromName", ctx =>
        {
            RbxEnum target = ReadEnumType(ctx, 0, "Enum:FromName");
            string name = ReadString(ctx, 1, "Enum:FromName", 1);
            return target.TryGetItem(name, out RbxEnumItem item) ? Wrap(item) : LuaValue.Nil;
        }));

        private static readonly LuaValue EnumFromValueFn = new(Fn("Enum.FromValue", ctx =>
        {
            RbxEnum target = ReadEnumType(ctx, 0, "Enum:FromValue");
            double value = ReadDouble(ctx, 1, "Enum:FromValue", 1);
            // WHY: every item value is an int, so a fractional or out-of-range number names no item
            // and answers nil, exactly like an unused whole value.
            if (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue)
            {
                return LuaValue.Nil;
            }

            return target.TryGetItemByValue((int)value, out RbxEnumItem item) ? Wrap(item) : LuaValue.Nil;
        }));

        private static LuaTable BuildEnumTypeMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Enum.__index", ctx =>
            {
                RbxEnum self = ReadEnumType(ctx, 0, "Enum member access");
                string key = ReadString(ctx, 1, "Enum member access");
                switch (key)
                {
                    case "GetEnumItems": return EnumGetEnumItemsFn;
                    case "FromName": return EnumFromNameFn;
                    case "FromValue": return EnumFromValueFn;
                    default: return Wrap(self[key]);
                }
            });
            meta[Metamethods.NewIndex] = Fn("Enum.__newindex", _ => throw ReadOnlyMember("Enum"));
            meta[Metamethods.Eq] = Fn("Enum.__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out RbxEnum a)
                && TryUnbox(Arg(ctx, 1), out RbxEnum b) && ReferenceEquals(a, b));
            meta[Metamethods.ToString] = Fn("Enum.__tostring",
                ctx => ReadEnumType(ctx, 0, "Enum tostring").ToString());
            return Lock(meta);
        }

        private static RbxEnumItem ReadEnumItem(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxEnumItem item))
            {
                return item;
            }

            throw RbxError.BadArgument(
                what + " expects an EnumItem at argument " + (index + 1),
                "pass an Enum item like Enum.Material.Wood, got " + Describe(value)
                                                                  + " at argument " + (index + 1));
        }

        private static RbxEnum ReadEnumType(LuaFunctionExecutionContext ctx, int index, string what)
        {
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxEnum enumType))
            {
                return enumType;
            }

            throw RbxError.BadArgument(
                what + " expects an Enum at argument " + (index + 1),
                "pass an Enum type like Enum.Material, got " + Describe(value)
                                                             + " at argument " + (index + 1));
        }

        private static RbxRotationOrder ReadRotationOrder(LuaFunctionExecutionContext ctx, int index)
        {
            return ReadRotationOrder(ctx, index, "CFrame.fromEulerAngles", index + 1);
        }

        private static RbxRotationOrder ReadRotationOrder(LuaFunctionExecutionContext ctx, int index,
            string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return RbxRotationOrder.XYZ;
            }

            if (TryUnbox(value, out RbxEnumItem item) && item.EnumType.Name == "RotationOrder"
                                                      && Enum.TryParse(item.Name, out RbxRotationOrder order))
            {
                return order;
            }

            throw RbxError.BadArgument(
                what + " expects an Enum.RotationOrder at argument " + argumentNumber,
                "pass Enum.RotationOrder.XYZ (or another order) at argument " + argumentNumber);
        }

        private static readonly LuaValue SignalConnectFn =
            new(Fn("RBXScriptSignal.Connect", inner => ConnectSignal(inner, false, "Connect")));

        private static readonly LuaValue SignalOnceFn =
            new(Fn("RBXScriptSignal.Once", inner => ConnectSignal(inner, true, "Once")));

        // WHY: DEV-5. CoreAI mods run single-threaded, so a desynchronized phase does not exist and
        // ConnectParallel is Connect. Refusing it would fail working Parallel Luau code, which is the
        // same reasoning that makes task.synchronize/desynchronize no-ops.
        private static readonly LuaValue SignalConnectParallelFn =
            new(Fn("RBXScriptSignal.ConnectParallel",
                inner => ConnectSignal(inner, false, "ConnectParallel")));

        /// <summary>Mod contexts already told that ConnectParallel runs serially (once per mod load).</summary>
        private static readonly ConditionalWeakTable<LuaCsRbxModContext, object> ParallelConnectNoted =
            new();

        private static readonly object ParallelConnectNoteMarker = new();

        /// <summary>Reference identity for tables, so a payload copy maps each source table once.</summary>
        private sealed class TableIdentityComparer : IEqualityComparer<LuaTable>
        {
            public static readonly TableIdentityComparer Instance = new();

            public bool Equals(LuaTable left, LuaTable right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(LuaTable value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        private static readonly LuaValue ConnectionDisconnectFn =
            new(Fn("RBXScriptConnection.Disconnect", inner =>
            {
                ReadConnection(inner, 0).Disconnect();
                return LuaValue.Nil;
            }));

        private static LuaTable BuildSignalMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("RBXScriptSignal.__index", ctx =>
            {
                RbxScriptSignal self = ReadSignal(ctx, 0);
                string key = ReadString(ctx, 1, "RBXScriptSignal member access");
                switch (key)
                {
                    case "Connect": return SignalConnectFn;
                    case "ConnectParallel": return SignalConnectParallelFn;
                    case "Once": return SignalOnceFn;
                    case "Wait":
                        LuaCsRbxModContext owner = ReadSignalOwner(ctx);
                        if (owner != null)
                        {
                            RequirePersistentSignalOwner(self, "Wait", owner);
                        }

                        return ReadSignalWaitBridge(ctx);
                    default:
                        throw NotAMember(key, "RBXScriptSignal");
                }
            });
            meta[Metamethods.NewIndex] = Fn("RBXScriptSignal.__newindex",
                _ => throw ReadOnlyMember("RBXScriptSignal"));
            meta[Metamethods.ToString] = Fn("RBXScriptSignal.__tostring",
                ctx => "Signal " + ReadSignal(ctx, 0).SignalName);
            return Lock(meta);
        }

        private static LuaValue ConnectSignal(LuaFunctionExecutionContext ctx, bool once, string member)
        {
            RbxScriptSignal signal = ReadSignal(ctx, 0);
            LuaValue handlerValue = Arg(ctx, 1);
            if (handlerValue.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    signal.SignalName + ":" + member + " expects a function at argument 1",
                    "pass a handler function, got " + Describe(handlerValue) + " at argument 1");
            }

            LuaCsRbxModContext signalOwner = ReadSignalOwner(ctx);
            if (signalOwner == null)
            {
                throw new RbxError(
                    RbxErrorCode.ContextViolation,
                    signal.SignalName + ":" + member + " requires an owning mod context",
                    "read the signal from an Instance proxy owned by the running mod");
            }

            RequirePersistentSignalOwner(signal, member, signalOwner);
            if (member == "ConnectParallel")
            {
                NoteParallelConnect(signalOwner);
            }

            LuaState handlerState = signalOwner.Bindings.ResolveSchedulerOwnerState(ctx.State);
            object callable = signalOwner.Bindings.CaptureSignalCallable(
                handlerState, handlerValue);
            Action<object[]> wrapper = BuildSignalHandler(
                signalOwner, callable);
            RbxScriptConnection connection = once ? signal.Once(wrapper) : signal.Connect(wrapper);
            connection.StartsSchedulerThread = true;

            // WHY: attribute the connection to the mod that opened it so composition teardown can
            // Disconnect it on unload/reload/quarantine — otherwise the handler keeps firing against the
            // torn-down mod (INSTANCE_DESTROYED). A context-free wrap or a mod-less one-off records nothing.
            signalOwner.TrackConnection(connection);

            return Wrap(connection);
        }

        private static LuaCsRbxModContext ReadSignalOwner(LuaFunctionExecutionContext ctx)
        {
            return Arg(ctx, 0).TryRead(out LuaCsRbxValueBox signalBox) ? signalBox.SignalOwner : null;
        }

        /// <summary>
        /// Refuses a connection or wait from the ownerless one-off surface (execute_lua) with the
        /// same CONTEXT_VIOLATION task.* raises there.
        /// </summary>
        /// <remarks>
        /// WHY refuse instead of connecting: an ownerless connection has no mod to track it under, so
        /// nothing ever disconnects it, and every later fire spawns its handler through the task
        /// scheduler, which demands an owning mod id and throws. That throw escaped the frame on every
        /// fire, for every mod, until the world was reloaded — with no mod to unload or quarantine.
        /// </remarks>
        private static void RequirePersistentSignalOwner(RbxScriptSignal signal, string member,
            LuaCsRbxModContext owner)
        {
            if (!string.IsNullOrWhiteSpace(owner.OwnerModId))
            {
                return;
            }

            throw new RbxError(
                RbxErrorCode.ContextViolation,
                signal.SignalName + ":" + member + " requires a persistent owning mod id",
                "run signal:" + member + " from a loaded mod instead of the ownerless one-off executor");
        }

        private static void NoteParallelConnect(LuaCsRbxModContext owner)
        {
            if (ParallelConnectNoted.TryGetValue(owner, out object _))
            {
                return;
            }

            ParallelConnectNoted.Add(owner, ParallelConnectNoteMarker);
            owner.Bindings?.LogSink?.Invoke(
                "[RbxApi] RBXScriptSignal:ConnectParallel runs its handler like Connect: CoreAI mods " +
                "run single-threaded, so there is no desynchronized phase (DEV-5). (Logged once per mod.)");
        }

        private static Action<object[]> BuildSignalHandler(
            LuaCsRbxModContext context, object callable)
        {
            return args =>
            {
                object[] luaArgs = new object[args.Length];
                for (int index = 0; index < args.Length; index++)
                {
                    luaArgs[index] = MarshalSignalArg(context, args[index]);
                }

                context.Bindings.SpawnSignalHandler(context, callable, luaArgs);
            };
        }

        /// <summary>
        /// Converts one fired signal argument into the receiving handler's Lua value. Called once
        /// per receiving handler, so every table a handler receives is its own copy.
        /// </summary>
        /// <remarks>
        /// WHY copy: one fire reaches every connected handler — several mods, and on a multi-actor
        /// host several actors — with the same argument array. A shared table let one receiver
        /// rewrite a field or install a metatable whose closure then ran on another actor's thread
        /// and budget. R5.10 (Roblox's bindable rule) says tables passed as arguments are copied and
        /// lose their metatable; remote payloads are decoded per machine in Roblox, so a per-handler
        /// copy is what each receiver would see there too.
        /// </remarks>
        internal static LuaValue MarshalSignalArg(LuaCsRbxModContext context, object arg)
        {
            switch (arg)
            {
                case null: return LuaValue.Nil;
                case LuaValue value: return CopySignalValue(context, value);
                case bool b: return b;
                case double d: return d;
                case float f: return f;
                case int i: return i;
                // WHY: slice 8.1 — IntValue holds int64 but Luau numbers are doubles (the
                // mirror documents precision loss past 2^53); CFrame/Color3 values cross as
                // their datatype userdata like Vector3 already does.
                case long l: return (double)l;
                case RbxCFrame cf: return Wrap(cf);
                case RbxColor3 c3: return Wrap(c3);
                case string s: return s;
                case RbxInstance instance: return context.WrapInstance(instance);
                case RbxInputObject input: return Wrap(input);
                case RbxEnumItem item: return Wrap(item);
                case RbxVector3 v3: return Wrap(v3);
                case RbxVector2 v2: return Wrap(v2);
                case RbxUDim udim: return Wrap(udim);
                case RbxUDim2 udim2: return Wrap(udim2);
                case LuaCsRbxNetworkTable table: return BuildSignalTable(context, table, 0);
                default: return LuaValue.Nil;
            }
        }

        /// <summary>
        /// A decoded remote payload table rebuilt as a fresh Lua table for one receiver, with
        /// Instance references wrapped for the receiving context.
        /// </summary>
        private static LuaValue BuildSignalTable(LuaCsRbxModContext context,
            LuaCsRbxNetworkTable portable, int depth)
        {
            RequireSignalTableDepth(depth);
            LuaTable table = new();
            if (portable.IsArray)
            {
                for (int index = 0; index < portable.ArrayValues.Count; index++)
                {
                    table[index + 1] = MarshalNetworkValue(context, portable.ArrayValues[index], depth);
                }
            }
            else
            {
                for (int index = 0; index < portable.DictionaryValues.Count; index++)
                {
                    KeyValuePair<string, object> pair = portable.DictionaryValues[index];
                    table[pair.Key] = MarshalNetworkValue(context, pair.Value, depth);
                }
            }

            return new LuaValue(table);
        }

        private static LuaValue MarshalNetworkValue(LuaCsRbxModContext context, object value, int depth)
        {
            return value is LuaCsRbxNetworkTable nested
                ? BuildSignalTable(context, nested, depth + 1)
                : MarshalSignalArg(context, value);
        }

        private static LuaValue CopySignalValue(LuaCsRbxModContext context, LuaValue value)
        {
            if (value.Type != LuaValueType.Table)
            {
                return RebindSignalValue(context, value);
            }

            Dictionary<LuaTable, LuaTable> copies = new(TableIdentityComparer.Instance);
            return new LuaValue(CopySignalTable(context, value.Read<LuaTable>(), copies, 0));
        }

        /// <summary>
        /// Deep-copies a table argument: no metatable, shared or cyclic subtables mapped to one copy
        /// each (so a cycle terminates and stays a cycle), nesting capped like the network codec.
        /// Functions and coroutines inside the table are dropped: calling one would run the firing
        /// mod's code on the receiver's thread and budget.
        /// </summary>
        private static LuaTable CopySignalTable(LuaCsRbxModContext context, LuaTable source,
            Dictionary<LuaTable, LuaTable> copies, int depth)
        {
            if (copies.TryGetValue(source, out LuaTable existing))
            {
                return existing;
            }

            RequireSignalTableDepth(depth);
            LuaTable copy = new();
            copies.Add(source, copy);
            foreach (KeyValuePair<LuaValue, LuaValue> pair in source)
            {
                LuaValue key = CopyNestedSignalValue(context, pair.Key, copies, depth);
                if (key.Type == LuaValueType.Nil)
                {
                    continue;
                }

                copy[key] = CopyNestedSignalValue(context, pair.Value, copies, depth);
            }

            return copy;
        }

        private static LuaValue CopyNestedSignalValue(LuaCsRbxModContext context, LuaValue value,
            Dictionary<LuaTable, LuaTable> copies, int depth)
        {
            switch (value.Type)
            {
                case LuaValueType.Table:
                    return new LuaValue(CopySignalTable(context, value.Read<LuaTable>(), copies, depth + 1));
                case LuaValueType.Function:
                case LuaValueType.Thread:
                    return LuaValue.Nil;
                default:
                    return RebindSignalValue(context, value);
            }
        }

        /// <summary>
        /// Re-wraps reference-carrying userdata for the receiving context: an Instance proxy or a
        /// signal box built for another mod would otherwise act with that mod's capabilities and
        /// connection ownership.
        /// </summary>
        private static LuaValue RebindSignalValue(LuaCsRbxModContext context, LuaValue value)
        {
            if (context == null)
            {
                return value;
            }

            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                return ReferenceEquals(proxy.Context, context) ? value : context.WrapInstance(proxy.Instance);
            }

            if (value.TryRead(out LuaCsRbxValueBox box) && box.SignalOwner != null
                                                       && !ReferenceEquals(box.SignalOwner, context)
                                                       && box.Value is RbxScriptSignal signal)
            {
                return Wrap(signal, context);
            }

            return value;
        }

        private static void RequireSignalTableDepth(int depth)
        {
            if (depth >= LuaCsRbxNetworkCodec.MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "signal argument table nesting exceeds CoreAI's "
                    + LuaCsRbxNetworkCodec.MaxNestingDepth + " level limit",
                    "fire shallower tables; remote payloads are capped at the same depth");
            }
        }

        private static LuaValue ReadSignalWaitBridge(LuaFunctionExecutionContext ctx)
        {
            LuaValue taskValue = ctx.State.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    "RBXScriptSignal.Wait requires the task scheduler bridge",
                    "run signal:Wait() from a loaded mod scheduler thread");
            }

            LuaValue bridge = taskValue.Read<LuaTable>()["_signalWaitBridge"];
            if (bridge.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "RBXScriptSignal.Wait bridge is unavailable",
                    "run signal:Wait() after the mod scheduler initializes");
            }

            return bridge;
        }

        private static RbxScriptSignal ReadSignal(LuaFunctionExecutionContext ctx, int index)
        {
            if (TryUnbox(Arg(ctx, index), out RbxScriptSignal signal))
            {
                return signal;
            }

            throw RbxError.BadArgument(
                "signal method expects an RBXScriptSignal as self",
                "call signal methods with a colon, e.g. part.ChildAdded:Connect(fn)");
        }

        // ---- RBXScriptConnection -------------------------------------------------------------

        private static LuaTable BuildConnectionMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("RBXScriptConnection.__index", ctx =>
            {
                RbxScriptConnection self = ReadConnection(ctx, 0);
                string key = ReadString(ctx, 1, "RBXScriptConnection member access");
                switch (key)
                {
                    case "Connected": return self.Connected;
                    case "Disconnect": return ConnectionDisconnectFn;
                    default:
                        throw NotAMember(key, "RBXScriptConnection");
                }
            });
            meta[Metamethods.NewIndex] = Fn("RBXScriptConnection.__newindex",
                _ => throw ReadOnlyMember("RBXScriptConnection"));
            meta[Metamethods.ToString] = Fn("RBXScriptConnection.__tostring", _ => "Connection");
            return Lock(meta);
        }

        private static RbxScriptConnection ReadConnection(LuaFunctionExecutionContext ctx, int index)
        {
            if (TryUnbox(Arg(ctx, index), out RbxScriptConnection connection))
            {
                return connection;
            }

            throw RbxError.BadArgument(
                "connection method expects an RBXScriptConnection as self",
                "call connection methods with a colon, e.g. connection:Disconnect()");
        }

        // ---- InputObject ---------------------------------------------------------------------

        private static LuaTable BuildInputObjectMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("InputObject.__index", ctx =>
            {
                RbxInputObject self = ReadInputObject(ctx, 0);
                string key = ReadString(ctx, 1, "InputObject member access");
                switch (key)
                {
                    case "KeyCode": return WrapOrNil(self.KeyCode);
                    case "UserInputType": return WrapOrNil(self.UserInputType);
                    case "UserInputState": return WrapOrNil(self.UserInputState);
                    case "Position": return Wrap(self.Position);
                    case "Delta": return Wrap(self.Delta);
                    default:
                        throw NotAMember(key, "InputObject");
                }
            });
            meta[Metamethods.NewIndex] = Fn("InputObject.__newindex",
                _ => throw ReadOnlyMember("InputObject"));
            meta[Metamethods.ToString] = Fn("InputObject.__tostring", _ => "InputObject");
            return Lock(meta);
        }

        private static LuaValue WrapOrNil(RbxEnumItem item)
        {
            return item != null ? Wrap(item) : LuaValue.Nil;
        }

        private static RbxInputObject ReadInputObject(LuaFunctionExecutionContext ctx, int index)
        {
            if (TryUnbox(Arg(ctx, index), out RbxInputObject input))
            {
                return input;
            }

            throw RbxError.BadArgument(
                "InputObject member access expects an InputObject as self",
                "read fields off the InputObject the input signal passed to your handler");
        }

        // ---- Shared errors ------------------------------------------------------------------

        private static RbxError NotAMember(string key, string typeName)
        {
            return RbxError.BadArgument(
                key + " is not a valid member of " + typeName,
                "check the " + typeName + " member list in the Roblox API reference");
        }

        private static RbxError ReadOnlyMember(string typeName)
        {
            return RbxError.BadArgument(
                typeName + " values are immutable",
                "construct a new " + typeName + " instead of mutating one");
        }

        private static RbxVector3? OptionalVector3(LuaFunctionExecutionContext ctx, int index)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            return TryUnbox(value, out RbxVector3 vector)
                ? vector
                : throw RbxError.BadArgument(
                    "expected a Vector3 at argument " + (index + 1),
                    "pass a Vector3, got " + Describe(value) + " at argument " + (index + 1));
        }

        private static RbxVector3? OptionalVector3(LuaFunctionExecutionContext ctx, int index,
            string what, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            return TryUnbox(value, out RbxVector3 vector)
                ? vector
                : throw ExpectedArgument(what, "a Vector3", value, argumentNumber);
        }

        /// <summary>
        /// Optional boolean argument, read by the one boolean rule of both script surfaces: only true and
        /// false are booleans, nil is <paramref name="whenOmitted"/> (the parameter's documented default),
        /// and any other value is a BAD_ARGUMENT. Lua truthiness is never used: it turned "false" and 0
        /// into true (C1-05).
        /// </summary>
        internal static bool ReadOptionalBoolean(LuaFunctionExecutionContext ctx, int index,
            string what, int argumentNumber, bool whenOmitted = false)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return whenOmitted;
            }

            if (value.Type != LuaValueType.Boolean)
            {
                throw ExpectedArgument(what, "a boolean", value, argumentNumber);
            }

            return value.Read<bool>();
        }
    }
}
