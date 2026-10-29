using System.Numerics;
using System.Text;

namespace UrlPack;

internal static class UrlTokenEncoding
{
    // RFC 3986 pchar ASCII alphabet; ':' is valid and included.
    // '/', '?', '#', '[', ']' and '%' are excluded: no routing delimiters or escapes.
    public const string Alphabet = "!$&'()*+,-.0123456789:;=~@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

    public static string Encode(byte[] payload)
    {
        var source = new byte[payload.Length + 1];
        source[0] = 1;
        payload.CopyTo(source, 1);
        var number = new BigInteger(source, isUnsigned: true, isBigEndian: true);
        var output = new StringBuilder();
        while (number > 0)
        {
            number = BigInteger.DivRem(number, Alphabet.Length, out var digit);
            output.Append(Alphabet[(int)digit]);
        }
        var characters = output.ToString().ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }

    public static byte[] Decode(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > UrlCompressionCodec.MaxTokenLength || token.Length < 2)
        {
            throw new ArgumentException("压缩码无效或过长");
        }
        if (token[0] == Alphabet[0])
        {
            throw new ArgumentException("不支持的压缩码格式");
        }
        var number = BigInteger.Zero;
        foreach (var character in token)
        {
            var digit = Alphabet.IndexOf(character);
            if (digit < 0)
            {
                throw new ArgumentException("压缩码包含无效字符");
            }
            number = number * Alphabet.Length + digit;
        }
        var bytes = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length < 2 || bytes[0] != 1)
        {
            throw new ArgumentException("压缩码格式无效");
        }
        return bytes[1..];
    }
}
