#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// WebGL player-only smoke tests for the native .jslib bridge. These tests must run in a
    /// browser/player build; in the Editor the __Internal functions do not exist.
    /// </summary>
    public sealed class CoreAiSseFetchWebGlBridgePlayModeTests
    {
        private sealed class CallbackState
        {
            public string Chunk = "";
            public string Error = "";
            public bool Done;
        }

        private static readonly Dictionary<int, CallbackState> States = new();
        private static int _nextId;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ChunkCallback(int id, IntPtr strPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DoneCallback(int id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ErrorCallback(int id, IntPtr errPtr);

        private static readonly ChunkCallback OnChunkDelegate = OnChunkStatic;
        private static readonly DoneCallback OnDoneDelegate = OnDoneStatic;
        private static readonly ErrorCallback OnErrorDelegate = OnErrorStatic;

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseSelfTest(
            int callId,
            string payload,
            IntPtr onChunk,
            IntPtr onDone,
            IntPtr onError);

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseAbort(int callId);

        [UnityTest]
        public IEnumerator BridgeSelfTest_RoundTripsPayloadAndDone()
        {
            int id = Interlocked.Increment(ref _nextId);
            const string payload =
                "data: {\"choices\":[{\"delta\":{\"content\":\"webgl-ok\"}}]}\n\n" +
                "data: [DONE]\n\n";
            CallbackState state = Register(id);

            CoreAi_FetchSseSelfTest(
                id,
                payload,
                Marshal.GetFunctionPointerForDelegate(OnChunkDelegate),
                Marshal.GetFunctionPointerForDelegate(OnDoneDelegate),
                Marshal.GetFunctionPointerForDelegate(OnErrorDelegate));

            yield return WaitForBridge(id, state, 5f);

            Assert.IsTrue(string.IsNullOrEmpty(state.Error), state.Error);
            Assert.IsTrue(state.Done, "JS bridge must call the C# done callback.");
            Assert.AreEqual(payload, state.Chunk, "JS bridge must pass UTF-8 payload text back to C# unchanged.");

            Unregister(id);
        }

        [UnityTest]
        public IEnumerator AbortWithoutActiveController_DoesNotThrow()
        {
            int unusedId = -Interlocked.Increment(ref _nextId);

            Assert.DoesNotThrow(() => CoreAi_FetchSseAbort(unusedId));

            // Abort is intentionally deferred through setTimeout so browser-side exceptions cannot
            // bubble into the Unity event that requested cancellation.
            yield return null;
            yield return null;
        }

        [Test]
        public void LuaSandbox_IsExplicitlyDisabledOnWebGl()
        {
            SecureLuaEnvironment env = new();

            Assert.IsFalse(SecureLuaEnvironment.IsSupported);
            Assert.Throws<PlatformNotSupportedException>(() => env.CreateScript(null));
        }

        private static CallbackState Register(int id)
        {
            CallbackState state = new();
            lock (States)
            {
                States[id] = state;
            }

            return state;
        }

        private static void Unregister(int id)
        {
            lock (States)
            {
                States.Remove(id);
            }
        }

        private static IEnumerator WaitForBridge(int id, CallbackState state, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!state.Done && string.IsNullOrEmpty(state.Error) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!state.Done && string.IsNullOrEmpty(state.Error))
            {
                Unregister(id);
                Assert.Fail("Timed out waiting for CoreAi_FetchSseSelfTest JS callbacks.");
            }
        }

        [MonoPInvokeCallback(typeof(ChunkCallback))]
        private static void OnChunkStatic(int id, IntPtr strPtr)
        {
            if (!TryGet(id, out CallbackState state))
            {
                return;
            }

            state.Chunk += Marshal.PtrToStringUTF8(strPtr) ?? "";
        }

        [MonoPInvokeCallback(typeof(DoneCallback))]
        private static void OnDoneStatic(int id)
        {
            if (TryGet(id, out CallbackState state))
            {
                state.Done = true;
            }
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnErrorStatic(int id, IntPtr errPtr)
        {
            if (TryGet(id, out CallbackState state))
            {
                state.Error = Marshal.PtrToStringUTF8(errPtr) ?? "unknown bridge error";
            }
        }

        private static bool TryGet(int id, out CallbackState state)
        {
            lock (States)
            {
                return States.TryGetValue(id, out state);
            }
        }
    }
}
#endif