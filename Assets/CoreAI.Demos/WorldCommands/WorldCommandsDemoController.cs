using System;
using CoreAI.Composition;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using UnityEngine;
using VContainer;

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo driver for the AI game-command pipeline: publishes JSON world-command envelopes
    /// into <c>IAiGameCommandSink</c> exactly like AI agents (and the Lua bindings) do, and the
    /// <c>AiGameCommandRouter</c> applies them on the main thread via the world executor.
    /// No LLM and no Lua are required — this is the raw command transport.
    /// </summary>
    public sealed class WorldCommandsDemoController : MonoBehaviour
    {
        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        private IAiGameCommandSink _sink;
        private string _status = "";
        private int _spawned;

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                Debug.LogError($"[WorldCommandsDemo] {_status}");
                enabled = false;
                return;
            }

            _sink = coreAiScope.Container.Resolve<IAiGameCommandSink>();
            _status = "Ready.";
        }

        private void Publish(CoreAiWorldCommandEnvelope envelope)
        {
            try
            {
                _sink.Publish(new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                    JsonPayload = JsonUtility.ToJson(envelope, false),
                    SourceRoleId = "Demo",
                    SourceTaskHint = "world_command",
                    SourceTag = "demo:world_command"
                });
                _status = $"Published: {envelope.action}";
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
                Debug.LogError($"[WorldCommandsDemo] {ex}");
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 420, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("CoreAI - World Commands Demo (AI command pipeline)");
            GUILayout.Label(_status);
            GUILayout.Space(6);

            if (GUILayout.Button("Spawn enemy"))
            {
                _spawned++;
                Publish(CoreAiWorldCommandEnvelope.Spawn(
                    "enemy.basic",
                    $"cmd_enemy_{_spawned}",
                    new Vector3(UnityEngine.Random.Range(-4f, 4f), 1.5f, 6f)));
            }

            if (GUILayout.Button("Move Boss to a random point"))
            {
                Publish(CoreAiWorldCommandEnvelope.Move(
                    "Boss",
                    new Vector3(UnityEngine.Random.Range(-4f, 4f), 0.5f, 8f)));
            }

            if (GUILayout.Button("Recolor Boss"))
            {
                string[] colors = { "#ff3300", "#33ccff", "#aaff33", "#ff66cc" };
                Publish(CoreAiWorldCommandEnvelope.SetColor(
                    "Boss",
                    colors[UnityEngine.Random.Range(0, colors.Length)]));
            }

            if (GUILayout.Button("Destroy last spawned enemy") && _spawned > 0)
            {
                Publish(CoreAiWorldCommandEnvelope.Destroy($"cmd_enemy_{_spawned}"));
                _spawned--;
            }

            GUILayout.EndArea();
        }
    }
}