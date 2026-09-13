using System.Buffers.Binary;
using System.Text;

namespace KSol.ZeroVDI.RDP;

/// <summary>
/// Shared NTLM ([MS-NLMP]) message helpers. The gateway is an NTLM <em>client</em> only — it
/// authenticates outbound to RDP hosts via <see cref="NtlmClient"/> — so all that survives here is
/// message-type identification. The server half (challenge generation, Type 3 parsing, NTLMv2
/// verification) existed solely for a CredSSP man-in-the-middle that was never wired up and was
/// removed in 0.6.38 along with the stored NT hash it depended on.
/// </summary>
public static class Ntlm
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("NTLMSSP\0");

    public enum MessageType { Unknown = 0, Negotiate = 1, Challenge = 2, Authenticate = 3 }

    public static MessageType GetMessageType(byte[] msg)
    {
        if (msg.Length < 12 || !msg.AsSpan(0, 8).SequenceEqual(Signature))
            return MessageType.Unknown;
        return (MessageType)BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(8, 4));
    }
}
