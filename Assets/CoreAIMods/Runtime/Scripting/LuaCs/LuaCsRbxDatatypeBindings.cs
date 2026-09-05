using System;
using System.Collections.Generic;
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
                ReadFloatOr(ctx, 0, 0f), ReadFloatOr(ctx, 1, 0f), ReadFloatOr(ctx, 2, 0f))));
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
                ReadFloatOr(ctx, 0, 0f), ReadFloatOr(ctx, 1, 0f))));
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
            return new LuaValue(t);
        }

        public static LuaValue BuildColor3Global()
        {
            LuaTable t = new();
            t["new"] = Fn("Color3.new", ctx => Wrap(new RbxColor3(
                ReadFloatOr(ctx, 0, 0f), ReadFloatOr(ctx, 1, 0f), ReadFloatOr(ctx, 2, 0f))));
            t["fromRGB"] = Fn("Color3.fromRGB", ctx => Wrap(RbxColor3.FromRGB(
                ReadFloatOr(ctx, 0, 0f), ReadFloatOr(ctx, 1, 0f), ReadFloatOr(ctx, 2, 0f))));
            t["fromHSV"] = Fn("Color3.fromHSV", ctx => Wrap(RbxColor3.FromHSV(
                ReadFloat(ctx, 0, "Color3.fromHSV"),
                ReadFloat(ctx, 1, "Color3.fromHSV"),
                ReadFloat(ctx, 2, "Color3.fromHSV"))));
            t["fromHex"] = Fn("Color3.fromHex",
                ctx => Wrap(RbxColor3.FromHex(ReadString(ctx, 0, "Color3.fromHex"))));
            return new LuaValue(t);
        }

        public static LuaValue BuildUDimGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("UDim.new", ctx => Wrap(new RbxUDim(
                ReadFloatOr(ctx, 0, 0f), (int)ReadFloatOr(ctx, 1, 0f))));
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
                    ReadFloatOr(ctx, 0, 0f), (int)ReadFloatOr(ctx, 1, 0f),
                    ReadFloatOr(ctx, 2, 0f), (int)ReadFloatOr(ctx, 3, 0f)));
            });
            t["fromScale"] = Fn("UDim2.fromScale", ctx => Wrap(RbxUDim2.FromScale(
                ReadFloatOr(ctx, 0, 0f), ReadFloatOr(ctx, 1, 0f))));
            t["fromOffset"] = Fn("UDim2.fromOffset", ctx => Wrap(RbxUDim2.FromOffset(
                (int)ReadFloatOr(ctx, 0, 0f), (int)ReadFloatOr(ctx, 1, 0f))));
            return new LuaValue(t);
        }

        public static LuaValue BuildRandomGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("Random.new", ctx => Arg(ctx, 0).Type == LuaValueType.Number
                ? Wrap(new RbxRandom(ReadDouble(ctx, 0, "Random.new")))
                : Wrap(new RbxRandom()));
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

            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for time at argument 1",
                    "pass a duration in seconds, got " + Describe(value) + " at argument 1");
            }

            double time = value.Read<double>();
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

            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for delayTime at argument 6",
                    "pass a delay in seconds, got " + Describe(value) + " at argument 6");
            }

            double delay = value.Read<double>();
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

            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    "TweenInfo.new expects a number for repeatCount at argument 4",
                    "pass an integer repeat count, got " + Describe(value) + " at argument 4");
            }

            double count = value.Read<double>();
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
                    Self3(ctx).Dot(ReadVector3(ctx, 1, "Vector3:Dot")))),
                ["Cross"] = new LuaValue(Fn("Vector3.Cross", ctx =>
                    Wrap(Self3(ctx).Cross(ReadVector3(ctx, 1, "Vector3:Cross"))))),
                ["Lerp"] = new LuaValue(Fn("Vector3.Lerp", ctx => Wrap(Self3(ctx).Lerp(
                    ReadVector3(ctx, 1, "Vector3:Lerp"), ReadFloat(ctx, 2, "Vector3:Lerp"))))),
                ["Angle"] = new LuaValue(Fn("Vector3.Angle", ctx => Self3(ctx).Angle(
                    ReadVector3(ctx, 1, "Vector3:Angle"), OptionalVector3(ctx, 2)))),
                ["FuzzyEq"] = new LuaValue(Fn("Vector3.FuzzyEq", ctx => Self3(ctx).FuzzyEq(
                    ReadVector3(ctx, 1, "Vector3:FuzzyEq"), ReadFloatOr(ctx, 2, 1e-5f)))),
                ["Abs"] = new LuaValue(Fn("Vector3.Abs", ctx => Wrap(Self3(ctx).Abs()))),
                ["Ceil"] = new LuaValue(Fn("Vector3.Ceil", ctx => Wrap(Self3(ctx).Ceil()))),
                ["Floor"] = new LuaValue(Fn("Vector3.Floor", ctx => Wrap(Self3(ctx).Floor()))),
                ["Sign"] = new LuaValue(Fn("Vector3.Sign", ctx => Wrap(Self3(ctx).Sign()))),
                ["Max"] = new LuaValue(Fn("Vector3.Max", ctx =>
                    Wrap(Self3(ctx).Max(ReadVector3(ctx, 1, "Vector3:Max"))))),
                ["Min"] = new LuaValue(Fn("Vector3.Min", ctx =>
                    Wrap(Self3(ctx).Min(ReadVector3(ctx, 1, "Vector3:Min")))))
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
            meta[Metamethods.Mul] = Fn("Vector3.__mul", ctx =>
            {
                LuaValue a = Arg(ctx, 0);
                LuaValue b = Arg(ctx, 1);
                if (a.Type == LuaValueType.Number)
                {
                    return Wrap((float)a.Read<double>() * ReadVector3(ctx, 1, "Vector3 *"));
                }

                RbxVector3 left = ReadVector3(ctx, 0, "Vector3 *");
                return b.Type == LuaValueType.Number
                    ? Wrap(left * (float)b.Read<double>())
                    : Wrap(left * ReadVector3(ctx, 1, "Vector3 *"));
            });
            meta[Metamethods.Div] = Fn("Vector3.__div", ctx =>
            {
                RbxVector3 left = ReadVector3(ctx, 0, "Vector3 /");
                LuaValue b = Arg(ctx, 1);
                return b.Type == LuaValueType.Number
                    ? Wrap(left / (float)b.Read<double>())
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
                    Self2(ctx).Dot(ReadVector2(ctx, 1, "Vector2:Dot")))),
                ["Cross"] = new LuaValue(Fn("Vector2.Cross", ctx =>
                    Self2(ctx).Cross(ReadVector2(ctx, 1, "Vector2:Cross")))),
                ["Lerp"] = new LuaValue(Fn("Vector2.Lerp", ctx => Wrap(Self2(ctx).Lerp(
                    ReadVector2(ctx, 1, "Vector2:Lerp"), ReadFloat(ctx, 2, "Vector2:Lerp"))))),
                ["FuzzyEq"] = new LuaValue(Fn("Vector2.FuzzyEq", ctx => Self2(ctx).FuzzyEq(
                    ReadVector2(ctx, 1, "Vector2:FuzzyEq"), ReadFloatOr(ctx, 2, 1e-5f)))),
                ["Abs"] = new LuaValue(Fn("Vector2.Abs", ctx => Wrap(Self2(ctx).Abs()))),
                ["Ceil"] = new LuaValue(Fn("Vector2.Ceil", ctx => Wrap(Self2(ctx).Ceil()))),
                ["Floor"] = new LuaValue(Fn("Vector2.Floor", ctx => Wrap(Self2(ctx).Floor()))),
                ["Sign"] = new LuaValue(Fn("Vector2.Sign", ctx => Wrap(Self2(ctx).Sign()))),
                ["Max"] = new LuaValue(Fn("Vector2.Max", ctx =>
                    Wrap(Self2(ctx).Max(ReadVector2(ctx, 1, "Vector2:Max"))))),
                ["Min"] = new LuaValue(Fn("Vector2.Min", ctx =>
                    Wrap(Self2(ctx).Min(ReadVector2(ctx, 1, "Vector2:Min")))))
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
            meta[Metamethods.Mul] = Fn("Vector2.__mul", ctx =>
            {
                LuaValue a = Arg(ctx, 0);
                LuaValue b = Arg(ctx, 1);
                if (a.Type == LuaValueType.Number)
                {
                    return Wrap((float)a.Read<double>() * ReadVector2(ctx, 1, "Vector2 *"));
                }

                RbxVector2 left = ReadVector2(ctx, 0, "Vector2 *");
                return b.Type == LuaValueType.Number
                    ? Wrap(left * (float)b.Read<double>())
                    : Wrap(left * ReadVector2(ctx, 1, "Vector2 *"));
            });
            meta[Metamethods.Div] = Fn("Vector2.__div", ctx =>
            {
                RbxVector2 left = ReadVector2(ctx, 0, "Vector2 /");
                LuaValue b = Arg(ctx, 1);
                return b.Type == LuaValueType.Number
                    ? Wrap(left / (float)b.Read<double>())
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
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxVector2 vector))
            {
                return vector;
            }

            throw RbxError.BadArgument(
                what + " expects a Vector2 at argument " + (index + 1),
                "pass a Vector2, got " + Describe(value) + " at argument " + (index + 1));
        }

        // ---- CFrame -------------------------------------------------------------------------

        private static LuaTable BuildCFrameMeta()
        {
            Dictionary<string, LuaValue> methods = new(StringComparer.Ordinal)
            {
                ["Inverse"] = new LuaValue(Fn("CFrame.Inverse", ctx => Wrap(SelfCf(ctx).Inverse()))),
                ["ToWorldSpace"] = new LuaValue(Fn("CFrame.ToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).ToWorldSpace(ReadCFrame(ctx, 1, "CFrame:ToWorldSpace"))))),
                ["ToObjectSpace"] = new LuaValue(Fn("CFrame.ToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).ToObjectSpace(ReadCFrame(ctx, 1, "CFrame:ToObjectSpace"))))),
                ["PointToWorldSpace"] = new LuaValue(Fn("CFrame.PointToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).PointToWorldSpace(ReadVector3(ctx, 1, "CFrame:PointToWorldSpace"))))),
                ["PointToObjectSpace"] = new LuaValue(Fn("CFrame.PointToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).PointToObjectSpace(ReadVector3(ctx, 1, "CFrame:PointToObjectSpace"))))),
                ["VectorToWorldSpace"] = new LuaValue(Fn("CFrame.VectorToWorldSpace", ctx => Wrap(
                    SelfCf(ctx).VectorToWorldSpace(ReadVector3(ctx, 1, "CFrame:VectorToWorldSpace"))))),
                ["VectorToObjectSpace"] = new LuaValue(Fn("CFrame.VectorToObjectSpace", ctx => Wrap(
                    SelfCf(ctx).VectorToObjectSpace(ReadVector3(ctx, 1, "CFrame:VectorToObjectSpace"))))),
                ["Lerp"] = new LuaValue(Fn("CFrame.Lerp", ctx => Wrap(SelfCf(ctx).Lerp(
                    ReadCFrame(ctx, 1, "CFrame:Lerp"), ReadFloat(ctx, 2, "CFrame:Lerp"))))),
                ["Orthonormalize"] = new LuaValue(Fn("CFrame.Orthonormalize",
                    ctx => Wrap(SelfCf(ctx).Orthonormalize()))),
                ["FuzzyEq"] = new LuaValue(Fn("CFrame.FuzzyEq", ctx => SelfCf(ctx).FuzzyEq(
                    ReadCFrame(ctx, 1, "CFrame:FuzzyEq"), ReadFloatOr(ctx, 2, 1e-5f)))),
                ["GetComponents"] = new LuaValue(FnMulti("CFrame.GetComponents", ctx =>
                {
                    float[] components = SelfCf(ctx).GetComponents();
                    LuaValue[] values = new LuaValue[components.Length];
                    for (int i = 0; i < components.Length; i++)
                    {
                        values[i] = components[i];
                    }

                    return values;
                }))
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
                    ReadColor3(ctx, 1, "Color3:Lerp"), ReadFloat(ctx, 2, "Color3:Lerp"))))),
                ["ToHSV"] = new LuaValue(FnMulti("Color3.ToHSV", ctx =>
                {
                    (float h, float s, float v) = SelfColor(ctx).ToHSV();
                    return new LuaValue[] { h, s, v };
                })),
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
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw RbxError.BadArgument(
                what + " expects a Color3 at argument " + (index + 1),
                "pass a Color3, got " + Describe(value) + " at argument " + (index + 1));
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
                        ReadUDim2(ctx, 1, "UDim2:Lerp"), ReadFloat(ctx, 2, "UDim2:Lerp")))))
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
            LuaValue value = Arg(ctx, index);
            if (TryUnbox(value, out RbxUDim2 udim2))
            {
                return udim2;
            }

            throw RbxError.BadArgument(
                what + " expects a UDim2 at argument " + (index + 1),
                "pass a UDim2, got " + Describe(value) + " at argument " + (index + 1));
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
                            ReadDouble(ctx, 1, "Random:NextNumber"),
                            ReadDouble(ctx, 2, "Random:NextNumber"))
                        : self.NextNumber();
                })),
                ["NextInteger"] = new LuaValue(Fn("Random.NextInteger", ctx =>
                    (double)SelfRandom(ctx).NextInteger(
                        (long)ReadDouble(ctx, 1, "Random:NextInteger"),
                        (long)ReadDouble(ctx, 2, "Random:NextInteger")))),
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

        private static LuaTable BuildEnumTypeMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("Enum.__index", ctx =>
            {
                RbxEnum self = ReadEnumType(ctx, 0, "Enum member access");
                string key = ReadString(ctx, 1, "Enum member access");
                if (key == "GetEnumItems")
                {
                    return new LuaValue(Fn("Enum.GetEnumItems", inner =>
                    {
                        RbxEnum target = ReadEnumType(inner, 0, "Enum:GetEnumItems");
                        LuaTable list = new();
                        int index = 1;
                        foreach (RbxEnumItem item in target.GetEnumItems())
                        {
                            list[index++] = Wrap(item);
                        }

                        return new LuaValue(list);
                    }));
                }

                return Wrap(self[key]);
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
                "CFrame.fromEulerAngles expects an Enum.RotationOrder at argument " + (index + 1),
                "pass Enum.RotationOrder.XYZ (or another order) at argument " + (index + 1));
        }

        private static readonly LuaValue SignalConnectFn =
            new(Fn("RBXScriptSignal.Connect", inner => ConnectSignal(inner, false)));

        private static readonly LuaValue SignalOnceFn =
            new(Fn("RBXScriptSignal.Once", inner => ConnectSignal(inner, true)));

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
                    case "Once": return SignalOnceFn;
                    case "Wait": return ReadSignalWaitBridge(ctx);
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

        private static LuaValue ConnectSignal(LuaFunctionExecutionContext ctx, bool once)
        {
            RbxScriptSignal signal = ReadSignal(ctx, 0);
            string member = once ? "Once" : "Connect";
            LuaValue handlerValue = Arg(ctx, 1);
            if (handlerValue.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    signal.SignalName + ":" + member + " expects a function at argument 1",
                    "pass a handler function, got " + Describe(handlerValue) + " at argument 1");
            }

            LuaCsRbxModContext signalOwner = null;
            if (Arg(ctx, 0).TryRead(out LuaCsRbxValueBox signalBox))
            {
                signalOwner = signalBox.SignalOwner;
            }

            if (signalOwner == null)
            {
                throw new RbxError(
                    RbxErrorCode.ContextViolation,
                    signal.SignalName + ":" + member + " requires an owning mod context",
                    "read the signal from an Instance proxy owned by the running mod");
            }

            LuaState handlerState = signalOwner.Bindings.ResolveSchedulerOwnerState(ctx.State);
            object callable = signalOwner.Bindings.CaptureSignalCallable(
                handlerState, handlerValue);
            Action<object[]> wrapper = BuildSignalHandler(
                signalOwner, callable);
            RbxScriptConnection connection = once ? signal.Once(wrapper) : signal.Connect(wrapper);

            // WHY: attribute the connection to the mod that opened it so composition teardown can
            // Disconnect it on unload/reload/quarantine — otherwise the handler keeps firing against the
            // torn-down mod (INSTANCE_DESTROYED). A context-free wrap or a mod-less one-off records nothing.
            signalOwner.TrackConnection(connection);

            return Wrap(connection);
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

        internal static LuaValue MarshalSignalArg(LuaCsRbxModContext context, object arg)
        {
            switch (arg)
            {
                case null: return LuaValue.Nil;
                case LuaValue value: return value;
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
                default: return LuaValue.Nil;
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
    }
}
