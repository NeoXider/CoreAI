using CoreAI.Audit;
using CoreAI.Authority;
using CoreAI.Infrastructure.World;
using VContainer;

namespace CoreAI.Features.Audit
{
    public static class AuditLogInstaller
    {
        public static void RegisterAuditLog(this IContainerBuilder builder)
        {
            builder.Register<AuditLogWriter>(Lifetime.Singleton).As<IAuditLog>().AsSelf();

            builder.Register<LlmAuditInterceptor>(Lifetime.Singleton).AsSelf();
            builder.Register<ToolCallAuditInterceptor>(Lifetime.Singleton).AsSelf();

            builder.RegisterBuildCallback(resolver =>
            {
                resolver.Resolve<LlmAuditInterceptor>();
                ToolCallAuditInterceptor toolCallInterceptor = resolver.Resolve<ToolCallAuditInterceptor>();
                IActorIdentityProvider actorIdentityProvider = resolver.Resolve<IActorIdentityProvider>();
                toolCallInterceptor.SetActorIdentityResolver(
                    (string traceId, string roleId) =>
                        actorIdentityProvider.GetActorContext(roleId).ActorId);
            });
        }
    }
}
