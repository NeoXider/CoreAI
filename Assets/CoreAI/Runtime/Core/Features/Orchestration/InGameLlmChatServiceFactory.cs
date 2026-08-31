using System;
using System.Collections.Generic;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Resolves one retained in-game chat service per durable actor identity.
    /// </summary>
    public interface IInGameLlmChatServiceFactory
    {
        /// <summary>Returns the chat service owned by the supplied actor.</summary>
        IInGameLlmChatService Resolve(ActorContext actorContext);

        /// <summary>Releases the retained service when an actor departs.</summary>
        bool ReleaseActor(ActorContext actorContext);
    }

    /// <summary>
    /// Thread-safe, bounded actor-keyed owner of in-game chat service instances.
    /// </summary>
    public sealed class ActorKeyedInGameLlmChatServiceFactory : IInGameLlmChatServiceFactory, IDisposable
    {
        /// <summary>Default maximum number of retained actor services.</summary>
        public const int DefaultMaxActorInstances = 256;

        private readonly Dictionary<string, IInGameLlmChatService> _services =
            new Dictionary<string, IInGameLlmChatService>(StringComparer.Ordinal);
        private readonly Func<IInGameLlmChatService> _serviceFactory;
        private readonly object _sync = new object();
        private readonly int _maxActorInstances;
        private bool _disposed;

        /// <summary>Creates a bounded actor-keyed chat service factory.</summary>
        public ActorKeyedInGameLlmChatServiceFactory(
            Func<IInGameLlmChatService> serviceFactory,
            int maxActorInstances = DefaultMaxActorInstances)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            if (maxActorInstances < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxActorInstances));
            }

            _maxActorInstances = maxActorInstances;
        }

        /// <inheritdoc />
        public IInGameLlmChatService Resolve(ActorContext actorContext)
        {
            actorContext.AssertTrusted();
            string actorId = actorContext.ActorId;

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_services.TryGetValue(actorId, out IInGameLlmChatService existing))
                {
                    return existing;
                }

                if (_services.Count >= _maxActorInstances)
                {
                    throw new InvalidOperationException(
                        $"In-game chat actor capacity {_maxActorInstances} has been reached.");
                }

                IInGameLlmChatService created = _serviceFactory();
                if (created == null)
                {
                    throw new InvalidOperationException("The in-game chat service factory returned null.");
                }

                _services.Add(actorId, created);
                return created;
            }
        }

        /// <inheritdoc />
        public bool ReleaseActor(ActorContext actorContext)
        {
            actorContext.AssertTrusted();
            IInGameLlmChatService released;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_services.TryGetValue(actorContext.ActorId, out released))
                {
                    return false;
                }

                _services.Remove(actorContext.ActorId);
            }

            DisposeService(released);
            return true;
        }

        /// <summary>Releases every retained actor service.</summary>
        public void Dispose()
        {
            List<IInGameLlmChatService> released;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                released = new List<IInGameLlmChatService>(_services.Values);
                _services.Clear();
            }

            foreach (IInGameLlmChatService service in released)
            {
                DisposeService(service);
            }
        }

        private static void DisposeService(IInGameLlmChatService service)
        {
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ActorKeyedInGameLlmChatServiceFactory));
            }
        }
    }
}
