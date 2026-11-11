using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// The Luau coroutine members Lua-CSharp lacks or the sandbox removed: <c>coroutine.wrap</c> (rebuilt over
    /// the guarded <c>coroutine.resume</c>, so a wrapped body runs under the same per-resume budget),
    /// <c>coroutine.isyieldable</c> and <c>coroutine.close</c>; and Luau's <c>debug</c> library, whose members
    /// read nothing a script could not reach otherwise: <c>traceback</c>, <c>info</c>, <c>profilebegin</c>,
    /// <c>profileend</c> and the memory category of the running thread.
    /// </summary>
    public sealed partial class LuaCsSecureEnvironment
    {
        /// <summary>The default memory category of a thread (Roblox names the script; a mod has one script).</summary>
        public const string DefaultMemoryCategory = "main";

        private static readonly ConditionalWeakTable<LuaState, StrongBox<string>> MemoryCategories = new();

        private static void InstallLuauCoroutineExtensions(LuaTable environment)
        {
            if (!environment["coroutine"].TryRead(out LuaTable coroutine))
            {
                return;
            }

            LuaValue resume = coroutine["resume"];
            LuaValue create = coroutine["create"];
            if (resume.Type == LuaValueType.Function && create.Type == LuaValueType.Function)
            {
                coroutine["wrap"] = new LuaFunction("coroutine.wrap", (ctx, ct) => CoroutineWrap(ctx, ct, create, resume));
            }

            coroutine["isyieldable"] = new LuaFunction("coroutine.isyieldable", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ctx.State.IsCoroutine
                                              && ctx.State.GetStatus() == LuaThreadStatus.Running)));
            coroutine["close"] = new LuaFunction("coroutine.close", CoroutineClose);
        }

        /// <summary>
        /// Luau <c>coroutine.wrap(f)</c>: a function that resumes a coroutine of <c>f</c> through the sandbox's
        /// guarded <c>coroutine.resume</c> and returns what it yields or returns, or raises the error it ended with.
        /// </summary>
        private static async ValueTask<int> CoroutineWrap(LuaFunctionExecutionContext ctx, CancellationToken ct,
            LuaValue create, LuaValue resume)
        {
            ReadArgument<LuaFunction>(ctx, 0, "function");
            LuaValue[] created = await ctx.State.CallAsync(create, new[] { ctx.GetArgument(0) }.AsSpan(), ct);
            LuaValue thread = created.Length > 0 ? created[0] : LuaValue.Nil;
            LuaFunction wrapped = new("coroutine.wrap", async (call, token) =>
            {
                LuaValue[] arguments = new LuaValue[call.ArgumentCount + 1];
                arguments[0] = thread;
                for (int i = 0; i < call.ArgumentCount; i++)
                {
                    arguments[i + 1] = call.GetArgument(i);
                }

                LuaValue[] results = await call.State.CallAsync(resume, arguments.AsSpan(), token);
                if (results.Length > 0 && results[0].ToBoolean())
                {
                    return call.Return(results.AsSpan(1));
                }

                LuaValue error = results.Length > 1 ? results[1] : LuaValue.Nil;
                throw new LuaRuntimeException(call.State, error, 0);
            });
            return ctx.Return(wrapped);
        }

        /// <summary>
        /// Luau <c>coroutine.close(co)</c>: a suspended or dead coroutine is closed and <c>true</c> returned; a
        /// running or normal one is refused, and a task or signal-handler thread is refused as
        /// <c>coroutine.resume</c> refuses it.
        /// </summary>
        private static ValueTask<int> CoroutineClose(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            LuaState thread = ReadArgument<LuaState>(ctx, 0, "thread");
            if (LuaCsCoroutineHandle.IsHandleThread(thread))
            {
                throw LibraryRefusal(ctx.State,
                    "cannot close a task or signal-handler thread with coroutine.close; stop it with task.cancel");
            }

            LuaThreadStatus status = thread.GetStatus();
            if (status == LuaThreadStatus.Running || status == LuaThreadStatus.Normal)
            {
                throw LibraryRefusal(ctx.State, ("cannot close "
                    + (status == LuaThreadStatus.Running ? "running" : "normal") + " coroutine"));
            }

            thread.UnsafeSetStatus(LuaThreadStatus.Dead);
            return new ValueTask<int>(ctx.Return(true));
        }

        // ---- debug ---------------------------------------------------------------------------------

        private static void InstallLuauDebugLibrary(LuaTable environment)
        {
            LuaTable debug = new();
            debug["traceback"] = new LuaFunction("debug.traceback", DebugTraceback);
            debug["info"] = new LuaFunction("debug.info", DebugInfo);
            debug["profilebegin"] = new LuaFunction("debug.profilebegin", (ctx, ct) =>
            {
                ReadLuauString(ctx, 0);
                return new ValueTask<int>(ctx.Return());
            });
            debug["profileend"] = new LuaFunction("debug.profileend", (ctx, ct) => new ValueTask<int>(ctx.Return()));
            debug["getmemorycategory"] = new LuaFunction("debug.getmemorycategory",
                (ctx, ct) => new ValueTask<int>(ctx.Return(MemoryCategoryOf(ctx.State).Value)));
            debug["setmemorycategory"] = new LuaFunction("debug.setmemorycategory", (ctx, ct) =>
            {
                string tag = ReadLuauString(ctx, 0);
                StrongBox<string> category = MemoryCategoryOf(ctx.State);
                string previous = category.Value;
                category.Value = tag;
                return new ValueTask<int>(ctx.Return(previous));
            });
            debug["resetmemorycategory"] = new LuaFunction("debug.resetmemorycategory", (ctx, ct) =>
            {
                MemoryCategoryOf(ctx.State).Value = DefaultMemoryCategory;
                return new ValueTask<int>(ctx.Return());
            });
            debug["dumpcodesize"] = LuaCsRbxLua.Fn("debug.dumpcodesize", _ =>
                throw RbxKnownUnimplementedErrors.ForMember("debug.dumpcodesize",
                    RbxKnownUnimplementedMemberStatus.Unsupported, null,
                    "it exists only in the Roblox Studio command bar; there is no native code to measure here"));
            environment["debug"] = debug;
        }

        private static StrongBox<string> MemoryCategoryOf(LuaState state)
        {
            return MemoryCategories.GetValue(state, _ => new StrongBox<string>(DefaultMemoryCategory));
        }

        /// <summary>
        /// Luau <c>debug.traceback([thread,] [message [, level]])</c>: the message (when given) and the call stack
        /// as text; a message that is neither a string nor nil is returned unchanged.
        /// </summary>
        private static ValueTask<int> DebugTraceback(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            int argument = 0;
            LuaState thread = ctx.State;
            if (ctx.ArgumentCount > 0 && ctx.GetArgument(0).TryRead(out LuaState other))
            {
                thread = other;
                argument = 1;
            }

            LuaValue message = ctx.ArgumentCount > argument ? ctx.GetArgument(argument) : LuaValue.Nil;
            if (message.Type != LuaValueType.Nil && message.Type != LuaValueType.String
                                                 && message.Type != LuaValueType.Number)
            {
                return new ValueTask<int>(ctx.Return(message));
            }

            int level = ReadOptionalLuauInt(ctx, argument + 1, ReferenceEquals(thread, ctx.State) ? 1 : 0);
            if (level < 0)
            {
                LuaRuntimeException.BadArgument(ctx.State, argument + 2, "level can't be negative");
            }

            StringBuilder text = new();
            if (message.Type != LuaValueType.Nil)
            {
                text.Append(message.ToString()).Append('\n');
            }

            // WHY one rule for both: level 1 of the running thread is the caller (the top frame is
            // debug.traceback itself), and level 0 of another thread is its own top frame.
            ReadOnlySpan<CallStackFrame> frames = thread.GetCallStackFrames();
            for (int i = frames.Length - 1 - level; i >= 0; i--)
            {
                if (IsScriptClosure(frames[i].Function, out LuaClosure closure))
                {
                    text.Append(closure.Proto.ChunkName).Append(':').Append(FrameLine(frames, i, closure)).Append('\n');
                }
            }

            return new ValueTask<int>(ctx.Return(text.ToString()));
        }

        /// <summary>The chunk name every script chunk is loaded under (mods, <c>execute_lua</c>, the AI envelope).</summary>
        private const string ScriptChunkName = "sandbox_chunk";

        /// <summary>
        /// True when <paramref name="function"/> is a Lua function of a script's own chunk, not host-authored Lua (the
        /// signal runner's <c>signal_runner</c> chunk) and not a C# function.
        /// </summary>
        /// <remarks>
        /// WHY <c>debug</c> shows a script only its own frames: a C# function or host-authored Lua below the script
        /// on the stack (a scheduler bridge, the signal runner, the trusted iterator resolver) is not otherwise
        /// reachable, and <c>debug.info(level, "f")</c> would hand it out to be called with crafted arguments.
        /// </remarks>
        private static bool IsScriptClosure(LuaFunction function, out LuaClosure closure)
        {
            closure = function as LuaClosure;
            return closure != null && string.Equals(closure.Proto.ChunkName, ScriptChunkName, StringComparison.Ordinal);
        }

        /// <summary>The line frame <paramref name="index"/> is at: the call its callee was made from, or its start.</summary>
        private static int FrameLine(ReadOnlySpan<CallStackFrame> frames, int index, LuaClosure closure)
        {
            ReadOnlySpan<int> lines = closure.Proto.LineInfo;
            if (index + 1 < frames.Length)
            {
                int pc = frames[index + 1].CallerInstructionIndex;
                if (pc >= 0 && pc < lines.Length)
                {
                    return lines[pc];
                }
            }

            return closure.Proto.LineDefined;
        }

        /// <summary>
        /// Luau <c>debug.info([thread,] level | function, options)</c>: for each letter of <c>slnaf</c> asked, the
        /// source, the line, the name, the arity (parameter count and whether it is variadic) and the function;
        /// nothing for a level past the stack.
        /// </summary>
        private static ValueTask<int> DebugInfo(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            int argument = 0;
            LuaState thread = ctx.State;
            if (ctx.ArgumentCount > 0 && ctx.GetArgument(0).TryRead(out LuaState other))
            {
                thread = other;
                argument = 1;
            }

            LuaValue target = ctx.ArgumentCount > argument ? ctx.GetArgument(argument) : LuaValue.Nil;
            string options = ReadLuauString(ctx, argument + 1);
            for (int i = 0; i < options.Length; i++)
            {
                if ("slnaf".IndexOf(options[i]) < 0 || options.IndexOf(options[i], i + 1) >= 0)
                {
                    LuaRuntimeException.BadArgument(ctx.State, argument + 2, "invalid option");
                }
            }

            LuaFunction function;
            LuaClosure lua;
            int line;
            bool reachable;
            if (target.TryRead(out LuaFunction direct))
            {
                function = direct;
                lua = direct as LuaClosure;
                line = lua != null ? lua.Proto.LineDefined : -1;
                reachable = true;
            }
            else
            {
                int level = ReadLuauInt(ctx, argument);
                if (level < 0)
                {
                    LuaRuntimeException.BadArgument(ctx.State, argument + 1, "level can't be negative");
                }

                ReadOnlySpan<CallStackFrame> frames = thread.GetCallStackFrames();
                int index = frames.Length - 1 - level;
                if (index < 0 || index >= frames.Length)
                {
                    return new ValueTask<int>(ctx.Return());
                }

                function = frames[index].Function;
                reachable = IsScriptClosure(function, out lua);
                line = reachable ? FrameLine(frames, index, lua) : -1;
            }

            LuaValue[] results = new LuaValue[options.Length + 1];
            int count = 0;
            for (int i = 0; i < options.Length; i++)
            {
                switch (options[i])
                {
                    case 's':
                        results[count++] = reachable && lua != null ? lua.Proto.ChunkName : "[C]";
                        break;
                    case 'l':
                        results[count++] = line;
                        break;
                    case 'n':
                        // WHY nil for every Lua function: this VM keeps no function names, only the chunk's (DEV-111).
                        results[count++] = lua != null || string.IsNullOrEmpty(function.Name)
                            ? LuaValue.Nil
                            : (LuaValue)function.Name;
                        break;
                    case 'a':
                        results[count++] = reachable && lua != null ? lua.Proto.ParameterCount : 0;
                        results[count++] = !reachable || lua == null || lua.Proto.HasVariableArguments;
                        break;
                    default:
                        results[count++] = reachable ? function : LuaValue.Nil;
                        break;
                }
            }

            return new ValueTask<int>(ctx.Return(results.AsSpan(0, count)));
        }
    }
}
