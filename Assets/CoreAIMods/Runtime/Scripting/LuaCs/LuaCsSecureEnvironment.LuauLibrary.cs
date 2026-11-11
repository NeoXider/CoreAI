using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;
using Lua.Standard;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The Luau standard library on top of Lua-CSharp's Lua 5.2 libraries (MVP13, RT5): the Luau <c>table</c>
    /// functions, <c>unpack</c>, <c>newproxy</c>, <c>gcinfo</c>, Roblox's rules for <c>collectgarbage</c>,
    /// <c>loadstring</c>, <c>getfenv</c> and <c>setfenv</c>, and the slices installed from here (string, utf8,
    /// bit32, math, coroutine, debug, the loud stubs of <c>buffer</c> and <c>vector</c>). Arguments are read by
    /// Luau's <c>luaL_check*</c> rules with the sandbox's "bad argument" wording. A function that loops over a
    /// table is charged to the executing run like the Lua loop it replaces, and one that builds a table or a
    /// string has a cap like <c>string.rep</c>'s.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>
        /// Most elements one library call may build or move: <c>table.create</c> with a value, <c>table.move</c>,
        /// the pieces of <c>string.split</c>. Without a value <c>table.create</c> only reserves room, so a larger
        /// count is clamped to this, which no script can observe.
        /// </summary>
        /// <remarks>
        /// WHY a per-call cap on top of the allocation budget: the budget is sampled between instructions, and a
        /// single call that fills 10^9 slots (24 bytes each) is one instruction. 2^20 slots are 24 MB, the
        /// same order as the 1M-char cap of <c>string.rep</c> and inside every default budget.
        /// </remarks>
        public const int MaxTableBuildLength = 1 << 20;

        /// <summary>Most values <c>table.unpack</c>/<c>unpack</c> return: Luau's <c>LUAI_MAXCSTACK</c>.</summary>
        public const int MaxUnpackResults = 8000;

        /// <summary>Luau's error for a write into a table <c>table.freeze</c> froze.</summary>
        public const string ReadonlyTableMessage = "attempt to modify a readonly table";

        /// <summary>What <c>loadstring</c> returns after <c>nil</c>: Roblox's answer while LoadStringEnabled is off.</summary>
        public const string LoadStringUnavailableMessage = "loadstring() is not available";

        /// <summary>Roblox's error for a <c>collectgarbage</c> option other than <c>"count"</c>.</summary>
        public const string CollectGarbageCountOnlyMessage =
            "collectgarbage must be called with 'count'; use gcinfo() instead";

        /// <summary>
        /// Steps charged per element visited by <c>table.find</c>: the instructions of the loop it replaces,
        /// <c>for i = init, n do if t[i] == x then return i end end</c> (GETTABLE, EQ, JMP, FORLOOP).
        /// </summary>
        internal const int TableFindStepsPerElement = 4;

        /// <summary>Steps per slot <c>table.create</c> fills: <c>for i = 1, n do t[i] = v end</c> (SETTABLE, FORLOOP).</summary>
        internal const int TableFillStepsPerElement = 2;

        /// <summary>
        /// Steps per entry for <c>table.clear</c> and <c>table.clone</c>: <c>for k, v in next, t do c[k] = v end</c>
        /// (TFORCALL, TFORLOOP, SETTABLE).
        /// </summary>
        internal const int TableEntryStepsPerElement = 3;

        /// <summary>
        /// Steps per entry for <c>table.maxn</c>: the loop above with <c>if type(k) == 'number' and k > m then m = k
        /// end</c> as its body.
        /// </summary>
        internal const int TableMaxNStepsPerElement = 8;

        /// <summary>Steps per element <c>table.move</c> copies: <c>for i = 0, n - 1 do b[t + i] = a[f + i] end</c>.</summary>
        internal const int TableMoveStepsPerElement = 5;

        /// <summary>Steps per value <c>unpack</c> returns: one GETTABLE each.</summary>
        internal const int UnpackStepsPerElement = 1;

        // WHY a weak table keyed by the table and not a flag on it: Lua-CSharp's LuaTable has no read-only state,
        // and the mark must die with the table. WHY the process-wide counter: until anything is frozen no write
        // pays for the lookup.
        private static readonly ConditionalWeakTable<LuaTable, object> FrozenTables = new();
        private static readonly object FrozenMark = new();
        private static int _frozenTableCount;

        /// <summary>Adds the Luau library to a state built by <see cref="Create"/>, after the hardening passes.</summary>
        internal static void InstallLuauStandardLibrary(LuaState state)
        {
            LuaTable environment = state.Environment;
            InstallLuauTableLibrary(environment);
            InstallLuauGlobals(environment);
            InstallLuauStringExtensions(environment);
            InstallLuauUtf8Library(environment);
            InstallLuauBit32Library(environment);
            InstallLuauMathAdditions(environment);
            InstallLuauCoroutineExtensions(environment);
            InstallLuauDebugLibrary(environment);
            InstallLuauLibraryStubs(environment);
        }

        /// <summary>True when <paramref name="table"/> was frozen by <c>table.freeze</c>.</summary>
        internal static bool IsFrozenTable(LuaTable table)
        {
            return table != null && Volatile.Read(ref _frozenTableCount) > 0
                                 && FrozenTables.TryGetValue(table, out object _);
        }

        /// <summary>
        /// Charges <paramref name="elements"/> times <paramref name="stepsPerElement"/> to the run executing this
        /// library call on <paramref name="state"/>, if any (<see cref="LuaCsGuardedRun.ChargeLibrarySteps"/>).
        /// </summary>
        internal static void ChargeLibraryWork(LuaState state, long elements, int stepsPerElement)
        {
            if (elements <= 0)
            {
                return;
            }

            LuaCsGuardedRun run = LuaCsGuardedRun.InnermostExecuting();
            run?.ChargeLibrarySteps(elements * stepsPerElement, state);
        }

        // ---- table ---------------------------------------------------------------------------------

        private static void InstallLuauTableLibrary(LuaTable environment)
        {
            if (!environment["table"].TryRead(out LuaTable table))
            {
                return;
            }

            table["find"] = new LuaFunction("table.find", LuauTableFind);
            table["clear"] = new LuaFunction("table.clear", LuauTableClear);
            table["create"] = new LuaFunction("table.create", LuauTableCreate);
            table["clone"] = new LuaFunction("table.clone", LuauTableClone);
            table["freeze"] = new LuaFunction("table.freeze", LuauTableFreeze);
            table["isfrozen"] = new LuaFunction("table.isfrozen", LuauTableIsFrozen);
            table["move"] = new LuaFunction("table.move", LuauTableMove);
            table["maxn"] = new LuaFunction("table.maxn", LuauTableMaxN);
            table["getn"] = new LuaFunction("table.getn", LuauTableGetN);
            table["foreach"] = new LuaFunction("table.foreach", LuauTableForEach);
            table["foreachi"] = new LuaFunction("table.foreachi", LuauTableForEachI);
            LuaFunction unpack = new("table.unpack", LuauUnpack);
            table["unpack"] = unpack;
            environment["unpack"] = unpack;

            // WHY each native runs as this call itself, with this call's context: the frozen check is the only
            // addition, so the results, errors and the zero-copy counted sort stay exactly what they were.
            table["insert"] = new LuaFunction("table.insert", (ctx, ct) =>
            {
                RefuseFrozenTable(ctx, 0);
                return TableLibrary.Instance.Insert(ctx, ct);
            });
            table["remove"] = new LuaFunction("table.remove", (ctx, ct) =>
            {
                RefuseFrozenTable(ctx, 0);
                return TableLibrary.Instance.Remove(ctx, ct);
            });
            table["sort"] = new LuaFunction("table.sort", (ctx, ct) =>
            {
                RefuseFrozenTable(ctx, 0);
                return CallNativeInPlace(ctx, ct, NativeSort, "table.sort");
            });
            if (environment["rawset"].Type == LuaValueType.Function)
            {
                environment["rawset"] = new LuaFunction("rawset", (ctx, ct) =>
                {
                    RefuseFrozenTable(ctx, 0);
                    return BasicLibrary.Instance.RawSet(ctx, ct);
                });
            }

            if (environment["setmetatable"].Type == LuaValueType.Function)
            {
                environment["setmetatable"] = new LuaFunction("setmetatable", (ctx, ct) =>
                {
                    RefuseFrozenTable(ctx, 0);
                    return BasicLibrary.Instance.SetMetatable(ctx, ct);
                });
            }
        }

        /// <summary>Raises <see cref="ReadonlyTableMessage"/> when argument <paramref name="index"/> is a frozen table.</summary>
        private static void RefuseFrozenTable(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx.ArgumentCount > index && ctx.GetArgument(index).TryRead(out LuaTable table) && IsFrozenTable(table))
            {
                throw LibraryRefusal(ctx.State, ReadonlyTableMessage);
            }
        }

        /// <summary>
        /// Luau <c>table.find(t, value, init)</c>: the first index from <c>init</c> (default 1) whose value equals
        /// <c>value</c> as <c>==</c> compares it (an <c>__eq</c> shared by both sides is called), stopping at the
        /// first nil; raw reads, as Luau's <c>luaH_getnum</c>.
        /// </summary>
        private static ValueTask<int> LuauTableFind(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            if (ctx.ArgumentCount < 2)
            {
                LuaRuntimeException.BadArgument(ctx.State, 2, "value expected");
            }

            LuaValue needle = ctx.GetArgument(1);
            int init = ReadOptionalLuauInt(ctx, 2, 1);
            if (init < 1)
            {
                LuaRuntimeException.BadArgument(ctx.State, 3, "index out of range");
            }

            LuaValue equals = EqualityMetamethod(needle);
            long visited = 0;
            int index = init;
            try
            {
                Span<LuaValue> array = table.GetArraySpan();
                for (; index <= array.Length; index++)
                {
                    LuaValue element = array[index - 1];
                    if (element.Type == LuaValueType.Nil)
                    {
                        return new ValueTask<int>(ctx.Return(LuaValue.Nil));
                    }

                    visited++;
                    if (element == needle)
                    {
                        return new ValueTask<int>(ctx.Return(index));
                    }

                    if (equals.Type != LuaValueType.Nil && SharesEqualityMetamethod(element, needle, equals))
                    {
                        return FindThroughMetamethodAsync(ctx, ct, table, needle, equals, index);
                    }
                }

                for (;; index++)
                {
                    LuaValue element = table[index];
                    if (element.Type == LuaValueType.Nil)
                    {
                        return new ValueTask<int>(ctx.Return(LuaValue.Nil));
                    }

                    visited++;
                    if (element == needle)
                    {
                        return new ValueTask<int>(ctx.Return(index));
                    }

                    if (equals.Type != LuaValueType.Nil && SharesEqualityMetamethod(element, needle, equals))
                    {
                        return FindThroughMetamethodAsync(ctx, ct, table, needle, equals, index);
                    }
                }
            }
            finally
            {
                ChargeLibraryWork(ctx.State, visited, TableFindStepsPerElement);
            }
        }

        /// <summary>
        /// The rest of <see cref="LuauTableFind"/> from <paramref name="index"/>, the first element whose comparison
        /// runs <c>__eq</c>; that element was already charged by the caller.
        /// </summary>
        private static async ValueTask<int> FindThroughMetamethodAsync(LuaFunctionExecutionContext ctx,
            CancellationToken ct, LuaTable table, LuaValue needle, LuaValue equals, int index)
        {
            long visited = 0;
            bool charged = true;
            try
            {
                for (;; index++)
                {
                    LuaValue element = table[index];
                    if (element.Type == LuaValueType.Nil)
                    {
                        return ctx.Return(LuaValue.Nil);
                    }

                    if (!charged)
                    {
                        visited++;
                    }

                    charged = false;
                    if (element == needle)
                    {
                        return ctx.Return(index);
                    }

                    if (SharesEqualityMetamethod(element, needle, equals))
                    {
                        LuaValue[] result = await CallCountedAsync(ctx.State, equals,
                            new[] { needle, element }.AsSpan(), ct, "table.find");
                        if (result.Length > 0 && result[0].ToBoolean())
                        {
                            return ctx.Return(index);
                        }
                    }
                }
            }
            finally
            {
                ChargeLibraryWork(ctx.State, visited, TableFindStepsPerElement);
            }
        }

        /// <summary>
        /// The <c>__eq</c> a comparison with <paramref name="value"/> may run: a table's or userdata's own, nil for
        /// every other kind (Luau compares those by value).
        /// </summary>
        private static LuaValue EqualityMetamethod(LuaValue value)
        {
            LuaTable metatable = MetatableOf(value);
            return metatable != null && metatable.TryGetValue(Metamethods.Eq, out LuaValue equals)
                ? equals
                : LuaValue.Nil;
        }

        /// <summary>
        /// True when <c>element == needle</c> runs <paramref name="equals"/>: both of one type, distinct, and the
        /// element's metatable holding the same <c>__eq</c> (Lua's and Luau's rule for calling it).
        /// </summary>
        private static bool SharesEqualityMetamethod(LuaValue element, LuaValue needle, LuaValue equals)
        {
            if (element.Type != needle.Type)
            {
                return false;
            }

            LuaTable metatable = MetatableOf(element);
            return metatable != null && metatable.TryGetValue(Metamethods.Eq, out LuaValue other) && other == equals;
        }

        private static LuaTable MetatableOf(LuaValue value)
        {
            if (value.Type == LuaValueType.Table)
            {
                return value.Read<LuaTable>().Metatable;
            }

            return value.Type == LuaValueType.UserData && value.TryRead(out ILuaUserData userData)
                ? userData.Metatable
                : null;
        }

        /// <summary>Luau <c>table.clear(t)</c>: every key set to nil, the metatable kept; refused on a frozen table.</summary>
        private static ValueTask<int> LuauTableClear(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            RefuseFrozenTable(ctx, 0);
            long entries = table.ArrayLength + table.HashMapCount;
            table.Clear();
            ChargeLibraryWork(ctx.State, entries, TableEntryStepsPerElement);
            return new ValueTask<int>(ctx.Return());
        }

        /// <summary>
        /// Luau <c>table.create(n, value)</c>: a table with room for <c>n</c> array slots, each holding
        /// <c>value</c> when one is given; <c>n</c> below zero is "size out of range".
        /// </summary>
        private static ValueTask<int> LuauTableCreate(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            int count = ReadLuauInt(ctx, 0);
            if (count < 0)
            {
                LuaRuntimeException.BadArgument(ctx.State, 1, "size out of range");
            }

            bool fill = ctx.ArgumentCount > 1 && ctx.GetArgument(1).Type != LuaValueType.Nil;
            if (fill && count > MaxTableBuildLength)
            {
                throw LibraryRefusal(ctx.State, $"{SandboxLinePrefix}table.create would fill {count} slots, more "
                                                + $"than the {MaxTableBuildLength} one call may build.");
            }

            LuaTable table = new(Math.Min(count, MaxTableBuildLength), 0);
            if (fill)
            {
                LuaValue value = ctx.GetArgument(1);
                for (int i = 1; i <= count; i++)
                {
                    table[i] = value;
                }

                ChargeLibraryWork(ctx.State, count, TableFillStepsPerElement);
            }

            return new ValueTask<int>(ctx.Return(table));
        }

        /// <summary>
        /// Luau <c>table.clone(t)</c>: a shallow copy with the same metatable, never frozen; a table whose metatable
        /// is protected by <c>__metatable</c> is refused.
        /// </summary>
        private static ValueTask<int> LuauTableClone(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            RefuseProtectedMetatable(ctx, table);
            LuaTable copy = new(table.ArrayLength, table.HashMapCount);
            long entries = 0;
            foreach (KeyValuePair<LuaValue, LuaValue> entry in table)
            {
                copy[entry.Key] = entry.Value;
                entries++;
            }

            copy.Metatable = table.Metatable;
            ChargeLibraryWork(ctx.State, entries, TableEntryStepsPerElement);
            return new ValueTask<int>(ctx.Return(copy));
        }

        /// <summary>
        /// Luau <c>table.freeze(t)</c>: marks the table read-only and returns it. Writes through <c>table.insert</c>,
        /// <c>table.remove</c>, <c>table.sort</c>, <c>table.clear</c>, <c>table.move</c>, <c>rawset</c> and
        /// <c>setmetatable</c> then raise <see cref="ReadonlyTableMessage"/>; a plain assignment does not (DEV-106).
        /// </summary>
        private static ValueTask<int> LuauTableFreeze(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            RefuseProtectedMetatable(ctx, table);
            if (!FrozenTables.TryGetValue(table, out object _))
            {
                FrozenTables.Add(table, FrozenMark);
                Interlocked.Increment(ref _frozenTableCount);
            }

            return new ValueTask<int>(ctx.Return(table));
        }

        private static ValueTask<int> LuauTableIsFrozen(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            return new ValueTask<int>(ctx.Return(IsFrozenTable(ReadLuauTable(ctx, 0))));
        }

        private static void RefuseProtectedMetatable(LuaFunctionExecutionContext ctx, LuaTable table)
        {
            if (table.Metatable != null && table.Metatable.TryGetValue(Metamethods.Metatable, out LuaValue lockValue)
                                        && lockValue.Type != LuaValueType.Nil)
            {
                LuaRuntimeException.BadArgument(ctx.State, 1, "table has a protected metatable");
            }
        }

        /// <summary>
        /// Luau <c>table.move(a1, f, e, t, a2)</c>: raw copies of <c>a1[f..e]</c> into <c>a2</c> (default <c>a1</c>)
        /// from index <c>t</c>, backwards when the ranges overlap that way; returns <c>a2</c>.
        /// </summary>
        private static ValueTask<int> LuauTableMove(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            int from = ReadLuauInt(ctx, 1);
            int end = ReadLuauInt(ctx, 2);
            int to = ReadLuauInt(ctx, 3);
            bool otherDestination = ctx.ArgumentCount > 4 && ctx.GetArgument(4).Type != LuaValueType.Nil;
            LuaTable source = ReadLuauTable(ctx, 0);
            LuaTable destination = otherDestination ? ReadLuauTable(ctx, 4) : source;
            if (end >= from)
            {
                if (!(from > 0 || end < int.MaxValue + from))
                {
                    LuaRuntimeException.BadArgument(ctx.State, 3, "too many elements to move");
                }

                int count = end - from + 1;
                if (to > int.MaxValue - count + 1)
                {
                    LuaRuntimeException.BadArgument(ctx.State, 4, "destination wrap around");
                }

                if (IsFrozenTable(destination))
                {
                    throw LibraryRefusal(ctx.State, ReadonlyTableMessage);
                }

                if (count > MaxTableBuildLength)
                {
                    throw LibraryRefusal(ctx.State, $"{SandboxLinePrefix}table.move would move {count} elements, "
                                                    + $"more than the {MaxTableBuildLength} one call may move.");
                }

                if (to > end || to <= from || !ReferenceEquals(source, destination))
                {
                    for (int i = 0; i < count; i++)
                    {
                        destination[to + i] = source[from + i];
                    }
                }
                else
                {
                    for (int i = count - 1; i >= 0; i--)
                    {
                        destination[to + i] = source[from + i];
                    }
                }

                ChargeLibraryWork(ctx.State, count, TableMoveStepsPerElement);
            }

            return new ValueTask<int>(ctx.Return(destination));
        }

        /// <summary>Luau <c>table.maxn(t)</c>: the largest positive numeric key, 0 when there is none.</summary>
        private static ValueTask<int> LuauTableMaxN(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            double max = 0d;
            long entries = 0;
            foreach (KeyValuePair<LuaValue, LuaValue> entry in table)
            {
                entries++;
                if (entry.Key.Type == LuaValueType.Number && entry.Value.Type != LuaValueType.Nil)
                {
                    double key = entry.Key.Read<double>();
                    if (key > max)
                    {
                        max = key;
                    }
                }
            }

            ChargeLibraryWork(ctx.State, entries, TableMaxNStepsPerElement);
            return new ValueTask<int>(ctx.Return(max));
        }

        /// <summary>Luau's deprecated <c>table.getn(t)</c>: the raw length.</summary>
        private static ValueTask<int> LuauTableGetN(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            return new ValueTask<int>(ctx.Return(RawLength(ReadLuauTable(ctx, 0))));
        }

        /// <summary>
        /// Luau's deprecated <c>table.foreach(t, f)</c>: <c>f(k, v)</c> for every entry until it returns a non-nil
        /// value, which is returned. Each call is a counted call back into Lua under the yield fence, as
        /// <c>table.sort</c>'s comparator.
        /// </summary>
        private static async ValueTask<int> LuauTableForEach(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            LuaFunction function = ReadArgument<LuaFunction>(ctx, 1, "function");
            LuaValue key = LuaValue.Nil;
            while (table.TryGetNext(key, out KeyValuePair<LuaValue, LuaValue> entry))
            {
                key = entry.Key;
                LuaValue[] result = await CallCountedAsync(ctx.State, function,
                    new[] { entry.Key, entry.Value }.AsSpan(), ct, "table.foreach");
                if (result.Length > 0 && result[0].Type != LuaValueType.Nil)
                {
                    return ctx.Return(result[0]);
                }
            }

            return ctx.Return();
        }

        /// <summary>Luau's deprecated <c>table.foreachi(t, f)</c>: <c>f(i, t[i])</c> for <c>i = 1, #t</c>, as foreach.</summary>
        private static async ValueTask<int> LuauTableForEachI(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            LuaFunction function = ReadArgument<LuaFunction>(ctx, 1, "function");
            int count = RawLength(table);
            for (int i = 1; i <= count; i++)
            {
                LuaValue[] result = await CallCountedAsync(ctx.State, function,
                    new[] { (LuaValue)i, table[i] }.AsSpan(), ct, "table.foreachi");
                if (result.Length > 0 && result[0].Type != LuaValueType.Nil)
                {
                    return ctx.Return(result[0]);
                }
            }

            return ctx.Return();
        }

        /// <summary>
        /// Luau <c>unpack(t, i, j)</c> (also <c>table.unpack</c>): raw <c>t[i]..t[j]</c>, <c>i</c> default 1 and
        /// <c>j</c> the raw length; more than <see cref="MaxUnpackResults"/> values is "too many results to unpack".
        /// </summary>
        /// <remarks>
        /// WHY replaced: Lua-CSharp's own unpack returned 34,464 values for <c>table.unpack({}, 1, 100000)</c>
        /// (its count wrapped at 16 bits), where Luau refuses the call.
        /// </remarks>
        private static ValueTask<int> LuauUnpack(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaTable table = ReadLuauTable(ctx, 0);
            int first = ReadOptionalLuauInt(ctx, 1, 1);
            int last = ctx.ArgumentCount > 2 && ctx.GetArgument(2).Type != LuaValueType.Nil
                ? ReadLuauInt(ctx, 2)
                : RawLength(table);
            if (first > last)
            {
                return new ValueTask<int>(ctx.Return());
            }

            long count = (long)last - first + 1;
            if (count > MaxUnpackResults)
            {
                throw LibraryRefusal(ctx.State, "too many results to unpack");
            }

            LuaValue[] values = new LuaValue[count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = table[first + i];
            }

            ChargeLibraryWork(ctx.State, count, UnpackStepsPerElement);
            return new ValueTask<int>(ctx.Return(values.AsSpan()));
        }

        /// <summary>
        /// Luau's raw length (<c>lua_objlen</c>, no <c>__len</c>): a border, the array part's length carried on
        /// while the next index holds a value.
        /// </summary>
        /// <remarks>
        /// WHY carried on past the array part: Lua-CSharp 0.5.6 keeps <c>t[9..]</c> of a table filled backwards in
        /// its hash part, so its array length stops at 8 although <c>t[9]</c> is set; 0.5.8 (issue 345) moved them,
        /// and on either version the border found here is the one Luau's <c>#</c> gives.
        /// </remarks>
        internal static int RawLength(LuaTable table)
        {
            int length = table.ArrayLength;
            while (length < int.MaxValue && table[length + 1].Type != LuaValueType.Nil)
            {
                length++;
            }

            return length;
        }

        // ---- globals -------------------------------------------------------------------------------

        private static void InstallLuauGlobals(LuaTable environment)
        {
            environment["newproxy"] = new LuaFunction("newproxy", LuauNewProxy);
            environment["gcinfo"] = new LuaFunction("gcinfo", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(HeapKilobytes())));
            environment["collectgarbage"] = new LuaFunction("collectgarbage", LuauCollectGarbage);
            environment["loadstring"] = new LuaFunction("loadstring", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(LuaValue.Nil, LoadStringUnavailableMessage)));
            environment["getfenv"] = new LuaFunction("getfenv", LuauGetFEnv);
            environment["setfenv"] = LuaCsRbxLua.Fn("setfenv", _ =>
                throw RbxKnownUnimplementedErrors.ForMember("setfenv", RbxKnownUnimplementedMemberStatus.Unsupported,
                    null, "this VM cannot give a function another environment; pass the values the function needs "
                          + "as arguments or keep them in a table it reads"));
            if (environment["pcall"].Type == LuaValueType.Function)
            {
                environment["ypcall"] = environment["pcall"];
            }
        }

        /// <summary>A userdata with no data and, when asked for, an empty metatable of its own (Luau <c>newproxy</c>).</summary>
        private sealed class LuauProxy : ILuaUserData
        {
            public LuaTable Metatable { get; set; }

            public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
        }

        private static ValueTask<int> LuauNewProxy(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaValue flag = ctx.ArgumentCount > 0 ? ctx.GetArgument(0) : LuaValue.Nil;
            if (flag.Type != LuaValueType.Nil && flag.Type != LuaValueType.Boolean)
            {
                LuaRuntimeException.BadArgument(ctx.State, 1, "nil or boolean expected, got " + flag.TypeToString());
            }

            LuauProxy proxy = new();
            if (flag.ToBoolean())
            {
                proxy.Metatable = new LuaTable();
            }

            return new ValueTask<int>(ctx.Return(new LuaValue(proxy)));
        }

        /// <summary>
        /// The managed heap in whole kilobytes: <c>gcinfo()</c> and <c>collectgarbage("count")</c>. The process
        /// heap, not one VM's: Lua-CSharp keeps no heap of its own (DEV-107).
        /// </summary>
        private static double HeapKilobytes()
        {
            return Math.Floor(GC.GetTotalMemory(false) / 1024d);
        }

        private static ValueTask<int> LuauCollectGarbage(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            string option = ReadOptionalLuauString(ctx, 0, "collect");
            if (option != "count")
            {
                throw LibraryRefusal(ctx.State, CollectGarbageCountOnlyMessage);
            }

            return new ValueTask<int>(ctx.Return(HeapKilobytes()));
        }

        /// <summary>
        /// <c>getfenv(f)</c>: the script's environment for no argument, a stack level that exists, or a function;
        /// every function of a script shares it here (DEV-108).
        /// </summary>
        private static ValueTask<int> LuauGetFEnv(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaValue target = ctx.ArgumentCount > 0 ? ctx.GetArgument(0) : LuaValue.Nil;
            if (target.Type == LuaValueType.Number || target.Type == LuaValueType.String)
            {
                int level = ReadLuauInt(ctx, 0);
                if (level < 0)
                {
                    LuaRuntimeException.BadArgument(ctx.State, 1, "level must be non-negative");
                }

                if (level > ctx.State.GetCallStackFrames().Length)
                {
                    LuaRuntimeException.BadArgument(ctx.State, 1, "invalid level");
                }
            }
            else if (target.Type != LuaValueType.Nil && target.Type != LuaValueType.Function)
            {
                LuaRuntimeException.BadArgument(ctx.State, 1, "number expected, got " + target.TypeToString());
            }

            return new ValueTask<int>(ctx.Return(ctx.State.Environment));
        }

        // ---- argument readers (Luau luaL_check*) -------------------------------------------------------

        /// <summary>Luau's <c>luaL_checktype(L, n, LUA_TTABLE)</c> with the sandbox's line.</summary>
        private static LuaTable ReadLuauTable(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx.ArgumentCount > index && ctx.GetArgument(index).TryRead(out LuaTable table))
            {
                return table;
            }

            string got = ctx.ArgumentCount > index ? ctx.GetArgument(index).TypeToString() : "no value";
            LuaRuntimeException.BadArgument(ctx.State, index + 1, "table expected, got " + got);
            throw new LuaRuntimeException(ctx.State, (LuaValue)("bad argument #" + (index + 1) + " (table expected)"));
        }

        /// <summary>
        /// Luau's <c>luaL_checkinteger</c>: <see cref="ReadLuauNumber"/>, then the C <c>(int)</c> cast — the
        /// fraction truncated, and NaN or a value outside the int range read as <see cref="int.MinValue"/>, which
        /// x86-64 gives for both.
        /// </summary>
        private static int ReadLuauInt(LuaFunctionExecutionContext ctx, int index)
        {
            return LuauToInt(ReadLuauNumber(ctx, index));
        }

        /// <summary>Luau's <c>luaL_optinteger</c>: <paramref name="fallback"/> for an absent or nil argument.</summary>
        private static int ReadOptionalLuauInt(LuaFunctionExecutionContext ctx, int index, int fallback)
        {
            if (ctx.ArgumentCount <= index || ctx.GetArgument(index).Type == LuaValueType.Nil)
            {
                return fallback;
            }

            return ReadLuauInt(ctx, index);
        }

        /// <summary>The C <c>(int)</c> cast of a double on x86-64 (see <see cref="ReadLuauInt"/>).</summary>
        internal static int LuauToInt(double value)
        {
            if (double.IsNaN(value) || value >= 2147483648d || value <= -2147483649d)
            {
                return int.MinValue;
            }

            return (int)Math.Truncate(value);
        }

        /// <summary>Luau's <c>luaL_checkstring</c>: a string, or a number as <c>tostring</c> gives it.</summary>
        private static string ReadLuauString(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx.ArgumentCount > index && LuaCsValueMarshaller.TryCoerceString(ctx.GetArgument(index), out string text))
            {
                return text;
            }

            string got = ctx.ArgumentCount > index ? ctx.GetArgument(index).TypeToString() : "no value";
            LuaRuntimeException.BadArgument(ctx.State, index + 1, "string expected, got " + got);
            throw new LuaRuntimeException(ctx.State, (LuaValue)("bad argument #" + (index + 1) + " (string expected)"));
        }

        /// <summary>Luau's <c>luaL_optstring</c>: <paramref name="fallback"/> for an absent or nil argument.</summary>
        private static string ReadOptionalLuauString(LuaFunctionExecutionContext ctx, int index, string fallback)
        {
            if (ctx.ArgumentCount <= index || ctx.GetArgument(index).Type == LuaValueType.Nil)
            {
                return fallback;
            }

            return ReadLuauString(ctx, index);
        }

        // ---- loud stubs ----------------------------------------------------------------------------

        /// <summary>
        /// The Luau libraries the sandbox does not implement yet, as locked tables whose every read, write or call
        /// raises the NOT_IMPLEMENTED stub naming the rung, instead of "attempt to index a nil value".
        /// </summary>
        private static void InstallLuauLibraryStubs(LuaTable environment)
        {
            environment["buffer"] = LibraryStub("buffer", RbxKnownUnimplementedMemberStatus.Planned, "MVP14",
                "keep binary data in a string (string.byte/string.char) or in a table of numbers");
            environment["vector"] = LibraryStub("vector", RbxKnownUnimplementedMemberStatus.Planned, "MVP14",
                "use Vector3.new(x, y, z) and its members; Roblox's vector values are Vector3 values");
        }

        private static LuaValue LibraryStub(string library, RbxKnownUnimplementedMemberStatus status, string rung,
            string workaround)
        {
            LuaTable meta = new();
            meta[Metamethods.Index] = LuaCsRbxLua.Fn(library + ".__index", ctx =>
                throw RbxKnownUnimplementedErrors.ForMember(library + "." + MemberName(ctx), status, rung, workaround));
            meta[Metamethods.NewIndex] = LuaCsRbxLua.Fn(library + ".__newindex", ctx =>
                throw RbxKnownUnimplementedErrors.ForMember(library + "." + MemberName(ctx), status, rung, workaround));
            meta[Metamethods.Call] = LuaCsRbxLua.Fn(library + ".__call", _ =>
                throw RbxKnownUnimplementedErrors.ForMember(library, status, rung, workaround));
            meta[Metamethods.Metatable] = "The metatable is locked";
            LuaTable stub = new();
            stub.Metatable = meta;
            return stub;
        }

        private static string MemberName(LuaFunctionExecutionContext ctx)
        {
            if (ctx.ArgumentCount > 1 && LuaCsValueMarshaller.TryCoerceString(ctx.GetArgument(1), out string name))
            {
                return name;
            }

            return "?";
        }
    }
}
