#if UNITY_EDITOR
using System;
using CoreAI.Infrastructure.Llm;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Build-time check that FAILS a player build whose shipped <c>Resources/CoreAISettings</c> asset selects an
    /// LLM mode needing the <c>COREAI_LLM</c> module (any HTTP mode, the local model, or Auto with a model) while the
    /// build target has no <c>COREAI_LLM</c> scripting define. Such a player would compile without any LLM client,
    /// and every request would fail with <c>LlmErrorCode.NotConfigured</c> on the player's device instead of at
    /// build time. Offline, and Auto without a model, build as before.
    /// </summary>
    public sealed class CoreAILlmModuleBuildGuard : IPreprocessBuildWithReport
    {
        /// <inheritdoc />
        public int callbackOrder => 0;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            CoreAISettingsAsset settings = Resources.Load<CoreAISettingsAsset>("CoreAISettings");
            NamedBuildTarget target = ResolveNamedBuildTarget(report);
            string failure = Evaluate(settings, PlayerSettings.GetScriptingDefineSymbols(target), target.TargetName);
            if (failure != null)
            {
                throw new BuildFailedException(failure);
            }
        }

        /// <summary>
        /// The build failure for <paramref name="settings"/> on a target whose defines are
        /// <paramref name="targetDefines"/>, or <c>null</c> when the build may proceed.
        /// </summary>
        internal static string Evaluate(CoreAISettingsAsset settings, string targetDefines, string targetName)
        {
            if (settings == null || HasDefine(targetDefines, LlmModuleGate.LlmDefine) ||
                !LlmModuleGate.RequiresLlmModule(settings))
            {
                return null;
            }

            return $"[CoreAI] Build aborted: CoreAISettings selects the '{settings.ExecutionMode}' LLM mode, but the " +
                   $"'{targetName}' build target has no {LlmModuleGate.LlmDefine} scripting define, so the player " +
                   "would ship without an LLM client and every request would fail with NotConfigured. Add " +
                   $"{LlmModuleGate.LlmDefine} for this target (CoreAI > Setup > Modules > LLM Providers > Enable " +
                   "Providers while it is the active target, or Project Settings > Player > Scripting Define Symbols), " +
                   "or set LLM Mode to Offline if the build is meant to run without a model.";
        }

        /// <summary>Whether the semicolon-separated <paramref name="defines"/> contain <paramref name="define"/>.</summary>
        internal static bool HasDefine(string defines, string define)
        {
            if (string.IsNullOrEmpty(defines))
            {
                return false;
            }

            foreach (string entry in defines.Split(';'))
            {
                if (string.Equals(entry.Trim(), define, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static NamedBuildTarget ResolveNamedBuildTarget(BuildReport report)
        {
            BuildTargetGroup group = report.summary.platformGroup;
            if (group == BuildTargetGroup.Unknown)
            {
                group = BuildPipeline.GetBuildTargetGroup(report.summary.platform);
            }

            if (group == BuildTargetGroup.Standalone &&
                report.summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server)
            {
                return NamedBuildTarget.Server;
            }

            return NamedBuildTarget.FromBuildTargetGroup(group);
        }
    }
}
#endif
