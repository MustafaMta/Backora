using System.Security.Cryptography;

namespace Backora.Application.Encryption;

/// <summary>
/// Bech32 (BIP-173) encoding with checksum, as used by age for recipients and identities. This is an
/// encoding (like base64), not cryptography. Decoding works on spans and returns byte arrays the caller owns
/// (and must zero when they hold private material). The BIP-173 90-character limit is not enforced because
/// the age specification does not apply it; callers bound the input length instead.
/// </summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
    private static readonly sbyte[] CharsetReverse = BuildReverse();

    public const int MaxLength = 1000;

    private static sbyte[] BuildReverse()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        for (var i = 0; i < Charset.Length; i++) map[Charset[i]] = (sbyte)i;
        return map;
    }

    /// <summary>Encodes 8-bit <paramref name="data"/> under <paramref name="hrp"/> (lowercase output).</summary>
    public static string Encode(string hrp, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(hrp);
        foreach (var c in hrp)
        {
            if (c < 33 || c > 126 || char.IsAsciiLetterUpper(c)) throw new ArgumentException("HRP must be lowercase printable ASCII.", nameof(hrp));
        }
        var values = ConvertBits(data, 8, 5, pad: true) ?? throw new InvalidOperationException("bit conversion failed");
        try
        {
            var checksum = CreateChecksum(hrp, values);
            var chars = new char[hrp.Length + 1 + values.Length + 6];
            try
            {
                hrp.CopyTo(0, chars, 0, hrp.Length);
                chars[hrp.Length] = '1';
                for (var i = 0; i < values.Length; i++) chars[hrp.Length + 1 + i] = Charset[values[i]];
                for (var i = 0; i < 6; i++) chars[hrp.Length + 1 + values.Length + i] = Charset[checksum[i]];
                return new string(chars);
            }
            finally
            {
                Array.Clear(chars);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(values);
        }
    }

    /// <summary>
    /// Decodes a bech32 string. Rejects mixed case, invalid characters, bad checksums and non-zero padding.
    /// The returned HRP is lowercase. <paramref name="data"/> is owned by the caller.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> input, out string hrp, out byte[] data, out string? error)
    {
        hrp = string.Empty;
        data = [];
        error = null;
        if (input.Length < 8) { error = "too short"; return false; }
        if (input.Length > MaxLength) { error = "too long"; return false; }

        bool lower = false, upper = false;
        foreach (var c in input)
        {
            if (c < 33 || c > 126) { error = "contains characters outside printable ASCII"; return false; }
            if (char.IsAsciiLetterLower(c)) lower = true;
            if (char.IsAsciiLetterUpper(c)) upper = true;
        }
        if (lower && upper) { error = "mixes upper and lower case"; return false; }

        var separator = input.LastIndexOf('1');
        if (separator < 1) { error = "missing human-readable prefix"; return false; }
        if (separator + 7 > input.Length) { error = "checksum too short"; return false; }

        var hrpChars = new char[separator];
        for (var i = 0; i < separator; i++) hrpChars[i] = char.ToLowerInvariant(input[i]);
        hrp = new string(hrpChars);

        var values = new byte[input.Length - separator - 1];
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var c = char.ToLowerInvariant(input[separator + 1 + i]);
                var v = c < 128 ? CharsetReverse[c] : (sbyte)-1;
                if (v < 0) { error = "contains a character that is not valid in bech32"; return false; }
                values[i] = (byte)v;
            }
            if (Polymod(hrp, values) != 1) { error = "checksum is invalid (typo or truncated value)"; return false; }

            var converted = ConvertBits(values.AsSpan(0, values.Length - 6), 5, 8, pad: false);
            if (converted is null) { error = "has invalid padding"; return false; }
            data = converted;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(values);
        }
    }

    private static uint Polymod(string hrp, ReadOnlySpan<byte> values)
    {
        uint chk = 1;
        foreach (var c in hrp) chk = Step(chk, (byte)(c >> 5));
        chk = Step(chk, 0);
        foreach (var c in hrp) chk = Step(chk, (byte)(c & 31));
        foreach (var v in values) chk = Step(chk, v);
        return chk;
    }

    private static uint Step(uint chk, byte value)
    {
        var top = chk >> 25;
        chk = ((chk & 0x1ffffff) << 5) ^ value;
        for (var i = 0; i < 5; i++)
        {
            if (((top >> i) & 1) != 0) chk ^= Generator[i];
        }
        return chk;
    }

    private static byte[] CreateChecksum(string hrp, ReadOnlySpan<byte> values)
    {
        var withZeros = new byte[values.Length + 6];
        try
        {
            values.CopyTo(withZeros);
            var mod = Polymod(hrp, withZeros) ^ 1;
            var result = new byte[6];
            for (var i = 0; i < 6; i++) result[i] = (byte)((mod >> (5 * (5 - i))) & 31);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(withZeros);
        }
    }

    /// <summary>General power-of-2 base conversion (BIP-173 reference). Returns null on invalid padding.</summary>
    private static byte[]? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << toBits) - 1;
        var result = new byte[pad ? (data.Length * fromBits + toBits - 1) / toBits : data.Length * fromBits / toBits];
        var n = 0;
        foreach (var value in data)
        {
            if (value >> fromBits != 0) { CryptographicOperations.ZeroMemory(result); return null; }
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result[n++] = (byte)((acc >> bits) & maxv);
            }
        }
        if (pad)
        {
            if (bits > 0) result[n++] = (byte)((acc << (toBits - bits)) & maxv);
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            CryptographicOperations.ZeroMemory(result);
            return null;
        }
        if (n != result.Length)
        {
            var trimmed = result[..n];
            CryptographicOperations.ZeroMemory(result);
            return trimmed;
        }
        return result;
    }
}
