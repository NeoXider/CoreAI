using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using UnityEngine;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Инструмент MEAI для инспекции и манипуляции с иерархией сцены во время PlayMode (Runtime).
    /// Выполняет все операции в главном потоке (Main Thread) через UniTask, чтобы избежать исключений Unity.
    /// </summary>
    public sealed class SceneLlmTool : ILlmTool
    {
        public string Name => "scene_tool";
        public string Description => "Manipulate and inspect Unity GameObjects dynamically at runtime.";
        public bool AllowDuplicates => false;
        public string ParametersSchema => "{}";

        public IEnumerable<AIFunction> CreateAIFunctions()
        {
            yield return AIFunctionFactory.Create(
                (Func<string, string, bool, CancellationToken, Task<string>>)FindObjectsAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "find_objects",
                    Description =
                        "Find game objects in the scene by name or tag. Returns a JSON array of their details."
                }
            );

            yield return AIFunctionFactory.Create(
                (Func<int?, CancellationToken, Task<string>>)GetHierarchyAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "get_hierarchy",
                    Description =
                        "Get the child hierarchy for a given GameObject instanceId. If null or 0, returns root objects."
                }
            );

            yield return AIFunctionFactory.Create(
                (Func<int, CancellationToken, Task<string>>)GetTransformAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "get_transform",
                    Description = "Get the world position, rotation (Euler), and local scale of a GameObject."
                }
            );

            yield return AIFunctionFactory.Create(
                (Func<int, float?, float?, float?, float?, float?, float?, float?, float?, float?, CancellationToken,
                    Task<string>>)SetTransformAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "set_transform",
                    Description =
                        "Move, rotate, or scale a GameObject by its instanceId. Pass values for coordinates you want to change."
                } // parameters: id, px,py,pz, rx,ry,rz, sx,sy,sz
            );
        }

        private async Task<string> FindObjectsAsync(
            string searchTerm,
            string searchMethod = "name",
            bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                IEnumerable<GameObject> allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                    includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

                List<object> results = new();
                foreach (GameObject go in allObjects)
                {
                    bool match = false;
                    if (searchMethod.Equals("name", StringComparison.OrdinalIgnoreCase) && go.name.Contains(searchTerm))
                    {
                        match = true;
                    }
                    else if (searchMethod.Equals("tag", StringComparison.OrdinalIgnoreCase) &&
                             go.CompareTag(searchTerm))
                    {
                        match = true;
                    }

                    if (match)
                    {
                        results.Add(new
                        {
                            instanceId = go.GetInstanceID(),
                            name = go.name,
                            tag = go.tag,
                            parent = go.transform.parent != null ? go.transform.parent.name : null
                        });
                    }
                }

                return SerializeSuccess(results);
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private async Task<string> GetHierarchyAsync(
            int? rootInstanceId = null,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                List<object> children = new();

                if (rootInstanceId.HasValue && rootInstanceId.Value != 0)
                {
                    GameObject root = FindObjectById(rootInstanceId.Value);
                    if (root == null)
                    {
                        return SerializeError($"GameObject with ID {rootInstanceId.Value} not found.");
                    }

                    for (int i = 0; i < root.transform.childCount; i++)
                    {
                        Transform child = root.transform.GetChild(i);
                        children.Add(new
                        {
                            instanceId = child.gameObject.GetInstanceID(),
                            name = child.name,
                            childCount = child.childCount
                        });
                    }
                }
                else
                {
                    // Return roots
                    IEnumerable<GameObject> roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                        .GetRootGameObjects();
                    foreach (GameObject r in roots)
                    {
                        children.Add(new
                        {
                            instanceId = r.GetInstanceID(),
                            name = r.name,
                            childCount = r.transform.childCount
                        });
                    }
                }

                return SerializeSuccess(children);
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private async Task<string> GetTransformAsync(
            int instanceId,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                GameObject go = FindObjectById(instanceId);
                if (go == null)
                {
                    return SerializeError($"GameObject with ID {instanceId} not found.");
                }

                Transform t = go.transform;
                var res = new
                {
                    position = new { x = t.position.x, y = t.position.y, z = t.position.z },
                    rotation = new { x = t.eulerAngles.x, y = t.eulerAngles.y, z = t.eulerAngles.z },
                    scale = new { x = t.localScale.x, y = t.localScale.y, z = t.localScale.z }
                };

                return SerializeSuccess(res);
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private async Task<string> SetTransformAsync(
            int instanceId,
            float? px, float? py, float? pz,
            float? rx, float? ry, float? rz,
            float? sx, float? sy, float? sz,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                GameObject go = FindObjectById(instanceId);
                if (go == null)
                {
                    return SerializeError($"GameObject with ID {instanceId} not found.");
                }

                Transform t = go.transform;
                Vector3 pos = t.position;
                Vector3 rot = t.eulerAngles;
                Vector3 scl = t.localScale;

                if (px.HasValue)
                {
                    pos.x = px.Value;
                }

                if (py.HasValue)
                {
                    pos.y = py.Value;
                }

                if (pz.HasValue)
                {
                    pos.z = pz.Value;
                }

                if (rx.HasValue)
                {
                    rot.x = rx.Value;
                }

                if (ry.HasValue)
                {
                    rot.y = ry.Value;
                }

                if (rz.HasValue)
                {
                    rot.z = rz.Value;
                }

                if (sx.HasValue)
                {
                    scl.x = sx.Value;
                }

                if (sy.HasValue)
                {
                    scl.y = sy.Value;
                }

                if (sz.HasValue)
                {
                    scl.z = sz.Value;
                }

                t.position = pos;
                t.eulerAngles = rot;
                t.localScale = scl;

                return SerializeSuccess("Transform updated successfully.");
            }
            catch (Exception ex)
            {
                return SerializeError(ex.Message);
            }
            finally
            {
                await UniTask.SwitchToThreadPool();
            }
        }

        private GameObject FindObjectById(int instanceId)
        {
            GameObject[] allObjects =
                UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (GameObject go in allObjects)
            {
                if (go.GetInstanceID() == instanceId)
                {
                    return go;
                }
            }

            return null;
        }

        private string SerializeSuccess(object data)
        {
            return JsonConvert.SerializeObject(new { success = true, data });
        }

        private string SerializeError(string error)
        {
            return JsonConvert.SerializeObject(new { success = false, error });
        }
    }
}