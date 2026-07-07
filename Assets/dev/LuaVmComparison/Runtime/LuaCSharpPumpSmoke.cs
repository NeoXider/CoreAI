using System;
using System.Collections;
using UnityEngine;

namespace LuaVmComparison
{
    /// <summary>
    /// WebGL-safe proof that Lua-CSharp's async VM can be driven WITHOUT blocking the single WASM thread.
    /// On Start it runs a Unity coroutine that pumps <see cref="LuaCSharpRunner.EvalAsync"/> for a handful of
    /// cases INCLUDING a <c>coroutine.yield</c> script (which deadlocks the sync GetResult path on WebGL).
    /// Each case yields back to the player loop every frame until the task completes, letting Unity's
    /// synchronization context advance the awaited continuation. Not wired into any scene here — the
    /// orchestrator attaches it to a scene and builds.
    /// </summary>
    public sealed class LuaCSharpPumpSmoke : MonoBehaviour
    {
        private LuaCSharpRunner _runner;
        private string _summary = "[LuaPump] LUAPUMP_RESULT: (pending)";
        private bool _done;
        private int _reflushes;
        private float _nextReflush;

        private sealed class PumpCase
        {
            public string Name;
            public string Code;
            public string Expected;
        }

        // Coroutine + pcall scripts are copied verbatim from LuaVmBench.cs Correctness corpus.
        private static readonly PumpCase[] Cases =
        {
            new PumpCase { Name = "arithmetic", Code = "return (2+3)*4 - 10/2", Expected = "15" },
            new PumpCase { Name = "recursion_fib15", Code = "local function fib(n) if n<2 then return n end return fib(n-1)+fib(n-2) end return fib(15)", Expected = "610" },
            new PumpCase { Name = "coroutines", Code =
                "local co = coroutine.create(function(a) local b = coroutine.yield(a+1); return b*2 end)\n" +
                "local _, x = coroutine.resume(co, 10)\n" +
                "local _, y = coroutine.resume(co, 5)\n" +
                "return x + y", Expected = "21" },
            new PumpCase { Name = "pcall_error", Code =
                "local ok,err = pcall(function() error('boom') end)\n" +
                "return tostring(ok)..':'..(type(err)=='string' and 'str' or type(err))", Expected = "false:str" },
        };

        private void Start()
        {
            StartCoroutine(PumpAll());
        }

        private IEnumerator PumpAll()
        {
            bool allOk = true;
            try
            {
                _runner = new LuaCSharpRunner();

                foreach (var c in Cases)
                {
                    var task = _runner.EvalAsync(c.Code);
                    while (!task.IsCompleted) yield return null;   // pump: return to player loop each frame
                    string got = task.Result;
                    bool ok = got == c.Expected;
                    allOk &= ok;
                    Debug.Log($"[LuaPump] {c.Name}: '{got}' (expected '{c.Expected}') {(ok ? "OK" : "FAIL")}");
                }
            }
            finally
            {
                _runner?.Dispose();
                _runner = null;
            }

            _summary = "[LuaPump] LUAPUMP_RESULT: " + (allOk
                ? "OK all pumped cases pass (coroutines included)"
                : "FAIL see above");
            Debug.Log(_summary);
            _done = true;
            _nextReflush = Time.time + 1f;
        }

        private void Update()
        {
            // Re-log the summary ~once/second, ~5 times, so it stays at the tail of the browser console.
            if (!_done || _reflushes >= 5) return;
            if (Time.time < _nextReflush) return;
            _nextReflush = Time.time + 1f;
            _reflushes++;
            Debug.Log(_summary);
        }

        private void OnDestroy()
        {
            _runner?.Dispose();
            _runner = null;
        }
    }
}
