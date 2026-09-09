using System;
using System.Threading;
using Cysharp.Threading.Tasks;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace CoreAI.Infrastructure
{
    /// <summary>
    /// Durability acknowledgement for CoreAI's file-backed stores on Unity WebGL.
    /// <para>
    /// <b>Who persists.</b> The engine does. With <c>config.autoSyncPersistentDataPath = true</c> in
    /// <c>createUnityInstance()</c>, Unity mounts <c>Application.persistentDataPath</c> as IDBFS with
    /// <c>autoPersist</c> and queues an IndexedDB persist from its own filesystem node hooks on every
    /// write/close, rename, unlink, mkdir and rmdir. A store therefore does not have to - and, since
    /// Unity 6.3, must not - drive <c>FS.syncfs</c> by hand.
    /// </para>
    /// <para>
    /// <b>Why the manual channel is gone.</b> CoreAI used to call <c>FS.syncfs(false, callback)</c>
    /// from <c>CoreAiPersistFs.jslib</c> and await the callback. In a served WebGL player on
    /// 2026-09-09 that call neither threw nor ever called back, so every awaited confirmation parked
    /// forever and <c>memory action=write</c> reported a false failure after the 30 s tool timeout -
    /// while the bytes were in fact already durable. The engine prints the matching deprecation at
    /// boot. The dead channel is deleted, not kept behind a flag.
    /// </para>
    /// <para>
    /// <b>What "true" means here.</b> Exactly this: the engine's automatic persistence is armed, so
    /// the completed file write has been handed to it and will be flushed to IndexedDB. It does NOT
    /// mean the IndexedDB transaction has committed - Unity exposes no completion signal for that,
    /// and inventing one is what this class stopped doing. <c>false</c> means the opposite of a
    /// durability claim: the page did not enable automatic persistence, so the write lives only in
    /// the tab's in-memory filesystem and dies with the tab. Callers turn that into a visible error.
    /// Nothing here waits, so no caller can hang on a confirmation that never arrives.
    /// </para>
    /// <para>
    /// Outside a WebGL player every member is a no-op returning <c>true</c>: the OS filesystem is
    /// durable once the write call returns.
    /// </para>
    /// </summary>
    public static class CoreAiWebGlPersistence
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private static bool _misconfigurationReported;

        [DllImport("__Internal")]
        private static extern int CoreAi_PersistFsAutoSyncEnabled();
#endif

        /// <summary>
        /// True when writes under <c>Application.persistentDataPath</c> are persisted automatically by
        /// the engine. Always true outside a WebGL player. In a WebGL player it is false until the web
        /// template passes <c>config.autoSyncPersistentDataPath = true</c> to
        /// <c>createUnityInstance()</c>; <c>CoreAIWebGlPersistentDataSyncBuildGuard</c> fails the build
        /// before a player can ship without it.
        /// </summary>
        public static bool IsAutoSyncEnabled
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                try
                {
                    return CoreAi_PersistFsAutoSyncEnabled() != 0;
                }
                catch (Exception)
                {
                    return false;
                }
#else
                return true;
#endif
            }
        }

        /// <summary>
        /// Reports whether the completed write is covered by durable storage. Returns immediately; it
        /// starts nothing and waits for nothing. See the type documentation for the exact meaning of
        /// each outcome.
        /// </summary>
        /// <returns>
        /// True when the write is durable (non-WebGL) or has been handed to the engine's automatic
        /// persistence (WebGL). False only when a WebGL page never armed that persistence.
        /// </returns>
        public static bool Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (IsAutoSyncEnabled)
            {
                return true;
            }

            ReportMisconfigurationOnce();
            return false;
#else
            return true;
#endif
        }

        /// <summary>
        /// Asynchronous shape of <see cref="Sync"/> for callers that already await their persistence
        /// step. It completes synchronously with the same answer - there is no confirmation callback to
        /// wait for, and a wait that cannot end is what this class was fixed to remove.
        /// </summary>
        public static UniTask<bool> SyncAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.FromResult(Sync());
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // WHY: Reported once, but Sync() keeps returning false on every call. A repeated log would
        // flood a save-heavy session; a single "recovered" answer would hide the data loss. The
        // caller's own failure (IOException, refused tool result) stays visible per write.
        private static void ReportMisconfigurationOnce()
        {
            if (_misconfigurationReported)
            {
                return;
            }

            _misconfigurationReported = true;
            UnityEngine.Debug.LogError(
                "[CoreAiWebGlPersistence] This page did not enable automatic persistentDataPath " +
                "synchronization, so nothing written by CoreAI will survive a reload. Pass " +
                "config.autoSyncPersistentDataPath = true to createUnityInstance() in the web " +
                "template this player was built with, or install the one CoreAI ships with the " +
                "menu item 'CoreAI/Setup/Install WebGL Template' and rebuild.");
        }
#endif
    }
}
