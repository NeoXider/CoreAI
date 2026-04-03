using UnityEngine;

namespace CoreAI.ExampleGame.ArenaAi.Infrastructure
{
    /// <summary>
    /// Триггер входа в «комнату»: <see cref="ArenaAiTaskBus.NotifyRoomEntered"/>.
    /// Повесьте на объект с <see cref="Collider"/> <c>isTrigger</c>, игрок с тегом <c>Player</c>.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class ArenaAiRoomEnterTrigger : MonoBehaviour
    {
        [SerializeField]
        private string roomId = "main";

        [Tooltip("Один раз за забег на этот коллайдер.")]
        [SerializeField]
        private bool fireOnce = true;

        private bool _fired;

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player"))
                return;
            if (fireOnce && _fired)
                return;
            var bus = FindAnyObjectByType<ArenaAiTaskBus>();
            if (bus == null)
            {
                Debug.LogWarning("[CoreAI.ExampleGame] ArenaAiRoomEnterTrigger: ArenaAiTaskBus не найден.");
                return;
            }

            bus.NotifyRoomEntered(roomId);
            _fired = true;
        }
    }
}
