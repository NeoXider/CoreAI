using System;
using CoreAI.Composition;
using CoreAI.Demos.Shared;
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
        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        private IAiGameCommandSink _sink;
        private CoreAiDemoPanel _panel;
        private int _spawned;

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            _panel = CoreAiDemoPanel.Create(
                "CoreAI — World Commands",
                "Publishes JSON world-command envelopes exactly as an AI agent does.\n"
                + "No LLM and no Lua: this is the raw command transport.");

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _panel.Log("CoreAILifetimeScope not found in scene.");
                Debug.LogError("[WorldCommandsDemo] CoreAILifetimeScope not found in scene.");
                enabled = false;
                return;
            }

            _sink = coreAiScope.Container.Resolve<IAiGameCommandSink>();
            _panel.AddButton("Spawn enemy", SpawnEnemy);
            _panel.AddButton("Move Boss", MoveBoss);
            _panel.AddButton("Recolor Boss", RecolorBoss);
            _panel.AddButton("Destroy last", DestroyLast);
            _panel.Log("Ready.");
        }

        private void SpawnEnemy()
        {
            _spawned++;
            Publish(CoreAiWorldCommandEnvelope.Spawn(
                "enemy.basic",
                $"cmd_enemy_{_spawned}",
                new Vector3(UnityEngine.Random.Range(-4f, 4f), 1.5f, 6f)));
        }

        private void MoveBoss()
        {
            Publish(CoreAiWorldCommandEnvelope.Move(
                "Boss",
                new Vector3(UnityEngine.Random.Range(-4f, 4f), 0.5f, 8f)));
        }

        private void RecolorBoss()
        {
            string[] colors = { "#ff3300", "#33ccff", "#aaff33", "#ff66cc" };
            Publish(CoreAiWorldCommandEnvelope.SetColor(
                "Boss",
                colors[UnityEngine.Random.Range(0, colors.Length)]));
        }

        private void DestroyLast()
        {
            if (_spawned <= 0)
            {
                _panel.Log("Nothing spawned yet.");
                return;
            }

            Publish(CoreAiWorldCommandEnvelope.Destroy($"cmd_enemy_{_spawned}"));
            _spawned--;
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
                _panel.Log($"Published: {envelope.action}");
            }
            catch (Exception ex)
            {
                _panel.Log($"Error: {ex.Message}");
                Debug.LogError($"[WorldCommandsDemo] {ex}");
            }
        }

    }
}
