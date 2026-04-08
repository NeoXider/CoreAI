using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Глобальная шина событий ИИ.
    /// Позволяет новичкам подписываться на события из любого MonoBehaviour,
    /// а агентам — вызывать эти события через EventTriggerLlmTool (.WithEventTool).
    /// </summary>
    public static class CoreAiEvents
    {
        private static readonly Dictionary<string, Action> _subscribers = new();
        private static readonly Dictionary<string, Action<string>> _payloadSubscribers = new();

        /// <summary>
        /// Подписаться на событие без параметров.
        /// </summary>
        public static void Subscribe(string eventName, Action handler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null)
            {
                return;
            }

            if (_subscribers.ContainsKey(eventName))
            {
                _subscribers[eventName] += handler;
            }
            else
            {
                _subscribers[eventName] = handler;
            }
        }

        /// <summary>
        /// Подписаться на событие со строковым Payload.
        /// </summary>
        public static void Subscribe(string eventName, Action<string> payloadHandler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || payloadHandler == null)
            {
                return;
            }

            if (_payloadSubscribers.ContainsKey(eventName))
            {
                _payloadSubscribers[eventName] += payloadHandler;
            }
            else
            {
                _payloadSubscribers[eventName] = payloadHandler;
            }
        }

        /// <summary>
        /// Отписаться от события без параметров.
        /// </summary>
        public static void Unsubscribe(string eventName, Action handler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null)
            {
                return;
            }

            if (_subscribers.ContainsKey(eventName))
            {
                _subscribers[eventName] -= handler;
                if (_subscribers[eventName] == null)
                {
                    _subscribers.Remove(eventName);
                }
            }
        }

        /// <summary>
        /// Отписаться от события со строковым Payload.
        /// </summary>
        public static void Unsubscribe(string eventName, Action<string> payloadHandler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || payloadHandler == null)
            {
                return;
            }

            if (_payloadSubscribers.ContainsKey(eventName))
            {
                _payloadSubscribers[eventName] -= payloadHandler;
                if (_payloadSubscribers[eventName] == null)
                {
                    _payloadSubscribers.Remove(eventName);
                }
            }
        }

        /// <summary>
        /// Опубликовать (вызвать) событие. Используется внутренним EventTriggerLlmTool.
        /// </summary>
        public static void Publish(string eventName, string payload = "")
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            if (_subscribers.TryGetValue(eventName, out Action action) && action != null)
            {
                action.Invoke();
            }

            if (_payloadSubscribers.TryGetValue(eventName, out Action<string> payloadAction) && payloadAction != null)
            {
                payloadAction.Invoke(payload);
            }
        }

        /// <summary>
        /// Очистить все подписки (полезно при перезапуске игровых сцен).
        /// </summary>
        public static void ClearAll()
        {
            _subscribers.Clear();
            _payloadSubscribers.Clear();
        }
    }
}