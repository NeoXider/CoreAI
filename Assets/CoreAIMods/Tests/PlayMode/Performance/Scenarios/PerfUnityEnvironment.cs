using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Performance
{
    /// <summary>The build, device and commit facts every Unity row is reported with.</summary>
    public static class PerfUnityEnvironment
    {
        /// <summary>Fills <paramref name="report"/> with what this player or Editor is running on.</summary>
        public static void Describe(PerfRunReport report, PerfRunnerOptions options)
        {
            report.SetEnvironment("label", options?.Label ?? "");
            report.SetEnvironment("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            report.SetEnvironment("commit", string.IsNullOrWhiteSpace(options?.Commit)
                ? "unknown (pass -coreaiPerfCommit or COREAI_PERF_COMMIT)"
                : options.Commit);
            report.SetEnvironment("unityVersion", Application.unityVersion);
            report.SetEnvironment("platform", Application.platform.ToString());
            report.SetEnvironment("isEditor", Application.isEditor ? "true" : "false");
            report.SetEnvironment("buildType", Application.isEditor ? "editor" : Debug.isDebugBuild ? "development" : "release");
            report.SetEnvironment("scriptingBackend", ScriptingBackend());
            report.SetEnvironment("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            report.SetEnvironment("processArchitecture",
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            report.SetEnvironment("os", SystemInfo.operatingSystem);
            report.SetEnvironment("deviceModel", SystemInfo.deviceModel);
            report.SetEnvironment("processor", SystemInfo.processorType);
            report.SetEnvironment("processorCount", SystemInfo.processorCount.ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("systemMemoryMb", SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("graphicsDevice", SystemInfo.graphicsDeviceName);
            report.SetEnvironment("graphicsApi", SystemInfo.graphicsDeviceType.ToString());
            report.SetEnvironment("renderPipeline", GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.GetType().Name + " (" + GraphicsSettings.currentRenderPipeline.name + ")"
                : "built-in");
            report.SetEnvironment("qualityLevel", QualitySettings.names.Length > QualitySettings.GetQualityLevel()
                ? QualitySettings.names[QualitySettings.GetQualityLevel()]
                : QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("screen", Screen.width.ToString(CultureInfo.InvariantCulture) + "x"
                                            + Screen.height.ToString(CultureInfo.InvariantCulture));
            report.SetEnvironment("incrementalGc", GarbageCollectorIncremental());
            report.SetEnvironment("measuring", "vSync off and targetFrameRate -1 during each scene; restored afterwards");
        }

        private static string ScriptingBackend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#elif ENABLE_MONO
            return "Mono";
#else
            return "unknown";
#endif
        }

        private static string GarbageCollectorIncremental()
        {
#if UNITY_2019_1_OR_NEWER
            return UnityEngine.Scripting.GarbageCollector.isIncremental ? "true" : "false";
#else
            return "unknown";
#endif
        }
    }
}
