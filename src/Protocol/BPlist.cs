using System.Buffers.Binary;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>
/// 极简二进制 plist 读写，只支持 AirPlay 用到的类型：
/// 字典、数组、字符串、整数、布尔、数据、浮点。
/// </summary>
static class BPlist
{
    public static byte[] Write(object root)
    {
        var objects = new List<object>();
        Flatten(root, objects);
        int refSize = objects.Count < 256 ? 1 : 2;

        var body = new MemoryStream();
        body.Write("bplist00"u8);
        var offsets = new long[objects.Count];
        var refs = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < objects.Count; i++) refs[objects[i]] = i;

        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, objects[i], refs, refSize);
        }

        long offsetTable = body.Position;
        int offSize = offsetTable < 256 ? 1 : offsetTable < 65536 ? 2 : 4;
        foreach (var o in offsets) WriteSized(body, (ulong)o, offSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTable);
        body.Write(trailer);
        return body.ToArray();
    }

    static void Flatten(object o, List<object> list)
    {
        // 值类型装箱后各自独立，用引用相等即可保证一一对应
        list.Add(o);
        switch (o)
        {
            case Dictionary<string, object> d:
                foreach (var k in d.Keys) list.Add(k);
                // 键字符串需要单独的对象，这里先放入，值随后递归
                foreach (var v in d.Values) Flatten(v, list);
                break;
            case List<object> a:
                foreach (var v in a) Flatten(v, list);
                break;
        }
    }

    static void WriteObject(Stream s, object o, Dictionary<object, int> refs, int refSize)
    {
        switch (o)
        {
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case int or long or uint or ushort:
                WriteInt(s, Convert.ToInt64(o));
                break;
            case double dbl:
                s.WriteByte(0x23);
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, dbl);
                s.Write(buf);
                break;
            case byte[] data:
                WriteHeader(s, 0x40, data.Length);
                s.Write(data);
                break;
            case string str:
                if (str.All(c => c < 128))
                {
                    WriteHeader(s, 0x50, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    WriteHeader(s, 0x60, str.Length);
                    s.Write(Encoding.BigEndianUnicode.GetBytes(str));
                }
                break;
            case List<object> arr:
                WriteHeader(s, 0xA0, arr.Count);
                foreach (var v in arr) WriteSized(s, (ulong)refs[v], refSize);
                break;
            case Dictionary<string, object> dict:
                WriteHeader(s, 0xD0, dict.Count);
                foreach (var k in dict.Keys) WriteSized(s, (ulong)refs[k], refSize);
                foreach (var v in dict.Values) WriteSized(s, (ulong)refs[v], refSize);
                break;
            default:
                throw new NotSupportedException(o.GetType().Name);
        }
    }

    static void WriteHeader(Stream s, byte type, int count)
    {
        if (count < 15) { s.WriteByte((byte)(type | count)); return; }
        s.WriteByte((byte)(type | 0x0F));
        WriteInt(s, count);
    }

    static void WriteInt(Stream s, long v)
    {
        if (v >= 0 && v < 0x100) { s.WriteByte(0x10); s.WriteByte((byte)v); }
        else if (v >= 0 && v < 0x10000) { s.WriteByte(0x11); WriteSized(s, (ulong)v, 2); }
        else if (v >= 0 && v < 0x100000000) { s.WriteByte(0x12); WriteSized(s, (ulong)v, 4); }
        else { s.WriteByte(0x13); WriteSized(s, (ulong)v, 8); }
    }

    static void WriteSized(Stream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }

    // ---------------- 读取 ----------------

    public static object? Read(byte[] data)
    {
        if (data.Length < 40 || !data.AsSpan(0, 8).SequenceEqual("bplist00"u8)) return null;
        var t = data.AsSpan(data.Length - 32);
        int offSize = t[6], refSize = t[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
        long table = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
        var offsets = new long[numObjects];
        for (int i = 0; i < numObjects; i++) offsets[i] = (long)ReadSized(data, table + i * offSize, offSize);
        return ReadObject(data, offsets, refSize, top, 0);
    }

    static object? ReadObject(byte[] d, long[] offsets, int refSize, long index, int depth)
    {
        if (depth > 32) return null;
        long p = offsets[index];
        byte marker = d[p];
        int type = marker >> 4, info = marker & 0x0F;
        p++;

        int Count()
        {
            if (info != 0x0F) return info;
            int intSize = 1 << (d[p] & 0x0F);
            int c = (int)ReadSized(d, p + 1, intSize);
            p += 1 + intSize;
            return c;
        }

        switch (type)
        {
            case 0x0: return info == 0x09 ? true : info == 0x08 ? false : null;
            case 0x1:
                {
                    int size = 1 << info;
                    ulong v = ReadSized(d, p, size);
                    return size == 8 ? (long)v : (long)v;
                }
            case 0x2:
                return info == 3
                    ? BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)p))
                    : BinaryPrimitives.ReadSingleBigEndian(d.AsSpan((int)p));
            case 0x4: { int n = Count(); return d.AsSpan((int)p, n).ToArray(); }
            case 0x5: { int n = Count(); return Encoding.ASCII.GetString(d, (int)p, n); }
            case 0x6: { int n = Count(); return Encoding.BigEndianUnicode.GetString(d, (int)p, n * 2); }
            case 0x8: return (long)ReadSized(d, p, info + 1); // UID，当作整数
            case 0xA:
                {
                    int n = Count();
                    var list = new List<object>();
                    for (int i = 0; i < n; i++)
                        list.Add(ReadObject(d, offsets, refSize, (long)ReadSized(d, p + i * refSize, refSize), depth + 1)!);
                    return list;
                }
            case 0xD:
                {
                    int n = Count();
                    var dict = new Dictionary<string, object>();
                    for (int i = 0; i < n; i++)
                    {
                        var k = ReadObject(d, offsets, refSize, (long)ReadSized(d, p + i * refSize, refSize), depth + 1);
                        var v = ReadObject(d, offsets, refSize, (long)ReadSized(d, p + (n + i) * refSize, refSize), depth + 1);
                        if (k is string ks && v != null) dict[ks] = v;
                    }
                    return dict;
                }
        }
        return null;
    }

    static ulong ReadSized(byte[] d, long p, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | d[p + i];
        return v;
    }
}
