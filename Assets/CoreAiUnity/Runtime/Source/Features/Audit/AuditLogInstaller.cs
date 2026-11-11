using CoreAI.Audit;
using VContainer;

namespace CoreAI.Features.Audit
{
    public static class AuditLogInstaller
    {
        public static void RegisterAuditLog(this IContainerBuilder builder)
        {
            // WHY factories: on a WebGL/IL2CPP player VContainer's constructor analysis did not see [Inject] on the
            // writer's default constructor, picked the (folder, options) one and failed on its string parameter.
            // The failure came out of the build callback below and stopped every callback after it, so the
            // in-world chat never appeared. A factory needs no constructor metadata.
            builder.Register<AuditLogWriter>(static _ => new AuditLogWriter(), Lifetime.Singleton)
                .As<IAuditLog>().AsSelf();

            builder.Register<LlmAuditInterceptor>(
                static resolver => new LlmAuditInterceptor(resolver.Resolve<IAuditLog>()), Lifetime.Singleton).AsSelf();
            builder.Register<ToolCallAuditInterceptor>(
                static resolver => new ToolCallAuditInterceptor(resolver.Resolve<IAuditLog>()), Lifetime.Singleton)
                .AsSelf();

            builder.RegisterBuildCallback(resolver =>
            {
                resolver.Resolve<LlmAuditInterceptor>();
                resolver.Resolve<ToolCallAuditInterceptor>();
            });
        }
    }
}
