using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Минимальный исполнитель: spawn/move/destroy/load_scene. Всё через whitelist registry.</summary>
    public sealed class CoreAiWorldCommandExecutor : ICoreAiWorldCommandExecutor
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiPrefabRegistryAsset _prefabRegistry;

        public CoreAiWorldCommandExecutor(IGameLogger logger, CoreAiPrefabRegistryAsset prefabRegistry = null)
        {
            _logger = logger;
            _prefabRegistry = prefabRegistry;
        }

        public bool TryExecute(ApplyAiGameCommand cmd)
        {
            if (cmd == null || !string.Equals(cmd.CommandTypeId, AiGameCommandTypeIds.WorldCommand,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string json = cmd.JsonPayload ?? "";
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            CoreAiWorldCommandEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] bad json: {ex.Message}");
                return false;
            }

            if (env == null || string.IsNullOrWhiteSpace(env.action))
            {
                return false;
            }

            switch (env.action.Trim())
            {
                case "spawn":
                    return TrySpawn(env);
                case "move":
                    return TryMove(env);
                case "destroy":
                    return TryDestroy(env);
                case "set_active":
                    return TrySetActive(env);
                case "load_scene":
                    return TryLoadScene(env);
                case "reload_scene":
                    return TryReloadScene();
                case "list_objects":
                    return TryListObjects(env);
                case "play_animation":
                    return TryPlayAnimation(env);
                case "list_animations":
                    return TryListAnimations(env);
                case "play_sound":
                    return TryPlaySound(env);
                case "stop_animation":
                    return TryStopAnimation(env);
                case "set_volume":
                    return TrySetVolume(env);
                case "show_text":
                    return TryShowText(env);
                case "hide_panel":
                    return TryHidePanel(env);
                case "update_score":
                    return TryUpdateScore(env);
                case "apply_force":
                    return TryApplyForce(env);
                case "set_velocity":
                    return TrySetVelocity(env);
                default:
                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] unknown action '{env.action}'");
                    return false;
            }
        }

        private bool TrySpawn(CoreAiWorldCommandEnvelope env)
        {
            if (_prefabRegistry == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] prefab registry not assigned");
                return false;
            }

            string key = (env.prefabKeyOrName ?? "").Trim();
            if (!_prefabRegistry.TryResolve(key, out GameObject prefab) || prefab == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] prefab not found: '{key}'");
                return false;
            }

            string targetName = (env.targetName ?? "").Trim();
            if (string.IsNullOrEmpty(targetName))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] spawn missing targetName");
                return false;
            }

            Vector3 pos = new(env.x, env.y, env.z);

            // Валидация позиции спавна (проверка коллизий)
            if (!ValidateSpawnPosition(pos, 0.5f))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] spawn blocked: position ({pos.x},{pos.y},{pos.z}) overlaps existing colliders");
                return false;
            }

            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            go.name = targetName;
            return true;
        }

        /// <summary>
        /// Проверяет, свободна ли позиция для спавна (нет пересечений с существующими коллайдерами).
        /// </summary>
        private bool ValidateSpawnPosition(Vector3 position, float checkRadius)
        {
            Collider[] overlaps = Physics.OverlapSphere(position, checkRadius);
            // Считаем только статические / не-trigger коллайдеры
            foreach (Collider col in overlaps)
            {
                if (!col.isTrigger)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryApplyForce(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] apply_force: object not found (name='{env.targetName}')");
                return false;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] apply_force: no Rigidbody on '{go.name}'");
                return false;
            }

            Vector3 force = new(env.fx, env.fy, env.fz);
            rb.AddForce(force, ForceMode.Impulse);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] apply_force: ({force.x},{force.y},{force.z}) on '{go.name}'");
            return true;
        }

        private bool TrySetVelocity(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_velocity: object not found (name='{env.targetName}')");
                return false;
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_velocity: no Rigidbody on '{go.name}'");
                return false;
            }

            Vector3 velocity = new(env.fx, env.fy, env.fz);
            rb.linearVelocity = velocity;
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] set_velocity: ({velocity.x},{velocity.y},{velocity.z}) on '{go.name}'");
            return true;
        }

        private bool TryStopAnimation(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: object not found (name='{env.targetName}')");
                return false;
            }

            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.enabled)
            {
                animator.StopPlayback();
                animator.speed = 0f;
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: stopped on '{go.name}' (Animator)");
                return true;
            }

            Animation animation = go.GetComponent<Animation>();
            if (animation != null && animation.enabled)
            {
                animation.Stop();
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] stop_animation: stopped on '{go.name}' (Animation)");
                return true;
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] stop_animation: no Animator/Animation on '{go.name}'");
            return false;
        }

        private bool TrySetVolume(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_volume: object not found (name='{env.targetName}')");
                return false;
            }

            AudioSource[] sources = go.GetComponents<AudioSource>();
            if (sources == null || sources.Length == 0)
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] set_volume: no AudioSource on '{go.name}'");
                return false;
            }

            float volume = Mathf.Clamp01(env.floatValue);
            foreach (AudioSource src in sources)
            {
                src.volume = volume;
            }

            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] set_volume: {volume} on '{go.name}' ({sources.Length} sources)");
            return true;
        }

        private bool TryShowText(CoreAiWorldCommandEnvelope env)
        {
            if (string.IsNullOrEmpty(env.stringValue))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] show_text: text is empty");
                return false;
            }

            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] show_text: object not found (name='{env.targetName}')");
                return false;
            }

            // Пробуем UI Text (Canvas)
            UnityEngine.UI.Text uiText = go.GetComponent<UnityEngine.UI.Text>();
            if (uiText != null)
            {
                uiText.text = env.stringValue;
                go.SetActive(true);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] show_text: UI.Text set on '{go.name}'");
                return true;
            }

            // Пробуем TextMesh (3D)
            TextMesh textMesh = go.GetComponent<TextMesh>();
            if (textMesh != null)
            {
                textMesh.text = env.stringValue;
                go.SetActive(true);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] show_text: TextMesh set on '{go.name}'");
                return true;
            }

            // Если нет компонента текста — создаём TextMesh
            TextMesh newMesh = go.AddComponent<TextMesh>();
            newMesh.text = env.stringValue;
            newMesh.fontSize = 24;
            newMesh.characterSize = 0.1f;
            go.SetActive(true);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] show_text: TextMesh created on '{go.name}'");
            return true;
        }

        private bool TryHidePanel(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] hide_panel: object not found (name='{env.targetName}')");
                return false;
            }

            go.SetActive(false);
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] hide_panel: '{go.name}' deactivated");
            return true;
        }

        private bool TryUpdateScore(CoreAiWorldCommandEnvelope env)
        {
            if (string.IsNullOrEmpty(env.stringValue))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, "[World] update_score: text is empty");
                return false;
            }

            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe,
                    $"[World] update_score: object not found (name='{env.targetName}')");
                return false;
            }

            // Пробуем UI Text
            UnityEngine.UI.Text uiText = go.GetComponent<UnityEngine.UI.Text>();
            if (uiText != null)
            {
                uiText.text = env.stringValue;
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] update_score: UI.Text='{env.stringValue}' on '{go.name}'");
                return true;
            }

            // Пробуем TextMesh
            TextMesh textMesh = go.GetComponent<TextMesh>();
            if (textMesh != null)
            {
                textMesh.text = env.stringValue;
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] update_score: TextMesh='{env.stringValue}' on '{go.name}'");
                return true;
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] update_score: no Text/TextMesh on '{go.name}'");
            return false;
        }

        private bool TryMove(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            go.transform.position = new Vector3(env.x, env.y, env.z);
            return true;
        }

        private bool TryDestroy(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                UnityEngine.Object.Destroy(go);
            }

            return true;
        }

        private bool TryLoadScene(CoreAiWorldCommandEnvelope env)
        {
            string name = (env.sceneName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            SceneManager.LoadScene(name);
            return true;
        }

        private bool TryReloadScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return false;
            }

            SceneManager.LoadScene(scene.name);
            return true;
        }

        private bool TrySetActive(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                return false;
            }

            go.SetActive(env.boolValue != 0);
            return true;
        }

        private bool TryPlayAnimation(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                return TryPlayAnimationOnGameObject(go, env.stringValue);
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] play_animation: object not found (name='{env.targetName}')");
            return false;
        }

        private bool TryPlayAnimationOnGameObject(GameObject go, string animationName)
        {
            if (string.IsNullOrEmpty(animationName))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_animation: animation name is empty");
                return false;
            }

            // Ищем Animator компонент
            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.enabled)
            {
                // Проверяем что анимация существует в контроллере
                if (TryGetAnimationState(animator, animationName, out string statePath))
                {
                    animator.Play(statePath);
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[World] play_animation: '{animationName}' on '{go.name}' (Animator)");
                    return true;
                }

                // Если не нашли состояние, пробуем просто Play (Unity может найти по имени)
                animator.Play(animationName);
                _logger.LogInfo(GameLogFeature.MessagePipe,
                    $"[World] play_animation: '{animationName}' on '{go.name}' (Animator, state not verified)");
                return true;
            }

            // Legacy Animation компонент
            Animation animation = go.GetComponent<Animation>();
            if (animation != null && animation.enabled)
            {
                if (animation.clip != null && animation.GetClip(animationName) != null)
                {
                    animation.Play(animationName);
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[World] play_animation: '{animationName}' on '{go.name}' (Animation)");
                    return true;
                }
            }

            _logger.LogWarning(GameLogFeature.MessagePipe,
                $"[World] play_animation: no Animator/Animation on '{go.name}'");
            return false;
        }

        private bool TryGetAnimationState(Animator animator, string animationName, out string statePath)
        {
            statePath = "";
            if (animator.runtimeAnimatorController == null)
            {
                return false;
            }

            // Получаем все анимационные клипы из контроллера
            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
            foreach (AnimationClip clip in clips)
            {
                if (clip.name.Equals(animationName, StringComparison.OrdinalIgnoreCase))
                {
                    statePath = animationName;
                    return true;
                }
            }

            return false;
        }

        private bool TryPlaySound(CoreAiWorldCommandEnvelope env)
        {
            if (ResolveObject(env.targetName, out GameObject go))
            {
                // Ищем AudioSource
                AudioSource[] audioSources = go.GetComponents<AudioSource>();
                if (audioSources == null || audioSources.Length == 0)
                {
                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_sound: no AudioSource on '{go.name}'");
                    return false;
                }

                string clipName = (env.stringValue ?? "").Trim();
                
                // Если имя клипа не указано, просто проигрываем первый попавшийся AudioSource (если у него есть клип)
                if (string.IsNullOrEmpty(clipName))
                {
                    foreach (AudioSource src in audioSources)
                    {
                        if (src.clip != null)
                        {
                            src.Play();
                            _logger.LogInfo(GameLogFeature.MessagePipe, $"[World] play_sound: playing existing clip '{src.clip.name}' on '{go.name}'");
                            return true;
                        }
                    }
                    _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_sound: no predefined AudioClip found in any AudioSource on '{go.name}'");
                    return false;
                }

                // Ищем конкретный клип
                foreach (AudioSource src in audioSources)
                {
                    if (src.clip != null && src.clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))
                    {
                        src.Play();
                        _logger.LogInfo(GameLogFeature.MessagePipe, $"[World] play_sound: playing '{clipName}' on '{go.name}'");
                        return true;
                    }
                }

                // Поиск среди загруженных ресурсов (Resources / StreamingAssets) здесь можно добавить по желанию
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_sound: AudioClip '{clipName}' not found on '{go.name}'");
                return false;
            }

            _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] play_sound: object not found (name='{env.targetName}')");
            return false;
        }

        /// <summary>
        /// Получить список доступных анимаций для объекта.
        /// </summary>
        public string[] GetAvailableAnimations(GameObject go)
        {
            if (go == null)
            {
                return Array.Empty<string>();
            }

            List<string> animationsList = new();

            // Animator компонент - получаем клипы из AnimatorController
            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
                foreach (AnimationClip clip in clips)
                {
                    if (!string.IsNullOrEmpty(clip.name))
                    {
                        animationsList.Add(clip.name);
                    }
                }
            }

            // Legacy Animation компонент
            Animation anim = go.GetComponent<Animation>();
            if (anim != null)
            {
                foreach (AnimationState state in anim)
                {
                    if (!string.IsNullOrEmpty(state.clip.name) && !animationsList.Contains(state.clip.name))
                    {
                        animationsList.Add(state.clip.name);
                    }
                }
            }

            return animationsList.ToArray();
        }

        private bool TryListObjects(CoreAiWorldCommandEnvelope env)
        {
            string searchPattern = (env.stringValue ?? "").Trim().ToLowerInvariant();

            // Собираем все GameObject из сцены
            GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
            List<Dictionary<string, object>> results = new();

            foreach (GameObject root in rootObjects)
            {
                CollectObjectsRecursive(root, searchPattern, results);
            }

            // Сохраняем результат в последнюю known result для доступа извне
            LastListedObjects = results;

            _logger.LogInfo(GameLogFeature.MessagePipe, $"[World] list_objects: found {results.Count} objects");
            return true;
        }

        private void CollectObjectsRecursive(GameObject parent, string searchPattern,
            List<Dictionary<string, object>> results)
        {
            if (parent == null)
            {
                return;
            }

            // Проверяем имя на соответствие паттерну
            if (string.IsNullOrEmpty(searchPattern) ||
                parent.name.ToLowerInvariant().Contains(searchPattern))
            {
                results.Add(new Dictionary<string, object>
                {
                    { "name", parent.name },
                    { "active", parent.activeSelf },
                    {
                        "position",
                        new float[]
                        {
                            parent.transform.position.x, parent.transform.position.y, parent.transform.position.z
                        }
                    },
                    { "tag", parent.tag },
                    { "layer", parent.layer },
                    { "childCount", parent.transform.childCount }
                });
            }

            // Рекурсивно обходим детей
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                CollectObjectsRecursive(parent.transform.GetChild(i).gameObject, searchPattern, results);
            }
        }

        private bool TryListAnimations(CoreAiWorldCommandEnvelope env)
        {
            if (!ResolveObject(env.targetName, out GameObject go))
            {
                _logger.LogWarning(GameLogFeature.MessagePipe, $"[World] list_animations: object not found");
                return false;
            }

            // Получаем список анимаций
            string[] animations = GetAvailableAnimations(go);
            LastListedAnimations = animations;

            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"[World] list_animations: found {animations.Length} animations on '{go.name}'");
            return true;
        }

        /// <summary>
        /// Разрешает объект по targetName.
        /// </summary>
        private bool ResolveObject(string targetName, out GameObject gameObject)
        {
            gameObject = null;
            string name = (targetName ?? "").Trim();
            if (!string.IsNullOrEmpty(name))
            {
                gameObject = GameObject.Find(name);
                return gameObject != null;
            }

            return false;
        }

        /// <summary>Последний результат list_objects для тестов.</summary>
        public List<Dictionary<string, object>> LastListedObjects { get; private set; } = new();

        /// <summary>Последний результат list_animations для тестов.</summary>
        public string[] LastListedAnimations { get; private set; } = Array.Empty<string>();
    }
}