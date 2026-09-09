using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional scope values used to isolate agent memory across users, sessions, topics, or tenants.
    /// </summary>
    public readonly struct AgentMemoryScope
    {
        /// <summary>
        /// Creates an immutable memory scope.
        /// </summary>
        public AgentMemoryScope(string tenantId, string userId, string sessionId, string topicId)
        {
            TenantId = tenantId ?? "";
            UserId = userId ?? "";
            SessionId = sessionId ?? "";
            TopicId = topicId ?? "";
        }

        /// <summary>Product or organization boundary.</summary>
        public string TenantId { get; }

        /// <summary>Current player, learner, or account id.</summary>
        public string UserId { get; }

        /// <summary>Current gameplay, chat, lesson, or practice session id.</summary>
        public string SessionId { get; }

        /// <summary>Optional domain topic, quest, scene, or course id.</summary>
        public string TopicId { get; }

        /// <summary>Default empty scope that preserves role-only memory keys.</summary>
        public static AgentMemoryScope Empty => new("", "", "", "");
    }

    /// <summary>
    /// Single canonical role/scope key mapping shared by memory, chat, transcript, and conversation summaries.
    /// Internal so persistence adapters cannot accidentally invent a second encoding.
    /// </summary>
    internal static class AgentMemoryScopeKey
    {
        internal const string ScopedKeyPrefix = "scope-v1-";
        internal const string ActorKeyPrefix = "actor-v1-";

        internal static string Resolve(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            roleId = NormalizeRoleId(roleId);
            if (AgentMemoryScopeExecutionContext.TryGet(
                    out AgentMemoryScope captured,
                    out string actorId))
            {
                if (string.IsNullOrEmpty(actorId))
                {
                    return Resolve(captured, roleId);
                }

                // WHY: "local" is the anonymous single-player default that every host gets for free
                // (CoreServicesInstaller.DefaultLocalHostIdentityProvider issues it with an EMPTY memory
                // scope). It identifies nobody, so it must never outrank a scope the host declared through
                // IAgentMemoryScopeProvider. It used to: every write made INSIDE a turn landed in the
                // unscoped bare-role file while hydration and reads outside the turn used the learner's
                // scope-v1-* file, so a whole lesson fell out of that learner's history and the shared file
                // silently accumulated every learner's turns. A named actor still wins - see ResolveActorId.
                if (IsDefaultLocalActor(actorId) && IsEmptyScope(captured))
                {
                    return ResolveFromProvider(scopeProvider, roleId);
                }

                // WHY: у именованного актора scope тоже берётся из хода, а когда ход его не принёс —
                // из провайдера хоста. Иначе тот же класс расхождения, что чинили для "local": внутри
                // хода ключ считался без scope, снаружи — со scope провайдера, и одна и та же память
                // читалась под двумя разными ключами.
                AgentMemoryScope effectiveScope = IsEmptyScope(captured)
                    ? ScopeFromProvider(scopeProvider, roleId)
                    : captured;

                return ResolveActorId(actorId, effectiveScope, roleId);
            }

            return ResolveFromProvider(scopeProvider, roleId);
        }

        private static AgentMemoryScope ScopeFromProvider(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            return (scopeProvider ?? new DefaultAgentMemoryScopeProvider()).GetScope(roleId);
        }

        private static string ResolveFromProvider(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            return Resolve(ScopeFromProvider(scopeProvider, roleId), roleId);
        }

        internal static string Resolve(AgentMemoryScope scope, string roleId)
        {
            roleId = NormalizeRoleId(roleId);
            if (IsEmptyScope(scope))
            {
                return roleId;
            }

            // WHY: Scoped ids commonly contain account/learner PII. Persisting the old readable,
            // length-prefixed mapping exposed those ids in filenames and could collide on a
            // case-insensitive filesystem when two identities differed only by case. A full digest
            // is opaque and turns case differences into unrelated lowercase filenames.
            StringBuilder canonical = new(128);
            AppendCanonicalPart(canonical, scope.TenantId);
            AppendCanonicalPart(canonical, scope.UserId);
            AppendCanonicalPart(canonical, scope.SessionId);
            AppendCanonicalPart(canonical, scope.TopicId);
            AppendCanonicalPart(canonical, roleId);
            return ScopedKeyPrefix + Sha256Hex(canonical.ToString());
        }

        private static string ResolveActorId(string actorId, AgentMemoryScope scope, string roleId)
        {
            if (IsDefaultLocalActor(actorId))
            {
                // WHY: the default local actor carries no identity of its own, so the key is decided by the
                // scope alone - an empty scope keeps the legacy bare-role file, a declared scope wins.
                // Returning the bare role id unconditionally discarded the host's scope and merged
                // every identity into one file.
                return Resolve(scope, roleId);
            }

            // WHY: ActorContext.MemoryScope обещан как «tenant, user, session и topic, которыми пользуется
            // персистентность памяти». Раньше для именованного актора эти поля ОТБРАСЫВАЛИСЬ: два урока
            // (topic) или две сессии одного актора делили один файл, а два арендатора с одинаковым id
            // актора — тем более. Пустой scope кодируется по-старому (две части), чтобы файлы уже
            // существующих именованных акторов без scope не осиротели; непустой добавляет четыре части.
            // Кодирование с префиксом длины остаётся инъективным: число частей восстанавливается однозначно.
            StringBuilder canonical = new(160);
            AppendCanonicalPart(canonical, actorId);
            if (!IsEmptyScope(scope))
            {
                AppendCanonicalPart(canonical, scope.TenantId);
                AppendCanonicalPart(canonical, scope.UserId);
                AppendCanonicalPart(canonical, scope.SessionId);
                AppendCanonicalPart(canonical, scope.TopicId);
            }

            AppendCanonicalPart(canonical, roleId);
            return ActorKeyPrefix + Sha256Hex(canonical.ToString());
        }

        /// <summary>
        /// Whether the id is the reserved anonymous local default rather than a real, named actor.
        /// A named actor keeps owning its durable key across reconnects, so only this one id defers.
        /// </summary>
        private static bool IsDefaultLocalActor(string actorId)
        {
            return string.Equals(actorId, LocalActorIdentityProvider.DefaultActorId, StringComparison.Ordinal);
        }

        private static bool IsEmptyScope(AgentMemoryScope scope)
        {
            return string.IsNullOrWhiteSpace(scope.TenantId) &&
                   string.IsNullOrWhiteSpace(scope.UserId) &&
                   string.IsNullOrWhiteSpace(scope.SessionId) &&
                   string.IsNullOrWhiteSpace(scope.TopicId);
        }

        private static string NormalizeRoleId(string roleId)
        {
            return string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
        }

        /// <summary>
        /// Length-prefixes every trimmed component before hashing, keeping the tuple encoding injective.
        /// Empty-scope bare-role data is deliberately not folded into this encoding: importing a legacy
        /// role file into a user scope must be an explicit host migration so the first scoped user cannot
        /// accidentally claim data that used to be shared by every user of that role.
        /// </summary>
        private static void AppendCanonicalPart(StringBuilder sb, string value)
        {
            string raw = value?.Trim() ?? "";
            sb.Append(raw.Length).Append(':').Append(raw).Append(';');
        }

        private static string Sha256Hex(string value)
        {
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            }

            StringBuilder sb = new(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++)
            {
                sb.Append(digest[i].ToString("x2"));
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Публичный вход для доступа к памяти ОТ ИМЕНИ актора вне хода оркестратора.
    /// <para>
    /// Внутри хода очередь сама поднимает контекст актора, и все scoped-декораторы считают ключ по нему.
    /// Снаружи (гидрация истории для UI, сброс контекста при отключении соединения, миграция) актора
    /// никто не поднимает, и ключ считается только по <see cref="IAgentMemoryScopeProvider"/> — то есть
    /// хост с именованными акторами читал и чистил НЕ ТОТ файл, что писал ход. Обёртка кода в
    /// <c>using (AgentMemoryActorScope.Enter(actorContext))</c> даёт тот же ключ, что и внутри хода.
    /// Контекст обязан быть выдан провайдером личности (<see cref="ActorContext.IsTrusted"/>): собранный
    /// вручную не принимается, чтобы нельзя было «войти» в чужую память подделкой структуры.
    /// </para>
    /// </summary>
    public static class AgentMemoryActorScope
    {
        /// <summary>Делает <paramref name="actorContext"/> текущим для памяти до <c>Dispose()</c>.</summary>
        /// <exception cref="InvalidOperationException">Контекст не выдан провайдером личности.</exception>
        public static IDisposable Enter(ActorContext actorContext)
        {
            return AgentMemoryScopeExecutionContext.Push(actorContext);
        }

        /// <summary>Делает <paramref name="scope"/> текущим для памяти до <c>Dispose()</c> (без актора).</summary>
        public static IDisposable Enter(AgentMemoryScope scope)
        {
            return AgentMemoryScopeExecutionContext.Push(scope);
        }
    }

    /// <summary>
    /// Carries an immutable enqueue-time memory scope across asynchronous orchestration execution.
    /// </summary>
    internal static class AgentMemoryScopeExecutionContext
    {
        private static readonly AsyncLocal<Frame> Current = new();

        internal static IDisposable Push(AgentMemoryScope scope)
        {
            Frame previous = Current.Value;
            Frame frame = new(scope, "");
            Current.Value = frame;
            return new Lease(frame, previous);
        }

        internal static IDisposable Push(ActorContext actorContext)
        {
            actorContext.AssertTrusted();
            Frame previous = Current.Value;
            Frame frame = new(actorContext.MemoryScope, actorContext.ActorId);
            Current.Value = frame;
            return new Lease(frame, previous);
        }

        internal static bool TryGet(out AgentMemoryScope scope, out string actorId)
        {
            Frame frame = Current.Value;
            if (frame == null)
            {
                scope = AgentMemoryScope.Empty;
                actorId = "";
                return false;
            }

            scope = frame.Scope;
            actorId = frame.ActorId;
            return true;
        }

        private sealed class Frame
        {
            internal Frame(AgentMemoryScope scope, string actorId)
            {
                Scope = scope;
                ActorId = actorId ?? "";
            }

            internal AgentMemoryScope Scope { get; }
            internal string ActorId { get; }
        }

        private sealed class Lease : IDisposable
        {
            private readonly Frame _owned;
            private readonly Frame _previous;
            private int _disposed;

            internal Lease(Frame owned, Frame previous)
            {
                _owned = owned;
                _previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (ReferenceEquals(Current.Value, _owned))
                {
                    Current.Value = _previous;
                }
            }
        }
    }
}
