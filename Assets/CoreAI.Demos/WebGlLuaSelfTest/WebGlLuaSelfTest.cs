#if !COREAI_NO_LUA
using System;
using System.Text;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>
    /// Runtime self-test for the Lua sandbox, meant for a WebGL-player build where EditMode/PlayMode
    /// runners are unavailable. On <see cref="Start"/> it exercises a small set of sandbox invariants
    /// against the Lua-CSharp sandbox (through the CLR-only <see cref="LuaCsGameToolExecutor"/> so this
    /// demo assembly never needs a direct reference to the Lua VM assembly) and logs + renders a
    /// PASS/FAIL report. Attach to a GameObject in a scene and build to WebGL to verify the managed
    /// sandbox survives IL2CPP stripping.
    /// </summary>
    public sealed class WebGlLuaSelfTest : MonoBehaviour
    {
        private string _report = "Lua self-test: not run yet.";
        private bool _passed;

        private void Start()
        {
            _passed = TryRunSelfTest(out string report);
            _report = report;
            if (_passed)
            {
                Debug.Log("[CoreAI][WebGlLuaSelfTest] PASS\n" + _report);
            }
            else
            {
                Debug.LogError("[CoreAI][WebGlLuaSelfTest] FAIL\n" + _report);
            }
        }

        /// <summary>Registers the host callbacks the self-test chunks call (CLR-only surface).</summary>
        private sealed class SelfTestBindings : ILuaCsGameRuntimeBindings
        {
            public void RegisterGameplayApis(LuaCsApiRegistry registry)
            {
                registry.Register("host_add", new Func<double, double, double>((a, b) => a + b));
            }
        }

        /// <summary>
        /// Runs a small set of sandbox invariants (host callback marshalling, stripped globals,
        /// string.rep and string.format caps) against the Lua-CSharp sandbox and returns a human-readable
        /// PASS/FAIL report. The report string contains no Lua VM types, so non-Lua assemblies can display
        /// it. Returns <c>true</c> when every check passes.
        /// </summary>
        private static bool TryRunSelfTest(out string report)
        {
            StringBuilder sb = new();
            bool allPassed = true;

            void Check(string name, Func<bool> body)
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

            if (!LuaCsGameToolExecutor.IsSupported)
            {
                report = "Lua sandbox is not supported on this player (IsSupported == false).";
                return false;
            }

            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                new SelfTestBindings(),
                new NullLuaExecutionObserver());

            // ExecuteAsync completes synchronously (RunChunk drives the VM under a guard and returns a
            // finished Task), so blocking here cannot deadlock — there is no async continuation to await.
            LuaTool.LuaResult Run(string code) =>
                executor.ExecuteAsync(code, CancellationToken.None).GetAwaiter().GetResult();

            Check("host callback marshalling (host_add(2,3) == 5)", () =>
            {
                LuaTool.LuaResult r = Run("return host_add(2, 3)");
                return r.Success && r.Output == "5";
            });

            Check("risky globals stripped (os/io/require are nil)", () =>
            {
                LuaTool.LuaResult r = Run("return (os == nil) and (io == nil) and (require == nil)");
                return r.Success && r.Output == "true";
            });

            Check("string.rep length cap is enforced", () =>
            {
                // The sandbox aborts the oversized allocation; the executor reports failure (no throw).
                LuaTool.LuaResult r = Run("return string.rep('a', 5000000)");
                return !r.Success;
            });

            Check("string.format width cap is enforced", () =>
            {
                LuaTool.LuaResult r = Run("return string.format('%2000000d', 5)");
                return !r.Success;
            });

            report = sb.ToString();
            return allPassed;
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 520, 240), GUI.skin.box);
            GUILayout.Label("CoreAI Lua sandbox self-test: " + (_passed ? "PASS" : "FAIL"));
            GUILayout.Label(_report);
            GUILayout.EndArea();
        }
    }
}
#else
using UnityEngine;

namespace CoreAI.Demos
{
    /// <summary>No-op fallback when the Lua runtime is stripped (no-lua build: COREAI_NO_LUA).</summary>
    public sealed class WebGlLuaSelfTest : MonoBehaviour
    {
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 520, 60), GUI.skin.box);
            GUILayout.Label("CoreAI Lua sandbox self-test unavailable: COREAI_NO_LUA build.");
            GUILayout.EndArea();
        }
    }
}
#endif
