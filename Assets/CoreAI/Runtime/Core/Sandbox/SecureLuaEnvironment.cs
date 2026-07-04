#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
﻿using System;
using MoonSharp.Interpreter;

namespace CoreAI.Sandbox
{
    /// <summary>
    /// Creates MoonSharp Lua runtimes with a restricted global surface and instruction guards.
    /// </summary>
    public sealed class SecureLuaEnvironment
    {
        private static readonly CoreModules SandboxModules =
            CoreModules.Preset_HardSandbox | CoreModules.Coroutine;

        /// <summary>Maximum instruction budget for one-shot Lua script execution.</summary>
        public const int OneShotHardLimitSteps = 500_000;

        /// <summary>
        /// Maximum length of a string that <c>string.rep</c> may build. Building a huge string is a
        /// single VM instruction, so the instruction-limit debugger cannot interrupt it; the cap is
        /// enforced before allocation instead.
        /// </summary>
        public const int MaxStringRepLength = 1_000_000;

        /// <summary>
        /// Maximum width/precision a single <c>string.format</c> conversion specifier may request
        /// (e.g. the <c>999999999</c> in <c>"%999999999d"</c>). Like <c>string.rep</c>, a padded
        /// conversion allocates its whole result in one VM instruction, so the instruction-limit
        /// debugger cannot interrupt it; the cap is enforced by parsing the format string before
        /// the underlying formatter runs.
        /// </summary>
        public const int MaxStringFormatLength = MaxStringRepLength;

        /// <summary>
        /// Host opt-in to run the MoonSharp Lua sandbox on the WebGL player. Default <c>false</c>:
        /// WebGL stays disabled unless the host explicitly enables it after verifying an IL2CPP build
        /// keeps the required marshalling metadata. Set once at bootstrap from
        /// <see cref="ICoreAISettings.EnableLuaOnWebGl"/>. Ignored on non-WebGL players (always supported).
        /// </summary>
        public static bool WebGlLuaOptIn { get; set; }

        /// <summary>Whether the embedded MoonSharp sandbox is safe to instantiate on this player.</summary>
        public static bool IsSupported
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return WebGlLuaOptIn;
#else
                return true;
#endif
            }
        }

        /// <summary>Creates a secured MoonSharp script and registers the allowed Lua APIs.</summary>
        public Script CreateScript(LuaApiRegistry registry)
        {
            ThrowIfUnsupported();

            Script script = new(SandboxModules);
            RouteDebugPrint(script);
            registry?.ApplyToGlobals(script.Globals);

            // Attach a debugger before loading host code so instruction limits cover all execution.
            InstructionLimitDebugger debugger = new(OneShotHardLimitSteps, 2000);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);
            return script;
        }

        /// <summary>
        /// MoonSharp's default <c>print</c> goes to <c>Console.WriteLine</c>, which is invisible in a
        /// Unity player — LLM-written Lua that reaches for <c>print</c> first would vanish silently.
        /// Route it to the project logger; hosts with a richer pipeline (e.g. the mod runtime's
        /// <c>report</c> events) may overwrite <c>Options.DebugPrint</c> again after creation.
        /// </summary>
        private static void RouteDebugPrint(Script script)
        {
            script.Options.DebugPrint = message => Logging.Log.Instance.Info($"[Lua print] {message}");
        }

        /// <summary>Runs Lua code inside a secured script with the optional execution guard.</summary>
        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            ThrowIfUnsupported();

            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            // LuaExecutionGuard.Execute attaches its own debugger, replacing the one CreateScript set, so
            // the default guard would silently cap at 200k steps instead of the documented one-shot limit.
            // Build the default guard with OneShotHardLimitSteps so RunChunk actually honours the constant.
            guard ??= new LuaExecutionGuard(maxSteps: OneShotHardLimitSteps);
            return guard.Execute(script, fn);
        }

        /// <summary>
        /// Creates a sandboxed coroutine handle for Lua code that yields across Unity frames.
        /// </summary>
        public LuaCoroutineHandle CreateCoroutine(
            LuaApiRegistry registry,
            string luaCode,
            int budgetPerResume = LuaCoroutineHandle.DefaultBudgetPerResume,
            long totalLifetimeSteps = LuaCoroutineHandle.DefaultTotalLifetimeSteps)
        {
            ThrowIfUnsupported();

            Script script = new(SandboxModules);
            RouteDebugPrint(script);
            registry?.ApplyToGlobals(script.Globals);

            InstructionLimitDebugger debugger = new(budgetPerResume, 500);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);

            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_coroutine");
            DynValue coroutine = script.CreateCoroutine(fn);

            return new LuaCoroutineHandle(script, coroutine, debugger, budgetPerResume, totalLifetimeSteps);
        }

        /// <summary>
        /// Runs a small set of sandbox invariants (host callback marshalling, stripped globals, string.rep and
        /// string.format caps) and returns a human-readable PASS/FAIL report. Intended for a WebGL-player self-test scene where
        /// EditMode/PlayMode test runners are unavailable. The report string contains no MoonSharp types,
        /// so non-Lua assemblies can display it. Returns <c>true</c> when every check passes.
        /// </summary>
        public static bool TryRunSelfTest(out string report)
        {
            System.Text.StringBuilder sb = new();
            bool allPassed = true;

            void Check(string name, System.Func<bool> body)
            {
                bool ok;
                string detail = "";
                try
                {
                    ok = body();
                }
                catch (Exception ex)
                {
                    ok = false;
                    detail = " (" + ex.GetType().Name + ": " + ex.Message + ")";
                }

                allPassed &= ok;
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name + detail);
            }

            if (!IsSupported)
            {
                report = "Lua sandbox is not supported on this player (IsSupported == false).";
                return false;
            }

            SecureLuaEnvironment env = new();

            Check("host callback marshalling (host_add(2,3) == 5)", () =>
            {
                LuaApiRegistry registry = new();
                registry.Register("host_add", (System.Func<double, double, double>)((a, b) => a + b));
                Script script = env.CreateScript(registry);
                DynValue result = env.RunChunk(script, "return host_add(2, 3)");
                return result.Type == DataType.Number && System.Math.Abs(result.Number - 5d) < 0.0001d;
            });

            Check("risky globals stripped (os/io/require are nil)", () =>
            {
                Script script = env.CreateScript(null);
                DynValue result = env.RunChunk(script, "return (os == nil) and (io == nil) and (require == nil)");
                return result.Type == DataType.Boolean && result.Boolean;
            });

            Check("string.rep length cap is enforced", () =>
            {
                Script script = env.CreateScript(null);
                try
                {
                    env.RunChunk(script, "return string.rep('a', 5000000)");
                    return false; // should have thrown
                }
                catch (ScriptRuntimeException)
                {
                    return true;
                }
            });

            Check("string.format width cap is enforced", () =>
            {
                Script script = env.CreateScript(null);
                try
                {
                    env.RunChunk(script, "return string.format('%999999999d', 1)");
                    return false; // should have thrown
                }
                catch (ScriptRuntimeException)
                {
                    return true;
                }
            });

            report = sb.ToString().TrimEnd();
            return allPassed;
        }

        private static void ThrowIfUnsupported()
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException(
                    "CoreAI MoonSharp Lua sandbox is disabled on this WebGL player. " +
                    "Set ICoreAISettings.EnableLuaOnWebGl = true (CoreAISettingsAsset) to opt in after verifying the IL2CPP build.");
            }
        }

        private static void StripRiskyGlobals(Script script)
        {
            Table g = script.Globals;

            void Remove(string name)
            {
                try
                {
                    g[name] = DynValue.Nil;
                }
                catch
                {
                }
            }

            Remove("load");
            Remove("loadfile");
            Remove("dofile");
            Remove("require");
            Remove("io");
            Remove("os");
            Remove("debug");

            // Preset_HardSandbox still leaves a 'package' table behind; it is useless without
            // require/loadfile but its loaders/paths shape is an information channel — deny it.
            Remove("package");

            // collectgarbage is a timing/heap oracle even when stubbed; deny it entirely.
            Remove("collectgarbage");

            // string.dump exposes bytecode of functions (information leak / deserialization vector).
            // The shared string metatable __index points at this same table, so removing it here
            // also blocks ('').dump-style access through the string metatable.
            DynValue stringLib = g.Get("string");
            if (stringLib.Type == DataType.Table)
            {
                try
                {
                    stringLib.Table["dump"] = DynValue.Nil;
                    stringLib.Table["rep"] = DynValue.NewCallback(CappedStringRep, "rep");

                    // string.format("%999999999d", ...) allocates a huge padded string in one VM
                    // instruction (the same allocation-bomb class the rep cap defends against, and the
                    // instruction-limit debugger cannot interrupt it). Wrap it so oversized width or
                    // precision specifiers are rejected before the underlying formatter allocates,
                    // delegating to the original implementation otherwise. Replacing it in the string
                    // library table also covers method-style calls (('%d'):format(n)) because the shared
                    // string metatable __index points here.
                    DynValue originalFormat = stringLib.Table.Get("format");
                    if (originalFormat.Type == DataType.Function ||
                        originalFormat.Type == DataType.ClrFunction)
                    {
                        stringLib.Table["format"] = DynValue.NewCallback(
                            (ctx, args) => CappedStringFormat(ctx, args, originalFormat), "format");
                    }
                }
                catch
                {
                }
            }
        }

        // string.rep replacement: identical semantics (s, n[, sep]) but refuses to build strings
        // longer than MaxStringRepLength. Replacing it in the string library table also covers
        // method-style calls (('a'):rep(n)) because the shared string metatable __index points here.
        private static DynValue CappedStringRep(ScriptExecutionContext ctx, CallbackArguments args)
        {
            string s = args.AsType(0, "rep", DataType.String, false).String;
            double countRaw = args.AsType(1, "rep", DataType.Number, false).Number;
            string sep = args.Count >= 3 && args[2].Type == DataType.String ? args[2].String : "";

            if (double.IsNaN(countRaw) || countRaw < 1)
            {
                return DynValue.NewString("");
            }

            long count = countRaw > MaxStringRepLength ? MaxStringRepLength + 1L : (long)countRaw;
            long total = s.Length * count + sep.Length * (count - 1);
            if (total > MaxStringRepLength)
            {
                throw new ScriptRuntimeException(
                    $"SecureLuaEnvironment: string.rep result would exceed {MaxStringRepLength} chars.");
            }

            System.Text.StringBuilder sb = new((int)total);
            for (long i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }

                sb.Append(s);
            }

            return DynValue.NewString(sb.ToString());
        }

        // string.format guard: rejects any conversion specifier whose width or precision exceeds
        // MaxStringFormatLength (e.g. "%999999999d" or "%.1000000f"), then delegates the actual
        // formatting to the original string.format. Parsing the spec lets us refuse the allocation
        // before it happens rather than after a huge string is already built.
        private static DynValue CappedStringFormat(
            ScriptExecutionContext ctx, CallbackArguments args, DynValue originalFormat)
        {
            if (args.Count >= 1 && args[0].Type == DataType.String)
            {
                EnsureFormatWidthWithinCap(args[0].String);
            }

            return ctx.Call(originalFormat, args.GetArray());
        }

        // Scans a printf-style format string for conversion specifiers ("%[flags][width][.precision]conv")
        // and throws if any width or precision field requests more characters than MaxStringFormatLength.
        // "%%" is a literal percent and is skipped. Non-numeric or malformed specs are left for the
        // underlying formatter to handle.
        private static void EnsureFormatWidthWithinCap(string format)
        {
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%')
                {
                    continue;
                }

                i++;
                if (i >= format.Length)
                {
                    break;
                }

                if (format[i] == '%')
                {
                    continue; // "%%" literal percent
                }

                // Skip flag characters (-, +, space, #, 0).
                while (i < format.Length && "-+ #0".IndexOf(format[i]) >= 0)
                {
                    i++;
                }

                // Width field.
                i = CheckNumericField(format, i);

                // Precision field (".<digits>").
                if (i < format.Length && format[i] == '.')
                {
                    i++;
                    i = CheckNumericField(format, i);
                }

                // 'i' now points at (or just past) the conversion char; the outer loop's i++ advances.
            }
        }

        // Reads a run of decimal digits starting at 'start', throws if its value exceeds
        // MaxStringFormatLength, and returns the index just past the digits.
        private static int CheckNumericField(string format, int start)
        {
            int i = start;
            long value = 0;
            bool hasDigits = false;
            while (i < format.Length && format[i] >= '0' && format[i] <= '9')
            {
                hasDigits = true;
                value = value * 10 + (format[i] - '0');
                if (value > MaxStringFormatLength)
                {
                    throw new ScriptRuntimeException(
                        $"SecureLuaEnvironment: string.format width/precision exceeds {MaxStringFormatLength} chars.");
                }

                i++;
            }

            return hasDigits ? i : start;
        }
    }
}
#endif