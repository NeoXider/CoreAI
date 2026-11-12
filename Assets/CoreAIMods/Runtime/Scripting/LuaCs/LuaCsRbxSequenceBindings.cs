using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua surface of the range and sequence datatypes a <c>ParticleEmitter</c> takes (MVP17): <c>NumberRange</c>,
    /// <c>NumberSequence</c>, <c>NumberSequenceKeypoint</c>, <c>ColorSequence</c> and <c>ColorSequenceKeypoint</c>, with
    /// their constructors as NumberRange.yaml, NumberSequence.yaml and ColorSequence.yaml list them, the read-only
    /// members, equality and the dump's tostring forms. The values are immutable, like every Roblox datatype.
    /// </summary>
    internal static class LuaCsRbxSequenceBindings
    {
        private static readonly LuaTable NumberRangeMeta = BuildNumberRangeMeta();
        private static readonly LuaTable NumberKeypointMeta = BuildNumberKeypointMeta();
        private static readonly LuaTable NumberSequenceMeta = BuildNumberSequenceMeta();
        private static readonly LuaTable ColorKeypointMeta = BuildColorKeypointMeta();
        private static readonly LuaTable ColorSequenceMeta = BuildColorSequenceMeta();

        public static LuaValue Wrap(RbxNumberRange value)
        {
            return Box(value, NumberRangeMeta);
        }

        public static LuaValue Wrap(RbxNumberSequenceKeypoint value)
        {
            return Box(value, NumberKeypointMeta);
        }

        public static LuaValue Wrap(RbxNumberSequence value)
        {
            return value == null ? LuaValue.Nil : Box(value, NumberSequenceMeta);
        }

        public static LuaValue Wrap(RbxColorSequenceKeypoint value)
        {
            return Box(value, ColorKeypointMeta);
        }

        public static LuaValue Wrap(RbxColorSequence value)
        {
            return value == null ? LuaValue.Nil : Box(value, ColorSequenceMeta);
        }

        // ---- constructor globals -------------------------------------------------------------

        /// <summary><c>NumberRange.new(value)</c> and <c>NumberRange.new(minimum, maximum)</c>.</summary>
        public static LuaValue BuildNumberRangeGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("NumberRange.new", ctx =>
            {
                float min = ReadFloat(ctx, 0, "NumberRange.new", 1);
                float max = Arg(ctx, 1).Type == LuaValueType.Nil ? min : ReadFloat(ctx, 1, "NumberRange.new", 2);
                if (min > max)
                {
                    throw RbxError.BadArgument("NumberRange.new: the minimum " + Format(min)
                                               + " is above the maximum " + Format(max),
                        "pass the smaller number first, e.g. NumberRange.new(1, 3)");
                }

                return Wrap(Construct(() => new RbxNumberRange(min, max), "NumberRange.new",
                    "pass finite numbers, e.g. NumberRange.new(1, 3)"));
            });
            return new LuaValue(t);
        }

        /// <summary><c>NumberSequenceKeypoint.new(time, value, envelope = 0)</c>.</summary>
        public static LuaValue BuildNumberKeypointGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("NumberSequenceKeypoint.new", ctx =>
            {
                float time = ReadFloat(ctx, 0, "NumberSequenceKeypoint.new", 1);
                float value = ReadFloat(ctx, 1, "NumberSequenceKeypoint.new", 2);
                float envelope = ReadFloatOr(ctx, 2, 0f, "NumberSequenceKeypoint.new", 3);
                return Wrap(Construct(() => new RbxNumberSequenceKeypoint(time, value, envelope),
                    "NumberSequenceKeypoint.new", "pass a time between 0 and 1 and finite numbers"));
            });
            return new LuaValue(t);
        }

        /// <summary><c>NumberSequence.new(n)</c>, <c>NumberSequence.new(n0, n1)</c> and <c>NumberSequence.new(keypoints)</c>.</summary>
        public static LuaValue BuildNumberSequenceGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("NumberSequence.new", ctx =>
            {
                LuaValue first = Arg(ctx, 0);
                if (first.TryRead(out LuaTable list))
                {
                    List<RbxNumberSequenceKeypoint> keypoints = ReadList<RbxNumberSequenceKeypoint>(list,
                        "NumberSequence.new", "NumberSequenceKeypoint");
                    return Wrap(Construct(() => new RbxNumberSequence(keypoints), "NumberSequence.new",
                        SequenceFix("NumberSequenceKeypoint.new(0, 1), NumberSequenceKeypoint.new(1, 0)")));
                }

                float start = ReadFloat(ctx, 0, "NumberSequence.new", 1);
                float end = Arg(ctx, 1).Type == LuaValueType.Nil ? start : ReadFloat(ctx, 1, "NumberSequence.new", 2);
                return Wrap(Construct(() => new RbxNumberSequence(start, end), "NumberSequence.new",
                    "pass finite numbers, e.g. NumberSequence.new(1, 0)"));
            });
            return new LuaValue(t);
        }

        /// <summary><c>ColorSequenceKeypoint.new(time, color)</c>.</summary>
        public static LuaValue BuildColorKeypointGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("ColorSequenceKeypoint.new", ctx =>
            {
                float time = ReadFloat(ctx, 0, "ColorSequenceKeypoint.new", 1);
                RbxColor3 color = ReadColor3(Arg(ctx, 1), "ColorSequenceKeypoint.new", 2);
                return Wrap(Construct(() => new RbxColorSequenceKeypoint(time, color), "ColorSequenceKeypoint.new",
                    "pass a time between 0 and 1 and a Color3"));
            });
            return new LuaValue(t);
        }

        /// <summary><c>ColorSequence.new(c)</c>, <c>ColorSequence.new(c0, c1)</c> and <c>ColorSequence.new(keypoints)</c>.</summary>
        public static LuaValue BuildColorSequenceGlobal()
        {
            LuaTable t = new();
            t["new"] = Fn("ColorSequence.new", ctx =>
            {
                LuaValue first = Arg(ctx, 0);
                if (first.TryRead(out LuaTable list))
                {
                    List<RbxColorSequenceKeypoint> keypoints = ReadList<RbxColorSequenceKeypoint>(list,
                        "ColorSequence.new", "ColorSequenceKeypoint");
                    return Wrap(Construct(() => new RbxColorSequence(keypoints), "ColorSequence.new",
                        SequenceFix("ColorSequenceKeypoint.new(0, Color3.new(1, 0, 0)), "
                                    + "ColorSequenceKeypoint.new(1, Color3.new(0, 0, 1))")));
                }

                RbxColor3 start = ReadColor3(first, "ColorSequence.new", 1);
                RbxColor3 end = Arg(ctx, 1).Type == LuaValueType.Nil ? start : ReadColor3(Arg(ctx, 1), "ColorSequence.new", 2);
                return Wrap(Construct(() => new RbxColorSequence(start, end), "ColorSequence.new",
                    "pass Color3 values with finite components"));
            });
            return new LuaValue(t);
        }

        // ---- property writes -----------------------------------------------------------------

        public static RbxNumberRange ReadAssignedNumberRange(LuaValue value, string ownerName, string property)
        {
            if (TryUnbox(value, out RbxNumberRange range))
            {
                return range;
            }

            throw RbxError.BadArgument(ownerName + "." + property + " expects a NumberRange, got " + Describe(value),
                "assign NumberRange.new(min, max), e.g. " + ownerName + "." + property + " = NumberRange.new(1, 2)");
        }

        public static RbxNumberSequence ReadAssignedNumberSequence(LuaValue value, string ownerName, string property)
        {
            if (TryUnbox(value, out RbxNumberSequence sequence))
            {
                return sequence;
            }

            throw RbxError.BadArgument(
                ownerName + "." + property + " expects a NumberSequence, got " + Describe(value),
                "wrap the number: " + ownerName + "." + property + " = NumberSequence.new(value), or "
                + "NumberSequence.new(start, end) for a change over the particle's life");
        }

        public static RbxColorSequence ReadAssignedColorSequence(LuaValue value, string ownerName, string property)
        {
            if (TryUnbox(value, out RbxColorSequence sequence))
            {
                return sequence;
            }

            throw RbxError.BadArgument(
                ownerName + "." + property + " expects a ColorSequence, got " + Describe(value),
                "wrap the colour: " + ownerName + "." + property + " = ColorSequence.new(Color3.new(1, 0.5, 0)), or "
                + "ColorSequence.new(startColor, endColor)");
        }

        // ---- metatables ----------------------------------------------------------------------

        private static LuaTable BuildNumberRangeMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("NumberRange.__index", ctx =>
            {
                RbxNumberRange self = Read<RbxNumberRange>(ctx, "NumberRange");
                string key = ReadString(ctx, 1, "NumberRange member access");
                switch (key)
                {
                    case "Min": return self.Min;
                    case "Max": return self.Max;
                    default: throw NotAMember(key, "NumberRange");
                }
            });
            AddCommon<RbxNumberRange>(meta, "NumberRange");
            return Lock(meta);
        }

        private static LuaTable BuildNumberKeypointMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("NumberSequenceKeypoint.__index", ctx =>
            {
                RbxNumberSequenceKeypoint self = Read<RbxNumberSequenceKeypoint>(ctx, "NumberSequenceKeypoint");
                string key = ReadString(ctx, 1, "NumberSequenceKeypoint member access");
                switch (key)
                {
                    case "Time": return self.Time;
                    case "Value": return self.Value;
                    case "Envelope": return self.Envelope;
                    default: throw NotAMember(key, "NumberSequenceKeypoint");
                }
            });
            AddCommon<RbxNumberSequenceKeypoint>(meta, "NumberSequenceKeypoint");
            return Lock(meta);
        }

        private static LuaTable BuildNumberSequenceMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("NumberSequence.__index", ctx =>
            {
                RbxNumberSequence self = Read<RbxNumberSequence>(ctx, "NumberSequence");
                string key = ReadString(ctx, 1, "NumberSequence member access");
                if (key != "Keypoints")
                {
                    throw NotAMember(key, "NumberSequence");
                }

                LuaTable list = new();
                for (int index = 0; index < self.Keypoints.Count; index++)
                {
                    list[index + 1] = Wrap(self.Keypoints[index]);
                }

                return new LuaValue(list);
            });
            AddCommon<RbxNumberSequence>(meta, "NumberSequence");
            return Lock(meta);
        }

        private static LuaTable BuildColorKeypointMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("ColorSequenceKeypoint.__index", ctx =>
            {
                RbxColorSequenceKeypoint self = Read<RbxColorSequenceKeypoint>(ctx, "ColorSequenceKeypoint");
                string key = ReadString(ctx, 1, "ColorSequenceKeypoint member access");
                switch (key)
                {
                    case "Time": return self.Time;
                    case "Value": return LuaCsRbxDatatypeBindings.Wrap(self.Value);
                    default: throw NotAMember(key, "ColorSequenceKeypoint");
                }
            });
            AddCommon<RbxColorSequenceKeypoint>(meta, "ColorSequenceKeypoint");
            return Lock(meta);
        }

        private static LuaTable BuildColorSequenceMeta()
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("ColorSequence.__index", ctx =>
            {
                RbxColorSequence self = Read<RbxColorSequence>(ctx, "ColorSequence");
                string key = ReadString(ctx, 1, "ColorSequence member access");
                if (key != "Keypoints")
                {
                    throw NotAMember(key, "ColorSequence");
                }

                LuaTable list = new();
                for (int index = 0; index < self.Keypoints.Count; index++)
                {
                    list[index + 1] = Wrap(self.Keypoints[index]);
                }

                return new LuaValue(list);
            });
            AddCommon<RbxColorSequence>(meta, "ColorSequence");
            return Lock(meta);
        }

        /// <summary>The read-only write, equality and tostring every one of these datatypes shares.</summary>
        private static void AddCommon<T>(LuaTable meta, string typeName)
        {
            meta[Metamethods.NewIndex] = Fn(typeName + ".__newindex", _ => throw RbxError.BadArgument(
                typeName + " values are immutable", "construct a new " + typeName + " instead of mutating one"));
            meta[Metamethods.Eq] = Fn(typeName + ".__eq", ctx =>
                TryUnbox(Arg(ctx, 0), out T a) && TryUnbox(Arg(ctx, 1), out T b) && a.Equals(b));
            meta[Metamethods.ToString] = Fn(typeName + ".__tostring", ctx => Read<T>(ctx, typeName).ToString());
        }

        // ---- helpers -------------------------------------------------------------------------

        private static T Read<T>(LuaFunctionExecutionContext ctx, string typeName)
        {
            LuaValue value = Arg(ctx, 0);
            if (TryUnbox(value, out T result))
            {
                return result;
            }

            throw RbxError.BadArgument(typeName + " member access expects a " + typeName + " as self",
                "read members with a dot, e.g. range.Min");
        }

        private static List<T> ReadList<T>(LuaTable table, string what, string elementName)
        {
            List<T> items = new();
            for (int index = 1; ; index++)
            {
                LuaValue element = table[index];
                if (element.Type == LuaValueType.Nil)
                {
                    break;
                }

                if (items.Count >= RbxSequenceRules.MaxKeypoints)
                {
                    throw RbxError.BadArgument(what + " takes at most " + RbxSequenceRules.MaxKeypoints + " keypoints",
                        "use fewer keypoints; a gradient rarely needs more than a handful");
                }

                if (!TryUnbox(element, out T item))
                {
                    throw RbxError.BadArgument(
                        what + " expects an array of " + elementName + " values, got " + Describe(element)
                        + " at position " + index,
                        "build each entry with " + elementName + ".new(time, ...)");
                }

                items.Add(item);
            }

            return items;
        }

        private static RbxColor3 ReadColor3(LuaValue value, string what, int argumentNumber)
        {
            if (TryUnbox(value, out RbxColor3 color))
            {
                return color;
            }

            throw ExpectedArgument(what, "a Color3", value, argumentNumber);
        }

        private static TValue Construct<TValue>(Func<TValue> build, string what, string fix)
        {
            try
            {
                return build();
            }
            catch (ArgumentException ex)
            {
                throw RbxError.BadArgument(what + ": " + ex.Message, fix);
            }
        }

        private static string SequenceFix(string example)
        {
            return "pass at least two keypoints in time order, the first at time 0 and the last at time 1, e.g. {"
                   + example + "}";
        }

        private static RbxError NotAMember(string key, string typeName)
        {
            return RbxError.BadArgument(key + " is not a valid member of " + typeName,
                "check the " + typeName + " member list in the Roblox API reference");
        }

        private static string Format(float value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
