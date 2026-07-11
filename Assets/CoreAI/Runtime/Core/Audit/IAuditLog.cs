using System;

namespace CoreAI.Audit
{
    public interface IAuditLog
    {
        void Record(AuditEntry entry);
    }

    public sealed class NullAuditLog : IAuditLog
    {
        public static readonly NullAuditLog Instance = new();

        public void Record(AuditEntry entry)
        {
        }
    }
}