using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Central event hub for CoreAI tool and runtime notifications.
    /// </summary>
    public static class CoreAiEvents
    {
        // Guards the subscriber dictionaries so Publish/Subscribe stay consistent when
        // events are raised off the main thread. Handler invocation happens outside the lock.
        private static readonly object Gate = new();
        private static readonly Dictionary<string, Action> _subscribers = new();
        private static readonly Dictionary<string, Action<string>> _payloadSubscribers = new();

        /// <summary>
        /// Subscribes a handler to a payload-free CoreAI event.
        /// </summary>
        public static void Subscribe(string eventName, Action handler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_subscribers.ContainsKey(eventName))
                {
                    _subscribers[eventName] += handler;
                }
                else
                {
                    _subscribers[eventName] = handler;
                }
            }
        }

        /// <summary>
        /// Subscribes a handler that receives the event payload string.
        /// </summary>
        public static void Subscribe(string eventName, Action<string> payloadHandler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || payloadHandler == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_payloadSubscribers.ContainsKey(eventName))
                {
                    _payloadSubscribers[eventName] += payloadHandler;
                }
                else
                {
                    _payloadSubscribers[eventName] = payloadHandler;
                }
            }
        }

        /// <summary>
        /// Removes a payload-free event handler.
        /// </summary>
        public static void Unsubscribe(string eventName, Action handler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_subscribers.ContainsKey(eventName))
                {
                    _subscribers[eventName] -= handler;
                    if (_subscribers[eventName] == null)
                    {
                        _subscribers.Remove(eventName);
                    }
                }
            }
        }

        /// <summary>
        /// Removes a payload event handler.
        /// </summary>
        public static void Unsubscribe(string eventName, Action<string> payloadHandler)
        {
            if (string.IsNullOrWhiteSpace(eventName) || payloadHandler == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_payloadSubscribers.ContainsKey(eventName))
                {
                    _payloadSubscribers[eventName] -= payloadHandler;
                    if (_payloadSubscribers[eventName] == null)
                    {
                        _payloadSubscribers.Remove(eventName);
                    }
                }
            }
        }

        /// <summary>
        /// Publishes an event to both payload-free and payload subscribers.
        /// </summary>
        public static void Publish(string eventName, string payload = "")
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            Action action;
            Action<string> payloadAction;
            lock (Gate)
            {
                _subscribers.TryGetValue(eventName, out action);
                _payloadSubscribers.TryGetValue(eventName, out payloadAction);
            }

            // Isolate each subscriber so one stale/throwing handler cannot break dispatch to the rest.
            // Core is UnityEngine-free, so there is no logger sink here: swallow-and-continue.
            if (action != null)
            {
                foreach (Delegate handler in action.GetInvocationList())
                {
                    try
                    {
                        ((Action)handler).Invoke();
                    }
                    catch (Exception)
                    {
                        // Intentionally continue dispatch to remaining subscribers.
                    }
                }
            }

            if (payloadAction != null)
            {
                foreach (Delegate handler in payloadAction.GetInvocationList())
                {
                    try
                    {
                        ((Action<string>)handler).Invoke(payload);
                    }
                    catch (Exception)
                    {
                        // Intentionally continue dispatch to remaining subscribers.
                    }
                }
            }
        }

        /// <summary>
        /// Removes all registered event subscriptions.
        /// </summary>
        public static void ClearAll()
        {
            lock (Gate)
            {
                _subscribers.Clear();
                _payloadSubscribers.Clear();
            }
        }
    }
}
