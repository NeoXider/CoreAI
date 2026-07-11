using CoreAI.Audit;
using CoreAI.Infrastructure.World;
using VContainer;

namespace CoreAI.Features.Audit
{
    public static class AuditLogInstaller
    {
        public static void RegisterAuditLog(this IContainerBuilder builder)
        {
            AuditLogWriter writer = new();
            builder.RegisterInstance<IAuditLog>(writer);

            builder.Register<LlmAuditInterceptor>(Lifetime.Singleton).AsSelf();
            builder.Register<ToolCallAuditInterceptor>(Lifetime.Singleton).AsSelf();

            builder.RegisterBuildCallback(resolver =>
            {
                resolver.Resolve<LlmAuditInterceptor>();
                resolver.Resolve<ToolCallAuditInterceptor>();
            });
        }
    }
}