using System;
using UnityEngine;

namespace CoreAI.Features.Audit
{
    /// <summary>Configures bounded audit storage and main-thread diagnostics. Values are copied at construction.</summary>
    public sealed class AuditLogWriterOptions
    {
        /// <summary>Maximum buffered diagnostics; overflow drops the oldest diagnostic. Defaults to 256.</summary>
        public int DiagnosticQueueCapacity { get; set; } = 256;

        /// <summary>Maximum diagnostic callbacks per player-loop tick or disposal drain. Defaults to 32.</summary>
        public int MaxDiagnosticsPerPump { get; set; } = 32;

        /// <summary>Optional main-thread sink. Null uses Unity logging; callback failures are counted and isolated.</summary>
        public Action<LogType, string> DiagnosticSink { get; set; }

        /// <summary>Rotate before a nonempty flush when the current file exceeds this size. Defaults to 50 MiB.</summary>
        public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024;

        /// <summary>
        /// Positive maximum retained numbered audit JSONL files, excluding the active file. Defaults
        /// to eight. Only canonical audit_0001.jsonl names with a rotation marker are pruned; unrelated
        /// files, nested folders and symbolic links are preserved. Delete failures remain diagnostic.
        /// </summary>
        public int MaxRetainedRotatedFiles { get; set; } = 8;

        internal void Validate()
        {
            if (DiagnosticQueueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DiagnosticQueueCapacity));
            }

            if (MaxDiagnosticsPerPump <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxDiagnosticsPerPump));
            }

            if (MaxFileSizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxFileSizeBytes));
            }

            if (MaxRetainedRotatedFiles <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRetainedRotatedFiles));
            }
        }
    }
}
