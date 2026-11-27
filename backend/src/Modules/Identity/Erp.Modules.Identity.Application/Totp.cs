using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Erp.Modules.Identity.Application;

/// <summary>Time-based one-time passwords (RFC 6238 over RFC 4226 HOTP, HMAC-SHA1, 6 digits, 30-second steps).</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>Accepted clock drift either side, in steps.</summary>
    public const int Window = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long TimeStep(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / PeriodSeconds);

    public static string Compute(byte[] secret, long timeStep, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Checks a code against the current step ± <see cref="Window"/>; returns the matching step (for replay protection).</summary>
    public static bool Verify(byte[] secret, string? code, DateTime utc, out long matchedStep)
    {
        matchedStep = 0;
        var normalized = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalized.Length != Digits)
        {
            return false;
        }

        var current = TimeStep(utc);
        var presented = Encoding.ASCII.GetBytes(normalized);
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(Compute(secret, step))))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    public static string ProvisioningUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
}

/// <summary>RFC 4648 base32 without padding (the format authenticator apps expect).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{c}' is not a base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>Single-use recovery codes: 10 codes like "K7MQ2-X9TBA", stored only as SHA-256 hashes.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IReadOnlyList<string> Generate() =>
        Enumerable.Range(0, Count).Select(_ =>
        {
            var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 10);
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        }).ToList();

    public static string Hash(string code)
    {
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
