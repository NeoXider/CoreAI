using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Serializable role-to-profile assignment.</summary>
    public sealed class LlmPersistedRoleProfile
    {
        public string RolePattern { get; set; } = "";
        public string ProfileId { get; set; } = "";
    }

    /// <summary>Versioned persisted endpoint registry state without session credentials.</summary>
    public sealed class LlmEndpointRegistryState
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public IReadOnlyList<LlmEndpointDescriptor> Endpoints { get; set; } = Array.Empty<LlmEndpointDescriptor>();
        public IReadOnlyList<LlmRuntimeProfile> Profiles { get; set; } = Array.Empty<LlmRuntimeProfile>();
        public IReadOnlyList<LlmPersistedRoleProfile> RoleProfiles { get; set; } = Array.Empty<LlmPersistedRoleProfile>();
    }

    /// <summary>Persistence boundary for runtime endpoint configuration.</summary>
    public interface ILlmEndpointRegistryStore
    {
        LlmEndpointRegistryState Load();
        void Save(LlmEndpointRegistryState state);
    }

    /// <summary>JSON endpoint registry store rooted under CoreAI persistent data.</summary>
    public sealed class FileLlmEndpointRegistryStore : ILlmEndpointRegistryStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _path;
        private readonly object _gate = new();

        public FileLlmEndpointRegistryStore(string path = null)
        {
            _path = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    "llm-endpoints.json")
                : path;
        }

        public LlmEndpointRegistryState Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    return new LlmEndpointRegistryState();
                }

                try
                {
                    string json = File.ReadAllText(_path);
                    LlmEndpointRegistryState state = JsonConvert.DeserializeObject<LlmEndpointRegistryState>(
                        json, JsonSettings);
                    return state?.SchemaVersion == LlmEndpointRegistryState.CurrentSchemaVersion
                        ? Sanitize(state)
                        : new LlmEndpointRegistryState();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoreAI] Could not load LLM endpoint registry: {ex.Message}");
                    return new LlmEndpointRegistryState();
                }
            }
        }

        public void Save(LlmEndpointRegistryState state)
        {
            lock (_gate)
            {
                LlmEndpointRegistryState safe = Sanitize(state ?? new LlmEndpointRegistryState());
                string directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(safe, JsonSettings));
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                File.Move(temp, _path);
                CoreAiWebGlPersistence.Sync();
            }
        }

        private static LlmEndpointRegistryState Sanitize(LlmEndpointRegistryState state)
        {
            return new LlmEndpointRegistryState
            {
                SchemaVersion = LlmEndpointRegistryState.CurrentSchemaVersion,
                Endpoints = (state.Endpoints ?? Array.Empty<LlmEndpointDescriptor>())
                    .Where(endpoint => endpoint != null)
                    .Select(CloneDescriptor)
                    .ToArray(),
                Profiles = (state.Profiles ?? Array.Empty<LlmRuntimeProfile>())
                    .Where(profile => profile != null)
                    .Select(CloneProfile)
                    .ToArray(),
                RoleProfiles = (state.RoleProfiles ?? Array.Empty<LlmPersistedRoleProfile>())
                    .Where(assignment => assignment != null)
                    .Select(assignment => new LlmPersistedRoleProfile
                    {
                        RolePattern = assignment.RolePattern?.Trim() ?? "",
                        ProfileId = assignment.ProfileId?.Trim() ?? ""
                    })
                    .ToArray()
            };
        }

        internal static LlmEndpointDescriptor CloneDescriptor(LlmEndpointDescriptor source)
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = source.EndpointId?.Trim() ?? "",
                DisplayName = source.DisplayName ?? "",
                Kind = source.Kind,
                BaseUrl = source.BaseUrl ?? "",
                Model = source.Model ?? "",
                SecretReference = source.SecretReference ?? "",
                Active = source.Active,
                KeepWarm = source.KeepWarm,
                ContextWindowTokens = source.ContextWindowTokens,
                LocalModelPath = source.LocalModelPath ?? "",
                UnityAgentName = source.UnityAgentName ?? "",
                Port = source.Port,
                GpuLayers = source.GpuLayers,
                Remote = source.Remote,
                FlashAttention = source.FlashAttention,
                ParallelSlots = source.ParallelSlots,
                MaxTokens = source.MaxTokens,
                ReasoningMode = source.ReasoningMode,
                ThinkingBudgetTokens = source.ThinkingBudgetTokens,
                ExtraBodyJson = source.ExtraBodyJson ?? ""
            };
        }

        internal static LlmRuntimeProfile CloneProfile(LlmRuntimeProfile source)
        {
            return new LlmRuntimeProfile
            {
                ProfileId = source.ProfileId?.Trim() ?? "",
                DisplayName = source.DisplayName ?? "",
                EndpointId = source.EndpointId?.Trim() ?? "",
                FallbackProfileIds = (source.FallbackProfileIds ?? Array.Empty<string>()).ToArray()
            };
        }
    }
}
