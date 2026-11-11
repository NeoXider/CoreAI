using Mirror;

namespace CoreAI.Net.Mirror.Harness
{
    /// <summary>
    /// One slice of the spike S3 payload, server to client, on the reliable channel; sent around
    /// the CoreAI bridge on purpose, because what S3 measures is kcp carrying a payload far larger
    /// than one remote may.
    /// </summary>
    public struct CoreAiHarnessChunkMessage : NetworkMessage
    {
        /// <summary>Which run of the spike this slice belongs to.</summary>
        public int Run;

        /// <summary>This slice's index in its run.</summary>
        public int Sequence;

        /// <summary>How many slices the run has.</summary>
        public int Count;

        /// <summary>The slice.</summary>
        public byte[] Bytes;
    }

    /// <summary>A client's report of one whole run of the spike, client to server.</summary>
    public struct CoreAiHarnessReceiptMessage : NetworkMessage
    {
        /// <summary>The run reported.</summary>
        public int Run;

        /// <summary>Slices received.</summary>
        public int Received;

        /// <summary>Payload bytes received.</summary>
        public long Bytes;

        /// <summary>Seconds from the run's first slice to its last, on the client.</summary>
        public double Seconds;

        /// <summary>The highest managed heap the client saw during the run.</summary>
        public long PeakManagedBytes;

        /// <summary>The highest memory Unity reported allocated on the client during the run.</summary>
        public long PeakUnityAllocatedBytes;
    }
}
