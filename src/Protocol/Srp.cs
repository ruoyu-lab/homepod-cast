using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>
/// HAP 配对使用的 SRP-6a 客户端（3072 位素数，SHA-512）。
/// 数值哈希时使用最短大端字节（与 Apple 设备和 pyatv 行为一致），
/// 只有 k 与 u 的计算对数值做定长填充。
/// </summary>
sealed class SrpClient
{
    const string PrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    static readonly BigInteger N = BigInteger.Parse("0" + PrimeHex, System.Globalization.NumberStyles.HexNumber);
    static readonly BigInteger G = 5;
    const int PadLen = 384;

    readonly string _user, _password;
    readonly BigInteger _a, _A;

    public byte[] PublicKey { get; }
    public byte[] SessionKey { get; private set; } = [];
    byte[] _m1 = [];

    public SrpClient(string user, string password)
    {
        _user = user;
        _password = password;
        _a = FromBytes(RandomNumberGenerator.GetBytes(32));
        _A = BigInteger.ModPow(G, _a, N);
        PublicKey = ToBytes(_A);
    }

    /// <summary>根据设备的盐和公钥计算会话密钥，返回客户端证明 M1。</summary>
    public byte[] ComputeProof(byte[] salt, byte[] serverPublic)
    {
        var B = FromBytes(serverPublic);
        if (B % N == 0) throw new CryptographicException("设备公钥无效");

        var k = FromBytes(H(ToBytes(N), Pad(G)));
        var u = FromBytes(H(Pad(_A), Pad(B)));
        var x = FromBytes(H(salt, H(Encoding.UTF8.GetBytes($"{_user}:{_password}"))));

        var gx = BigInteger.ModPow(G, x, N);
        var baseVal = Mod(B - k * gx, N);
        var S = BigInteger.ModPow(baseVal, _a + u * x, N);
        SessionKey = H(ToBytes(S));

        var hN = FromBytes(SHA512.HashData(ToBytes(N)));
        var hG = FromBytes(SHA512.HashData(ToBytes(G)));
        _m1 = H(ToBytes(hN ^ hG), H(Encoding.UTF8.GetBytes(_user)), salt, ToBytes(_A), ToBytes(B), SessionKey);
        return _m1;
    }

    /// <summary>校验设备返回的证明 M2 = H(A | M1 | K)。</summary>
    public bool VerifyServerProof(byte[] m2) =>
        CryptographicOperations.FixedTimeEquals(H(ToBytes(_A), _m1, SessionKey), m2);

    static byte[] H(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) sha.AppendData(p);
        return sha.GetHashAndReset();
    }

    static BigInteger Mod(BigInteger v, BigInteger m) { var r = v % m; return r < 0 ? r + m : r; }
    static BigInteger FromBytes(ReadOnlySpan<byte> b) => new(b, isUnsigned: true, isBigEndian: true);

    static byte[] ToBytes(BigInteger v)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        return b.Length == 0 ? [0] : b;
    }

    static byte[] Pad(BigInteger v)
    {
        var b = ToBytes(v);
        if (b.Length >= PadLen) return b;
        var r = new byte[PadLen];
        b.CopyTo(r, PadLen - b.Length);
        return r;
    }
}

static class Hap
{
    public static byte[] Hkdf(byte[] secret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    /// <summary>12 字节 nonce：前 4 字节为 0，后 8 字节为小端计数器。</summary>
    public static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }
}

/// <summary>
/// HAP 加密信道：明文按 1024 字节分帧，每帧 = 2 字节小端长度 + 密文 + 16 字节标签，
/// 长度字段作为附加认证数据。
/// </summary>
sealed class HapCipher(byte[] outKey, byte[] inKey) : IDisposable
{
    readonly ChaCha20Poly1305 _out = new(outKey), _in = new(inKey);
    ulong _outCounter, _inCounter;
    readonly List<byte> _pending = [];

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> tag = stackalloc byte[16];
        Span<byte> len = stackalloc byte[2];
        for (int pos = 0; pos < data.Length; pos += 1024)
        {
            var frame = data.Slice(pos, Math.Min(1024, data.Length - pos));
            len[0] = (byte)frame.Length;
            len[1] = (byte)(frame.Length >> 8);
            var cipher = new byte[frame.Length];
            Hap.Nonce(nonce, _outCounter++);
            _out.Encrypt(nonce, frame, cipher, tag, len);
            output.Write(len);
            output.Write(cipher);
            output.Write(tag);
        }
        return output.ToArray();
    }

    /// <summary>喂入收到的密文，返回所有已完整的明文帧。</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data);
        var output = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        while (_pending.Count >= 2)
        {
            int len = _pending[0] | (_pending[1] << 8);
            if (_pending.Count < 2 + len + 16) break;
            var buf = _pending.GetRange(0, 2 + len + 16).ToArray();
            _pending.RemoveRange(0, 2 + len + 16);
            var plain = new byte[len];
            Hap.Nonce(nonce, _inCounter++);
            _in.Decrypt(nonce, buf.AsSpan(2, len), buf.AsSpan(2 + len, 16), plain, buf.AsSpan(0, 2));
            output.Write(plain);
        }
        return output.ToArray();
    }

    public void Dispose() { _out.Dispose(); _in.Dispose(); }
}
