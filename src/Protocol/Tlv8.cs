namespace HomePodCast.Protocol;

/// <summary>HomeKit 配对用的 TLV8 编解码（值超过 255 字节时拆分成多段同 tag）。</summary>
static class Tlv8
{
    public const byte Method = 0x00, Salt = 0x02, PublicKey = 0x03, Proof = 0x04,
        SeqNo = 0x06, Error = 0x07, Flags = 0x13;

    public static byte[] Write(params (byte Tag, byte[] Value)[] items)
    {
        var ms = new MemoryStream();
        foreach (var (tag, value) in items)
        {
            int pos = 0;
            do
            {
                int size = Math.Min(255, value.Length - pos);
                ms.WriteByte(tag);
                ms.WriteByte((byte)size);
                ms.Write(value, pos, size);
                pos += size;
            } while (pos < value.Length);
        }
        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Read(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        int pos = 0;
        while (pos + 2 <= data.Length)
        {
            byte tag = data[pos];
            int len = data[pos + 1];
            var value = data.Slice(pos + 2, Math.Min(len, data.Length - pos - 2)).ToArray();
            result[tag] = result.TryGetValue(tag, out var prev) ? [.. prev, .. value] : value;
            pos += 2 + len;
        }
        return result;
    }
}
