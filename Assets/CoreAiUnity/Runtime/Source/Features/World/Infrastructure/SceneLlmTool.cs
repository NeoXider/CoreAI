using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// LLM tool that exposes scene inspection and manipulation operations.
    /// </summary>
    public sealed class SceneLlmTool : IAIFunctionsLlmTool
    {
        public string Name => "scene_tool";
        public string Description => "Manipulate and inspect Unity GameObjects dynamically at runtime.";

        public bool AllowDuplicates => false;

        // WHY: This wrapper expands into multiple native MEAI functions. The aggregate ILlmTool schema is
        // intentionally empty because each AIFunction.JsonSchema from CreateAIFunctions is authoritative.
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
            [Description("Name or tag to search for.")]
            string searchTerm,
            [Description("How to match: 'name' (substring of the object name) or 'tag'. Default 'name'.")]
            string searchMethod = "name",
            [Description("When true, also search inactive GameObjects. Default false.")]
            bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            await UniTask.SwitchToMainThread(cancellationToken);
            try
            {
                string method = string.IsNullOrWhiteSpace(searchMethod) ? "name" : searchMethod.Trim();
                if (!string.Equals(method, "name", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "tag", StringComparison.OrdinalIgnoreCase))
                {
                    return SerializeError("searchMethod must be 'name' or 'tag'.");
                }

                string term = searchTerm?.Trim();
                bool listAll = string.IsNullOrWhiteSpace(term);

                IEnumerable<GameObject> allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                    includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

                List<object> results = new();
                foreach (GameObject go in allObjects)
                {
                    bool match = false;
                    if (listAll)
                    {
                        match = true;
                    }
                    else if (string.Equals(method, "name", StringComparison.OrdinalIgnoreCase) &&
                             go.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = true;
                    }
                    else if (string.Equals(method, "tag", StringComparison.OrdinalIgnoreCase) &&
                             go.CompareTag(term))
                    {
                        match = true;
                    }

                    if (match)
                    {
                        results.Add(new
                        {
                            instanceId = GetObjectId(go),
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
            [Description("GameObject instanceId whose children to list. If null or 0, returns root objects.")]
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
                            instanceId = GetObjectId(child.gameObject),
                            name = child.name,
                            childCount = child.childCount
                        });
                    }
                }
                else
                {
                    IEnumerable<GameObject> roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene()
                        .GetRootGameObjects();
                    foreach (GameObject r in roots)
                    {
                        children.Add(new
                        {
                            instanceId = GetObjectId(r),
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
            [Description("instanceId of the GameObject whose transform to read.")]
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
            [Description("instanceId of the GameObject to move, rotate, or scale.")]
            int instanceId,
            // WHY: Default values mark these optional in the MEAI function schema so the model (and callers)
            // can pass only the coordinates they want to change; missing values leave the axis untouched.
            [Description("New world position X. Omit to leave unchanged.")]
            float? px = null,
            [Description("New world position Y. Omit to leave unchanged.")]
            float? py = null,
            [Description("New world position Z. Omit to leave unchanged.")]
            float? pz = null,
            [Description("New Euler rotation X in degrees. Omit to leave unchanged.")]
            float? rx = null,
            [Description("New Euler rotation Y in degrees. Omit to leave unchanged.")]
            float? ry = null,
            [Description("New Euler rotation Z in degrees. Omit to leave unchanged.")]
            float? rz = null,
            [Description("New local scale X. Omit to leave unchanged.")]
            float? sx = null,
            [Description("New local scale Y. Omit to leave unchanged.")]
            float? sy = null,
            [Description("New local scale Z. Omit to leave unchanged.")]
            float? sz = null,
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
                bool hasChange =
                    px.HasValue || py.HasValue || pz.HasValue ||
                    rx.HasValue || ry.HasValue || rz.HasValue ||
                    sx.HasValue || sy.HasValue || sz.HasValue;

                if (!hasChange)
                {
                    return SerializeError(
                        "At least one transform field (px, py, pz, rx, ry, rz, sx, sy, sz) must be provided.");
                }

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
                if (GetObjectId(go) == instanceId)
                {
                    return go;
                }
            }

            return null;
        }

        private static int GetObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

            // WHY: Unity 6.5 made BOTH the EntityId->int implicit cast AND Object.GetInstanceID() obsolete ERRORS
            // (CS0619). EntityId.GetHashCode() yields a stable, session-unique int for the in-session object
            // lookup these ids feed (collisions are negligible at scene-object counts) without any obsolete API.
            return obj.GetEntityId().GetHashCode();
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
