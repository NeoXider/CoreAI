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
            registry?.ApplyToGlobals(script.Globals);

            // Attach a debugger before loading host code so instruction limits cover all execution.
            InstructionLimitDebugger debugger = new(OneShotHardLimitSteps, 2000);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);
            return script;
        }

        /// <summary>Runs Lua code inside a secured script with the optional execution guard.</summary>
        public DynValue RunChunk(Script script, string luaCode, LuaExecutionGuard guard = null)
        {
            ThrowIfUnsupported();

            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_chunk");
            guard ??= new LuaExecutionGuard();
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
            registry?.ApplyToGlobals(script.Globals);

            InstructionLimitDebugger debugger = new(budgetPerResume, 500);
            script.AttachDebugger(debugger);

            StripRiskyGlobals(script);

            DynValue fn = script.LoadString(luaCode, codeFriendlyName: "sandbox_coroutine");
            DynValue coroutine = script.CreateCoroutine(fn);

            return new LuaCoroutineHandle(script, coroutine, debugger, budgetPerResume, totalLifetimeSteps);
        }

        /// <summary>
        /// Runs a small set of sandbox invariants (host callback marshalling, stripped globals, string.rep cap)
        /// and returns a human-readable PASS/FAIL report. Intended for a WebGL-player self-test scene where
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
    }
}
#endif