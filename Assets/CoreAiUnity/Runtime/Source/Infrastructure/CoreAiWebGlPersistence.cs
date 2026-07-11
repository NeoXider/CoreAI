using System.Runtime.InteropServices;

namespace CoreAI.Infrastructure
{
    /// <summary>
    /// Shared WebGL IDBFS-to-IndexedDB flush helper for CoreAI file-backed stores. Wraps the single
    /// <c>CoreAi_PersistFsSync</c> jslib export (<c>CoreAiPersistFs.jslib</c>) so callers share one
    /// <c>DllImport</c> declaration instead of redeclaring it per store.
    /// </summary>
    public static class CoreAiWebGlPersistence
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void CoreAi_PersistFsSync();
#endif

        /// <summary>
        /// On WebGL pushes the in-memory IDBFS tree into IndexedDB so a preceding write survives a
        /// reload or tab close that never runs <c>Application.Quit</c>. On other platforms this is a
        /// no-op (the OS filesystem is already durable once the write call returns).
        /// </summary>
        public static void Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { CoreAi_PersistFsSync(); } catch { /* best-effort flush */ }
#endif
        }
    }
}