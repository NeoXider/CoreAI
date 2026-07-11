using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiWorldCommandExecutorPlayModeTests
    {
        [UnityTest]
        public IEnumerator WorldLlmTool_ParentedSpawn_DefaultsLocal_AndCanPreserveWorldTransform()
        {
            yield return null;

            string id = Guid.NewGuid().ToString("N");
            string parentName = $"ParentSpace_{id}";
            string localChildName = $"LocalChild_{id}";
            string worldChildName = $"WorldChild_{id}";
            string secondParentName = $"SecondParentSpace_{id}";
            GameObject parent = new(parentName);
            parent.transform.position = new Vector3(10f, 0f, 0f);
            parent.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            GameObject secondParent = new(secondParentName);
            secondParent.transform.position = new Vector3(-5f, 3f, 7f);
            secondParent.transform.rotation = Quaternion.Euler(0f, 120f, 0f);

            CoreAiWorldCommandExecutor executor =
                new(GameLoggerUnscopedFallback.Instance, null, allowPrimitives: true);
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            WorldLlmTool tool = new(executor, settings, GameLoggerUnscopedFallback.Instance);
            GameObject localChild = null;
            GameObject worldChild = null;

            try
            {
                yield return ExecuteToolSuccess(tool.ExecuteAsync("spawn", prefabKey: "cube",
                    targetName: localChildName, x: 1f, y: 2f, z: 3f, stringValue: parentName));
                localChild = GameObject.Find(localChildName);
                Assert.IsNotNull(localChild);
                Assert.AreSame(parent.transform, localChild.transform.parent);
                AssertVector3(new Vector3(1f, 2f, 3f), localChild.transform.localPosition,
                    "default parent-local spawn position");

                yield return ExecuteToolSuccess(tool.ExecuteAsync("spawn", prefabKey: "cube",
                    targetName: worldChildName, x: 1f, y: 2f, z: 3f, fy: 15f, stringValue: parentName,
                    worldPositionStays: true));
                worldChild = GameObject.Find(worldChildName);
                Assert.IsNotNull(worldChild);
                Assert.AreSame(parent.transform, worldChild.transform.parent);
                AssertVector3(new Vector3(1f, 2f, 3f), worldChild.transform.position,
                    "world-preserving spawn position");
                Assert.AreEqual(15f, worldChild.transform.eulerAngles.y, 0.01f);

                yield return ExecuteToolSuccess(tool.ExecuteAsync("change", targetName: localChildName,
                    x: 2f, y: 0.5f, z: -1f, fy: 30f, stringValue: parentName));
                AssertVector3(new Vector3(2f, 0.5f, -1f), localChild.transform.localPosition,
                    "default parent-local change position");
                Assert.AreEqual(30f, localChild.transform.localEulerAngles.y, 0.01f);

                yield return ExecuteToolSuccess(tool.ExecuteAsync("change", targetName: worldChildName,
                    x: -2f, y: 4f, z: 6f, fy: 25f, stringValue: secondParentName,
                    worldPositionStays: true));
                Assert.AreSame(secondParent.transform, worldChild.transform.parent);
                AssertVector3(new Vector3(-2f, 4f, 6f), worldChild.transform.position,
                    "world-preserving change position");
                Assert.AreEqual(25f, worldChild.transform.eulerAngles.y, 0.01f);
            }
            finally
            {
                if (localChild != null)
                {
                    UnityEngine.Object.Destroy(localChild);
                }

                if (worldChild != null)
                {
                    UnityEngine.Object.Destroy(worldChild);
                }

                UnityEngine.Object.Destroy(parent);
                UnityEngine.Object.Destroy(secondParent);
                UnityEngine.Object.Destroy(settings);
            }
        }

        [UnityTest]
        public IEnumerator WorldCommandExecutor_PrimitiveSpawnScaleParentListAndDestroy_IsDeterministic()
        {
            yield return null;

            string id = Guid.NewGuid().ToString("N");
            string childName = $"CoreAiWorldCommandChild_{id}";
            string parentName = $"CoreAiWorldCommandParent_{id}";
            string secondParentName = $"CoreAiWorldCommandSecondParent_{id}";
            string textName = $"CoreAiWorldCommandText_{id}";
            string soundName = $"CoreAiWorldCommandSound_{id}";
            string searchPattern = $"CoreAiWorldCommand";

            CoreAiWorldCommandExecutor executor =
                new(GameLoggerUnscopedFallback.Instance, null, allowPrimitives: true);
            GameObject parent = new(parentName);
            GameObject secondParent = new(secondParentName);
            GameObject textObject = new(textName);
            TextMesh textMesh = textObject.AddComponent<TextMesh>();
            GameObject soundObject = new(soundName);
            AudioSource audioSource = soundObject.AddComponent<AudioSource>();
            audioSource.clip = AudioClip.Create("ding", 64, 1, 8000, false);
            GameObject child = null;

            try
            {
                CoreAiWorldCommandEnvelope spawn = CoreAiWorldCommandEnvelope.Spawn(
                    "cube",
                    childName,
                    new Vector3(2.5f, 1.25f, -3.75f),
                    new Vector3(10f, 45f, 15f),
                    1f,
                    new Vector3(1.25f, 2.5f, 0.75f));

                AssertExecute(executor, spawn, "spawn");
                yield return null;

                child = GameObject.Find(childName);
                Assert.IsNotNull(child);
                Assert.IsNotNull(child.GetComponent<Renderer>());
                AssertVector3(new Vector3(2.5f, 1.25f, -3.75f), child.transform.position, "spawn position");
                Assert.LessOrEqual(
                    Quaternion.Angle(Quaternion.Euler(10f, 45f, 15f), child.transform.rotation),
                    0.01f,
                    "spawn rotation");
                AssertVector3(new Vector3(1.25f, 2.5f, 0.75f), child.transform.localScale, "spawn scale");

                AssertExecute(executor, CoreAiWorldCommandEnvelope.SetColor(childName, "#3366ff"), "set_color");

                Rigidbody rigidbody = child.AddComponent<Rigidbody>();
                rigidbody.useGravity = false;
                AssertExecute(executor, CoreAiWorldCommandEnvelope.ApplyForce(childName, new Vector3(0f, 2f, 0f)),
                    "apply_force");
                AssertExecute(executor, CoreAiWorldCommandEnvelope.SetVelocity(childName, new Vector3(1f, 2f, 3f)),
                    "set_velocity");
                AssertVector3(new Vector3(1f, 2f, 3f), rigidbody.linearVelocity, "set_velocity velocity");

                AssertExecute(executor, CoreAiWorldCommandEnvelope.ShowText(textName, "Ready"), "show_text");
                Assert.AreEqual("Ready", textMesh.text);
                AssertExecute(executor, CoreAiWorldCommandEnvelope.HidePanel(textName), "hide_panel");
                Assert.IsFalse(textObject.activeSelf);
                AssertExecute(executor, CoreAiWorldCommandEnvelope.SetActive(textName, true), "set_active");
                Assert.IsTrue(textObject.activeSelf);

                AssertExecute(executor, CoreAiWorldCommandEnvelope.PlaySound(soundName, "ding", 0.4f), "play_sound");
                AssertExecute(executor, CoreAiWorldCommandEnvelope.SetVolume(soundName, 0.25f), "set_volume");
                Assert.AreEqual(0.25f, audioSource.volume, 0.001f);

                Animation legacyAnimation = child.AddComponent<Animation>();
                AnimationClip clip = new() { name = "Idle" };
                clip.legacy = true;
                legacyAnimation.AddClip(clip, "Idle");
                legacyAnimation.clip = clip;
                AssertExecute(executor, CoreAiWorldCommandEnvelope.ListAnimations(childName), "list_animations");
                Assert.That(executor.LastListedAnimations, Does.Contain("Idle"));
                AssertExecute(executor, CoreAiWorldCommandEnvelope.PlayAnimation(childName, "Idle"), "play_animation");
                AssertExecute(executor, CoreAiWorldCommandEnvelope.StopAnimation(childName), "stop_animation");

                AssertExecute(
                    executor,
                    CoreAiWorldCommandEnvelope.SetScale(childName, 1f, new Vector3(0.5f, 3f, 1.5f)),
                    "set_scale");
                AssertVector3(new Vector3(0.5f, 3f, 1.5f), child.transform.localScale, "set_scale scale");

                AssertExecute(executor, CoreAiWorldCommandEnvelope.Parent(childName, parentName), "parent");
                Assert.AreSame(parent.transform, child.transform.parent);

                AssertExecute(
                    executor,
                    CoreAiWorldCommandEnvelope.Change(
                        childName,
                        new Vector3(0f, 8f, 0f),
                        true,
                        true,
                        true,
                        false,
                        new Vector3(0f, 90f, 0f),
                        true,
                        false,
                        true,
                        false,
                        0f,
                        new Vector3(2f, 0f, 0f),
                        true,
                        secondParentName),
                    "change");
                Assert.AreEqual(0f, child.transform.position.x, 0.001f);
                Assert.AreEqual(8f, child.transform.position.y, 0.001f);
                Assert.AreEqual(-3.75f, child.transform.position.z, 0.001f);
                Assert.AreEqual(90f, child.transform.eulerAngles.y, 0.01f);
                Assert.AreEqual(2f, child.transform.localScale.x, 0.001f);
                Assert.AreEqual(3f, child.transform.localScale.y, 0.001f);
                Assert.AreSame(secondParent.transform, child.transform.parent);

                AssertExecute(executor, CoreAiWorldCommandEnvelope.ListObjects(searchPattern), "list_objects");
                List<Dictionary<string, object>> listed = executor.LastListedObjects;
                Assert.That(listed.Select(o => o["name"] as string), Does.Contain(parentName));
                Assert.That(listed.Select(o => o["name"] as string), Does.Contain(childName));

                Dictionary<string, object> listedParent = listed.Single(o => (string)o["name"] == parentName);
                Dictionary<string, object> listedSecondParent =
                    listed.Single(o => (string)o["name"] == secondParentName);
                Assert.AreEqual(0, (int)listedParent["childCount"]);
                Assert.AreEqual(1, (int)listedSecondParent["childCount"]);

                AssertExecute(executor, CoreAiWorldCommandEnvelope.Parent(childName, "none"), "unparent");
                Assert.IsNull(child.transform.parent);

                AssertExecute(executor, CoreAiWorldCommandEnvelope.Destroy(childName), "destroy");
                yield return null;

                Assert.IsNull(GameObject.Find(childName));
                child = null;
            }
            finally
            {
                if (child != null)
                {
                    UnityEngine.Object.Destroy(child);
                }

                UnityEngine.Object.Destroy(parent);
                UnityEngine.Object.Destroy(secondParent);
                UnityEngine.Object.Destroy(textObject);
                UnityEngine.Object.Destroy(soundObject);
            }
        }

        [UnityTest]
        public IEnumerator WorldLlmTool_PublicWorldTools_ExecuteComplexTaskInPlayMode()
        {
            yield return null;

            string id = Guid.NewGuid().ToString("N");
            string parentName = $"WorldToolParent_{id}";
            string blockName = $"WorldToolBlock_{id}";
            string textName = $"WorldToolText_{id}";
            string soundName = $"WorldToolSound_{id}";

            CoreAiWorldCommandExecutor executor =
                new(GameLoggerUnscopedFallback.Instance, null, allowPrimitives: true);
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            WorldLlmTool tool = new(executor, settings, GameLoggerUnscopedFallback.Instance);
            GameObject parent = new(parentName);
            GameObject textObject = new(textName);
            textObject.AddComponent<TextMesh>();
            GameObject soundObject = new(soundName);
            AudioSource audioSource = soundObject.AddComponent<AudioSource>();
            audioSource.clip = AudioClip.Create("ping", 64, 1, 8000, false);
            GameObject block = null;

            try
            {
                yield return ExecuteToolSuccess(tool.ExecuteAsync(
                    "spawn",
                    targetName: blockName,
                    prefabKey: "cube",
                    x: 1f,
                    y: 2f,
                    z: 3f,
                    fy: 45f,
                    scaleX: 2f,
                    scaleY: 3f,
                    scaleZ: 0.5f,
                    stringValue: parentName));

                block = GameObject.Find(blockName);
                Assert.IsNotNull(block);
                Assert.AreSame(parent.transform, block.transform.parent);
                AssertVector3(new Vector3(1f, 2f, 3f), block.transform.position, "tool spawn position");
                AssertVector3(new Vector3(2f, 3f, 0.5f), block.transform.localScale, "tool spawn scale");
                Assert.LessOrEqual(Quaternion.Angle(Quaternion.Euler(0f, 45f, 0f), block.transform.rotation), 0.01f);

                Rigidbody rigidbody = block.AddComponent<Rigidbody>();
                rigidbody.useGravity = false;
                Animation legacyAnimation = block.AddComponent<Animation>();
                AnimationClip clip = new() { name = "Idle" };
                clip.legacy = true;
                legacyAnimation.AddClip(clip, "Idle");
                legacyAnimation.clip = clip;

                yield return ExecuteToolSuccess(tool.ExecuteAsync(
                    "change",
                    targetName: blockName,
                    x: 0f,
                    scaleY: 4f));
                Assert.AreEqual(0f, block.transform.position.x, 0.001f);
                Assert.AreEqual(2f, block.transform.position.y, 0.001f);
                Assert.AreEqual(4f, block.transform.localScale.y, 0.001f);

                yield return ExecuteToolSuccess(tool.ExecuteAsync("set_color", targetName: blockName,
                    stringValue: "#44aa88"));
                yield return ExecuteToolSuccess(tool.ExecuteAsync("show_text", targetName: textName,
                    textToDisplay: "Complex world tool task"));
                Assert.AreEqual("Complex world tool task", textObject.GetComponent<TextMesh>().text);
                yield return ExecuteToolSuccess(tool.ExecuteAsync("hide_panel", targetName: textName));
                Assert.IsFalse(textObject.activeSelf);
                yield return ExecuteToolSuccess(tool.ExecuteAsync("set_active", targetName: textName));
                Assert.IsTrue(textObject.activeSelf);

                yield return ExecuteToolSuccess(tool.ExecuteAsync("play_sound", targetName: soundName,
                    stringValue: "ping", volume: 0.4f));
                yield return ExecuteToolSuccess(tool.ExecuteAsync("set_volume", targetName: soundName,
                    volume: 0.25f));
                Assert.AreEqual(0.25f, audioSource.volume, 0.001f);

                yield return ExecuteToolSuccess(tool.ExecuteAsync("list_animations", targetName: blockName));
                Assert.That(executor.LastListedAnimations, Does.Contain("Idle"));
                yield return ExecuteToolSuccess(tool.ExecuteAsync("play_animation", targetName: blockName,
                    animationName: "Idle"));
                yield return ExecuteToolSuccess(tool.ExecuteAsync("stop_animation", targetName: blockName));

                yield return ExecuteToolSuccess(tool.ExecuteAsync("apply_force", targetName: blockName,
                    fx: 0f, fy: 2f, fz: 0f));
                // Rigidbody.AddForce(Impulse) queues into the physics engine's force accumulator and only
                // lands in linearVelocity on the next FixedUpdate - unlike TrySetVelocity's direct
                // assignment. WorldLlmTool.ExecuteAsync's UniTask.SwitchToMainThread hop between these two
                // async tool calls (unlike the synchronous executor.TryExecute calls earlier in this test)
                // already lets a physics step slip in, so wait one out explicitly: otherwise the queued
                // impulse can land AFTER set_velocity's assignment and silently add on top of it.
                yield return new WaitForFixedUpdate();
                yield return ExecuteToolSuccess(tool.ExecuteAsync("set_velocity", targetName: blockName,
                    fx: 1f, fy: 2f, fz: 3f));
                AssertVector3(new Vector3(1f, 2f, 3f), rigidbody.linearVelocity, "tool set_velocity velocity");

                yield return ExecuteToolSuccess(tool.ExecuteAsync("list_objects", stringValue: "WorldTool"));
                Assert.That(executor.LastListedObjects.Select(o => o["name"] as string), Does.Contain(blockName));

                yield return ExecuteToolSuccess(tool.ExecuteAsync("destroy", targetName: blockName));
                yield return null;
                Assert.IsNull(GameObject.Find(blockName));
                block = null;
            }
            finally
            {
                if (block != null)
                {
                    UnityEngine.Object.Destroy(block);
                }

                UnityEngine.Object.Destroy(parent);
                UnityEngine.Object.Destroy(textObject);
                UnityEngine.Object.Destroy(soundObject);
                UnityEngine.Object.Destroy(settings);
            }
        }

        private static void AssertExecute(
            CoreAiWorldCommandExecutor executor,
            CoreAiWorldCommandEnvelope envelope,
            string label)
        {
            string json = JsonUtility.ToJson(envelope, false);
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = $"playmode_{label}"
            });

            Assert.IsTrue(executed, $"Expected world command '{label}' to execute. Json: {json}");
        }

        private static IEnumerator ExecuteToolSuccess(Task<string> task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                Assert.Fail(task.Exception?.GetBaseException().Message);
            }

            WorldLlmTool.WorldResult result =
                JsonConvert.DeserializeObject<WorldLlmTool.WorldResult>(task.Result);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.Message);
        }

        private static void AssertVector3(Vector3 expected, Vector3 actual, string label)
        {
            Assert.AreEqual(expected.x, actual.x, 0.001f, $"{label}.x");
            Assert.AreEqual(expected.y, actual.y, 0.001f, $"{label}.y");
            Assert.AreEqual(expected.z, actual.z, 0.001f, $"{label}.z");
        }
    }
}
