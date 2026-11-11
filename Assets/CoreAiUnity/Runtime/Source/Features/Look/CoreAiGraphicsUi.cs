using System;

namespace CoreAI.Look
{
    /// <summary>
    /// Process-local attachment point for the live <see cref="IGraphicsOptionsService"/>, read by the Hub
    /// Graphics page. The Hub package bootstraps without the container (like the routing UI adapter,
    /// <c>CoreAiRoutingUi</c>), so the scope publishes the service here and withdraws it on dispose.
    /// Null while no scope applies a look, including <see cref="LookMode.Off"/>.
    /// </summary>
    public static class CoreAiGraphicsUi
    {
        // TODO: fold into the composition when the Hub resolves its pages' services from the CoreAI scope it is
        // attached to (the same fold CoreAiRoutingUi waits for): HubGraphicsPage then takes IGraphicsOptionsService
        // from that container and this static goes away.
        private static IGraphicsOptionsService _service;

        /// <summary>Raised after <see cref="Service"/> changed.</summary>
        public static event Action ServiceChanged;

        /// <summary>The published service, or null.</summary>
        public static IGraphicsOptionsService Service
        {
            get => _service;
            set
            {
                if (ReferenceEquals(_service, value))
                {
                    return;
                }

                _service = value;
                ServiceChanged?.Invoke();
            }
        }
    }
}
