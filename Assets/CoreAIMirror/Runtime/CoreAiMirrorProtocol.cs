using System;
using System.Globalization;
using Mirror;

namespace CoreAI.Net.Mirror
{
    /// <summary>
    /// The CoreAI Mirror wire protocol both sides of a connection must speak.
    /// </summary>
    /// <remarks>
    /// WHY a protocol number and not the package version: a patch release that changes no message
    /// stays compatible with its neighbours, while any change to a message's fields or meaning is
    /// not — and only the second is what mixed builds trip over. The number is carried in the
    /// admission request and answered in the admission response; a mismatch is refused on both
    /// sides with a reason the player can be shown, before anything else crosses the wire.
    /// <para>
    /// Bump <see cref="Version"/> with every change to a CoreAI message after admission: a field
    /// added, removed or reordered, a new message one side would not have a handler for, or a
    /// byte whose meaning changed. The admission messages themselves are read tolerantly (see
    /// <see cref="CoreAiMirrorAdmissionSerialization"/>), which is what lets a peer of another
    /// version be refused with a reason instead of failing to parse.
    /// </para>
    /// </remarks>
    public static class CoreAiMirrorProtocol
    {
        /// <summary>
        /// The protocol this build speaks. 1: the admission handshake carries the version, the
        /// refusal reaches the client before the drop, and the server publishes a roster.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// What a peer that sends no protocol number is read as: CoreAI 7.56.0 and older, whose
        /// admission messages predate the field.
        /// </summary>
        public const int Unversioned = 0;

        /// <summary>A protocol number as an operator reads it.</summary>
        public static string Describe(int version)
        {
            return version == Unversioned
                ? "none (CoreAI 7.56.0 or older)"
                : version.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The reason a peer of another protocol is given, naming both protocols.
        /// </summary>
        /// <param name="localVersion">The protocol of the side that refuses.</param>
        /// <param name="peerVersion">The protocol the other side said it speaks.</param>
        /// <param name="localIsServer">Whether the refusing side is the server.</param>
        internal static string MismatchReason(int localVersion, int peerVersion, bool localIsServer)
        {
            string local = localIsServer ? "this server" : "this client";
            string peer = localIsServer ? "the client" : "the server";
            return "CoreAI Mirror protocol mismatch: " + local + " speaks protocol "
                   + Describe(localVersion) + " and " + peer + " speaks " + Describe(peerVersion)
                   + "; run the same CoreAI release on the server and every client";
        }
    }

    /// <summary>Why an admission was refused, as the admission response carries it.</summary>
    public enum CoreAiAdmissionRefusalKind : byte
    {
        /// <summary>Not refused: the connection was admitted.</summary>
        None = 0,

        /// <summary>
        /// The host's admission provider said no. The client is told nothing more: the provider's
        /// own reason stays in the host's log.
        /// </summary>
        NotAdmitted = 1,

        /// <summary>The two sides speak different CoreAI Mirror protocols; the reason names both.</summary>
        ProtocolMismatch = 2
    }

    /// <summary>
    /// What a client learned about its refused admission: kept by the authenticator after the
    /// connection closes, so the game can show it.
    /// </summary>
    public readonly struct CoreAiAdmissionRefusal
    {
        /// <summary>Creates a refusal record.</summary>
        public CoreAiAdmissionRefusal(CoreAiAdmissionRefusalKind kind, string reason,
            int serverProtocolVersion, bool refusedByServer)
        {
            Kind = kind;
            Reason = reason ?? "";
            ServerProtocolVersion = serverProtocolVersion;
            RefusedByServer = refusedByServer;
        }

        /// <summary>Why the admission failed.</summary>
        public CoreAiAdmissionRefusalKind Kind { get; }

        /// <summary>The text to show the player.</summary>
        public string Reason { get; }

        /// <summary>
        /// The protocol the server said it speaks; <see cref="CoreAiMirrorProtocol.Unversioned"/>
        /// when its response carried none.
        /// </summary>
        public int ServerProtocolVersion { get; }

        /// <summary>The protocol this client speaks.</summary>
        public int ClientProtocolVersion => CoreAiMirrorProtocol.Version;

        /// <summary>
        /// True when the server refused the client; false when the server admitted it and this
        /// client refused the server for speaking another protocol.
        /// </summary>
        public bool RefusedByServer { get; }
    }

    /// <summary>
    /// Mirror serializers for the two admission messages, written by hand so a peer of another
    /// protocol is read instead of failing to parse.
    /// </summary>
    /// <remarks>
    /// WHY by hand: the weaver's generated reader reads every field it knows and throws when the
    /// bytes run out, and Mirror answers that throw by dropping the connection with no reason —
    /// exactly what an older client, whose request has no protocol field, would meet. These
    /// readers read the fields in their historical order and take a missing trailing field as
    /// absent; a newer peer's extra trailing fields are left unread, which Mirror allows because
    /// every message in a batch carries its own length. The field order matches the generated
    /// layout (Mirror writes an <c>int</c> as its varint), so the wire bytes are the same either
    /// way. Mirror's weaver finds these through their signatures; nothing calls them by name.
    /// </remarks>
    public static class CoreAiMirrorAdmissionSerialization
    {
        /// <summary>Writes an admission request: the credential, then the protocol.</summary>
        public static void WriteCoreAiAdmissionRequest(this NetworkWriter writer,
            CoreAiAdmissionRequestMessage message)
        {
            writer.WriteBytesAndSize(message.Credential);
            writer.WriteVarInt(message.ProtocolVersion);
        }

        /// <summary>
        /// Reads an admission request; a request that ends after its credential is an
        /// <see cref="CoreAiMirrorProtocol.Unversioned"/> one.
        /// </summary>
        public static CoreAiAdmissionRequestMessage ReadCoreAiAdmissionRequest(this NetworkReader reader)
        {
            CoreAiAdmissionRequestMessage message = new()
            {
                Credential = reader.ReadBytesAndSize(),
                ProtocolVersion = CoreAiMirrorProtocol.Unversioned
            };
            if (reader.Remaining > 0)
            {
                message.ProtocolVersion = reader.ReadVarInt();
            }

            return message;
        }

        /// <summary>Writes an admission response: the historical fields, then the protocol and the refusal.</summary>
        public static void WriteCoreAiAdmissionResponse(this NetworkWriter writer,
            CoreAiAdmissionResponseMessage message)
        {
            writer.WriteBool(message.Admitted);
            writer.WriteString(message.Reason);
            writer.WriteString(message.ActorId);
            writer.WriteVarInt(message.ProtocolVersion);
            writer.WriteByte(message.Refusal);
        }

        /// <summary>
        /// Reads an admission response; one that ends after its actor id came from a server that
        /// predates the protocol number, and reads as <see cref="CoreAiMirrorProtocol.Unversioned"/>
        /// with no refusal kind.
        /// </summary>
        public static CoreAiAdmissionResponseMessage ReadCoreAiAdmissionResponse(this NetworkReader reader)
        {
            CoreAiAdmissionResponseMessage message = new()
            {
                Admitted = reader.ReadBool(),
                Reason = reader.ReadString(),
                ActorId = reader.ReadString(),
                ProtocolVersion = CoreAiMirrorProtocol.Unversioned,
                Refusal = (byte)CoreAiAdmissionRefusalKind.None
            };
            if (reader.Remaining > 0)
            {
                message.ProtocolVersion = reader.ReadVarInt();
            }

            if (reader.Remaining > 0)
            {
                message.Refusal = reader.ReadByte();
            }

            return message;
        }

        /// <summary>
        /// Whether Mirror's codec table holds these readers for the admission messages; false
        /// means the weaver generated its own, intolerant ones and an older peer would be dropped
        /// without a reason.
        /// </summary>
        public static bool TolerantReadersRegistered()
        {
            Func<NetworkReader, CoreAiAdmissionRequestMessage> request =
                Reader<CoreAiAdmissionRequestMessage>.read;
            Func<NetworkReader, CoreAiAdmissionResponseMessage> response =
                Reader<CoreAiAdmissionResponseMessage>.read;
            return request != null && response != null
                   && request.Method.Name == nameof(ReadCoreAiAdmissionRequest)
                   && response.Method.Name == nameof(ReadCoreAiAdmissionResponse);
        }
    }
}
