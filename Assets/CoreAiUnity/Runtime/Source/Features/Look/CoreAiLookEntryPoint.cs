using System;
using UnityEngine.SceneManagement;
using VContainer.Unity;

namespace CoreAI.Look
{
    /// <summary>
    /// Scope entry point of the default look: applies it on start, re-applies it when the graphics options
    /// change or a scene loads, publishes the options to the Hub Graphics page, and reverts everything when
    /// the scope is disposed. <see cref="LookMode.Off"/> does none of this.
    /// </summary>
    public sealed class CoreAiLookEntryPoint : IStartable, IDisposable
    {
        private readonly IGraphicsOptionsService _options;
        private readonly ICoreAiLookApplier _applier;
        private readonly CoreAiLookSettings _settings;
        private bool _started;
        private bool _disposed;

        /// <summary>Creates the entry point; every argument is required.</summary>
        public CoreAiLookEntryPoint(IGraphicsOptionsService options, ICoreAiLookApplier applier,
            CoreAiLookSettings settings)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _applier = applier ?? throw new ArgumentNullException(nameof(applier));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>True after <see cref="Start"/> attached to a mode other than Off.</summary>
        public bool IsActive => _started && !_disposed;

        /// <inheritdoc />
        public void Start()
        {
            if (_started || _disposed || _settings.Mode == LookMode.Off)
            {
                return;
            }

            _started = true;
            _options.Changed += HandleOptionsChanged;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            ApplyCurrent();
            CoreAiGraphicsUi.Service = _options;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_started)
            {
                return;
            }

            _options.Changed -= HandleOptionsChanged;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            if (ReferenceEquals(CoreAiGraphicsUi.Service, _options))
            {
                CoreAiGraphicsUi.Service = null;
            }

            _applier.Revert();
        }

        private void HandleOptionsChanged(ResolvedGraphicsOptions _)
        {
            ApplyCurrent();
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyCurrent();
        }

        private void ApplyCurrent()
        {
            if (_disposed)
            {
                return;
            }

            _applier.Apply(_settings.ResolveProfile(), _options.Effective, _settings.Mode);
        }
    }
}
