using System.Runtime.CompilerServices;

namespace BotDs.Reader.V5;

/// <summary>
/// CRC-32/ISO-HDLC (polynomial 0xEDB88320 reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF).
/// Matches the standard used by zlib, gzip, PNG, and the protocol specification.
/// </summary>
public static class V5Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>Compute CRC32 over the entire span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Compute CRC32 over two concatenated spans.</summary>
    public static uint ComputeCombined(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in first)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        foreach (byte b in second)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Validate CRC of a buffer slot. The slot is 8192 bytes.
    /// CRC covers header[0..23] + payload[0..PayloadLength-1].
    /// The CRC field at header offset 24 is outside the covered range.
    /// </summary>
    public static bool ValidateBuffer(ReadOnlySpan<byte> buffer, out uint computedCrc)
    {
        if (buffer.Length < V5Constants.BufferSlotSize)
        {
            computedCrc = 0;
            return false;
        }

        uint payloadLength = BitConverter.ToUInt32(buffer[V5Constants.HdrPayloadLengthOffset..]);
        if (payloadLength > V5Constants.MaxPayloadLength)
        {
            computedCrc = 0;
            return false;
        }

        ReadOnlySpan<byte> coveredHeader = buffer[..V5Constants.CrcCoveredHeaderLength];
        ReadOnlySpan<byte> payload = buffer.Slice(V5Constants.PayloadOffset, (int)payloadLength);

        computedCrc = ComputeCombined(coveredHeader, payload);

        uint storedCrc = BitConverter.ToUInt32(buffer[V5Constants.HdrCrc32Offset..]);
        return computedCrc == storedCrc;
    }

    /// <summary>
    /// Write CRC32 into a buffer slot. The CRC field at offset 24 must be zeroed before calling.
    /// Computes CRC over header[0..23] + payload[0..PayloadLength-1], then writes result to offset 24.
    /// </summary>
    public static void WriteCrc(Span<byte> buffer, uint payloadLength)
    {
        if (buffer.Length < V5Constants.BufferSlotSize || payloadLength > V5Constants.MaxPayloadLength)
            return;

        // Zero the CRC field
        BitConverter.TryWriteBytes(buffer[V5Constants.HdrCrc32Offset..], (uint)0);

        ReadOnlySpan<byte> coveredHeader = buffer[..V5Constants.CrcCoveredHeaderLength];
        ReadOnlySpan<byte> payload = buffer.Slice(V5Constants.PayloadOffset, (int)payloadLength);

        uint crc = ComputeCombined(coveredHeader, payload);
        BitConverter.TryWriteBytes(buffer[V5Constants.HdrCrc32Offset..], crc);
    }
}
