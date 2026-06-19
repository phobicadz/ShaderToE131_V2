using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace ShaderToE131;

/// <summary>
/// Sends E.1.31 (sACN) UDP frames to a unicast or multicast target.
/// Packet structure verified against Haukcode.sACN v2.0.90 reference implementation.
/// </summary>
public sealed class E131Sender : IDisposable
{
    private readonly Socket _sendSocket;
    private readonly IPEndPoint _destination;
    public IPAddress? BoundLocalAddress { get; }

    // ACN constants (verified against Haukcode.sACN)
    private const ushort PREAMBLE_SIZE = 16;
    private const ushort POSTAMBLE_SIZE = 0;
    private static readonly byte[] PACKET_ID = new byte[] {
        0x41, 0x53, 0x43, 0x2d, 0x45,                    // "ASC-E"
        0x31, 0x2e, 0x31, 0x37, 0x00,                    // "1.17\0"
        0x00, 0x00                                        // padding
    };
    private const int VECTOR_ROOT_E131_DATA = 4;   // Root layer vector for E.1.31 data
    private const int VECTOR_FRAME_E131_DATA = 2;  // Framing layer vector for E.1.31 data
    private const byte DMP_VECTOR = 0x02;          // DMP Set Property Message
    private const byte DMP_ADDRESS_TYPE_AND_DATA_TYPE = 0xA1; // DMX format
    private const ushort FIRST_PROPERTY_ADDRESS = 0;
    private const ushort ADDRESS_INCREMENT = 1;
    private const ushort FLAGS = 0x7000;           // High 4 bits of flags/length fields

    // Per-universe sequence numbers (matching Haukcode.sACN's Dictionary<ushort, byte>)
    private readonly Dictionary<ushort, byte> _seqNums = new();

    // Source name — null-padded to 64 bytes
    private static readonly byte[] _sourceNameBytes;

    static E131Sender()
    {
        var name = "ShaderToE131";
        _sourceNameBytes = new byte[64];
        for (int i = 0; i < name.Length && i < 64; i++)
            _sourceNameBytes[i] = (byte)name[i];
    }

    public E131Sender(string targetIp, int port = 5568)
    {
        var targetAddress = IPAddress.Parse(targetIp);
        _destination = new IPEndPoint(targetAddress, port);

        // Find best local address for the target subnet
        var localAddr = FindBestLocalAddressForTarget(targetAddress) ?? IPAddress.Loopback;
        BoundLocalAddress = localAddr;

        _sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sendSocket.Bind(new IPEndPoint(localAddr, 0));
        _sendSocket.SendBufferSize = 2720000;
    }

    /// <summary>
    /// Send pixel data across multiple universes when the channel count exceeds 510.
    /// Each universe gets exactly 510 slots of DMX data (padded with zeros if needed).
    /// Universes are paced to avoid UDP burst drops on Windows.
    /// </summary>
    public void SendFrameMultiUniverse(ReadOnlySpan<byte> pixels, ushort baseUniverse)
    {
        const int maxSlots = 510;
        int totalChannels = pixels.Length;
        int numUniverses = (totalChannels + maxSlots - 1) / maxSlots; // ceiling division

        // Pad pixel data to a full multiple of maxSlots so every universe sends exactly 510 channels.
        int paddedLength = numUniverses * maxSlots;
        byte[] effectivePixels;
        if (totalChannels < paddedLength)
        {
            effectivePixels = new byte[paddedLength];
            pixels.CopyTo(new Span<byte>(effectivePixels));
        }
        else
        {
            effectivePixels = pixels.ToArray();
        }

        // Pre-allocate one buffer sized for a full 510-channel universe.
        // Root(38) + framing flags/len field(2) + framing payload(75+dmp)+DMP data portion
        const int dmpLayerDataLen = 11 + maxSlots;
        const int totalPacketSize = 38 + 77 + dmpLayerDataLen;

        byte[] buffer = new byte[totalPacketSize];

        for (int u = 0; u < numUniverses; u++)
        {
            ushort universe = (ushort)(baseUniverse + u);
            int startSlot = u * maxSlots;

            BuildFrame(buffer, universe, effectivePixels.AsSpan(startSlot, maxSlots));
            _sendSocket.SendTo(buffer, SocketFlags.None, _destination);

            // Pace packets to avoid UDP burst drops on Windows.
            if (u < numUniverses - 1)
                System.Threading.Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Build a complete E.1.31 packet into the buffer for one universe with exactly maxSlots data bytes.
    /// Structure verified against Haukcode.sACN v2.0.90:
    ///   Root layer:     preamble(2)+postamble(2)+PID(12)+flags/len(2)+vector(4)+UUID(16) = 38
    ///   Framing layer:  flags/len(2)+vector(4)+srcName(64)+priority(1)+syncAddr(2)
    ///                   +seqNum(1)+options(1)+universe(2) = 77 (+ DMPLayer payload after it)
    ///   DMP layer:      flags/len(2)+vector(1)+type(1)+firstAddr(2)+addrInc(2)
    ///                   +propCount(2)+startCode(1)+data(n) = 11+n
    /// </summary>
    private void BuildFrame(byte[] buffer, ushort universe, ReadOnlySpan<byte> data)
    {
        const int maxSlots = 510;
        int channelCount = Math.Min(maxSlots, data.Length);

        // DMPLayer.Length = 11 + channelCount (flags/len through start code + data)
        int dmpLayerLength = 11 + channelCount;

        // Framing layer length: match Haukcode.sACN which stores total framing layer size
        // including its own flags/len field: 77 + DMPLayer.Length
        int framingPayloadLen = 77 + dmpLayerLength;

        // Root flags/length: (38 + FramingLayer.Length - 16) in low 12 bits
        // = 22 + FramingLayer.Length = 22 + 77 + dmpLayerLength = 99 + dmpLayerLength
        int rootFlagsAndLen = 99 + dmpLayerLength;

        int offset = 0;

        // ===== ACN ROOT LAYER (38 bytes) =====
        WriteUInt16BE(buffer, offset, PREAMBLE_SIZE); offset += 2;      // preamble size (16)
        WriteUInt16BE(buffer, offset, POSTAMBLE_SIZE); offset += 2;     // postamble size (0)
        for (int i = 0; i < PACKET_ID.Length; i++) buffer[offset + i] = PACKET_ID[i];
        offset += 12;

        // Root flags/length: 0x7000 | (99 + dmpLayerLength)
        WriteUInt16BE(buffer, offset, (ushort)(FLAGS | rootFlagsAndLen)); offset += 2;
        WriteUInt32BE(buffer, offset, VECTOR_ROOT_E131_DATA); offset += 4;   // vector = 0x00000004

        // UUID — use a random GUID per sender instance (matching Haukcode.sACN which uses SenderId)
        var uuid = _uuid.ToByteArray();
        for (int i = 0; i < uuid.Length; i++) buffer[offset + i] = uuid[i];
        offset += 16;

        // ===== FRAMING LAYER =====
        WriteUInt16BE(buffer, offset, (ushort)(FLAGS | framingPayloadLen)); offset += 2;
        WriteUInt32BE(buffer, offset, VECTOR_FRAME_E131_DATA); offset += 4;   // vector = 0x00000002
        for (int i = 0; i < _sourceNameBytes.Length; i++) buffer[offset + i] = _sourceNameBytes[i];
        offset += 64;

        byte seqNum;
        lock (_seqNums)
        {
            if (!_seqNums.TryGetValue(universe, out seqNum)) seqNum = 0;
            seqNum++;
            _seqNums[universe] = seqNum;
        }

        buffer[offset++] = 100;                    // priority (standard)
        WriteUInt16BE(buffer, offset, 0); offset += 2;   // sync address
        buffer[offset++] = seqNum;                 // sequence number — per-universe counter
        buffer[offset++] = 0x06;                   // options: terminated stream
        WriteUInt16BE(buffer, offset, universe); offset += 2;   // universe number

        // ===== DMP LAYER =====
        int dmpOffset = offset;
        WriteUInt16BE(buffer, offset, (ushort)(FLAGS | dmpLayerLength)); offset += 2;
        buffer[offset++] = DMP_VECTOR;                             // 0x02
        buffer[offset++] = DMP_ADDRESS_TYPE_AND_DATA_TYPE;         // 0xA1
        WriteUInt16BE(buffer, offset, FIRST_PROPERTY_ADDRESS); offset += 2;   // first addr = 0
        WriteUInt16BE(buffer, offset, ADDRESS_INCREMENT); offset += 2;       // increment = 1
        WriteUInt16BE(buffer, offset, (ushort)(channelCount + 1)); offset += 2; // prop count = data+startCode
        buffer[offset++] = 0x00;                                   // DMX start code

        // Copy pixel/data bytes
        for (int i = 0; i < channelCount; i++)
            buffer[offset + i] = data[i];
    }

    private static void WriteUInt16BE(Span<byte> buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteUInt32BE(Span<byte> buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static IPAddress? FindBestLocalAddressForTarget(IPAddress target)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork)
            return null;

        byte[] targetBytes = target.GetAddressBytes();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                byte[] localBytes = ua.Address.GetAddressBytes();

                // Prefer same /24 as target.
                if (localBytes[0] == targetBytes[0] && localBytes[1] == targetBytes[1] && localBytes[2] == targetBytes[2])
                    return ua.Address;
            }
        }

        return null;
    }

    // Random GUID for UUID field — matches Haukcode.sACN which uses a SenderId Guid.
    private static readonly Guid _uuid = Guid.NewGuid();

    public void Dispose() => _sendSocket.Dispose();
}
