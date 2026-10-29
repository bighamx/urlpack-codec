using System.Text;
using System.Text.Json;

namespace UrlPack;

internal static class UrlDomainCompression
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = LoadTables();
    private static readonly string[] Domains = Tables["sldEncode"].Keys.OrderByDescending(value => value.Length).ToArray();
    private static readonly Dictionary<string, string> Tlds = Invert("tldEncode");
    private static readonly Dictionary<string, string> Slds = Invert("sldEncode");
    private static readonly Dictionary<string, string> Characters = Invert("domainEncode");

    public static byte[] Encode(byte[] input, byte prefix)
    {
        var body = Encoding.UTF8.GetString(input);
        var end = body.IndexOfAny(['/', '?', '#', ':']);
        if (end < 0)
        {
            end = body.Length;
        }
        var host = body[..end];
        var dot = host.LastIndexOf('.');
        if (dot < 0 || !Tables["tldEncode"].ContainsKey(host[(dot + 1)..]))
        {
            return null;
        }
        var tld = host[(dot + 1)..];
        var remaining = host[..dot];
        var sld = Domains.FirstOrDefault(value => remaining.EndsWith(value, StringComparison.Ordinal));
        var literal = sld == null ? remaining : remaining[..^sld.Length];
        if (literal.Any(value => !Tables["domainEncode"].ContainsKey(value.ToString())))
        {
            return null;
        }
        var bits = new StringBuilder();
        bits.Append(Convert.ToString((3 << 3) | prefix, 2).PadLeft(5, '0'));
        bits.Append(sld == null ? '0' : '1');
        bits.Append(Tables["tldEncode"][tld]);
        if (sld != null)
        {
            bits.Append(Tables["sldEncode"][sld]);
        }
        foreach (var character in literal)
        {
            bits.Append(Tables["domainEncode"][character.ToString()]);
        }
        bits.Append(Tables["domainEncode"]["END"]);
        while (bits.Length % 8 != 0)
        {
            bits.Append('0');
        }
        var tail = UrlDictionaryCompression.Encode(Encoding.UTF8.GetBytes(body[end..]), 0);
        var output = new byte[bits.Length / 8 + tail.Length];
        for (var offset = 0; offset < bits.Length; offset++)
        {
            output[offset / 8] |= (byte)((bits[offset] - '0') << (7 - offset % 8));
        }
        tail.CopyTo(output, bits.Length / 8);
        return output;
    }

    public static byte[] Decode(byte[] data)
    {
        var position = 5;
        int Bit()
        {
            if (position >= data.Length * 8)
            {
                throw new ArgumentException("域名压缩数据不完整");
            }
            var value = (data[position / 8] >> (7 - position % 8)) & 1;
            position++;
            return value;
        }
        string Read(Dictionary<string, string> table)
        {
            var code = "";
            for (var count = 0; count < 32; count++)
            {
                code += Bit() == 0 ? '0' : '1';
                if (table.TryGetValue(code, out var value))
                {
                    return value;
                }
            }
            throw new ArgumentException("域名码表数据无效");
        }
        var hasSld = Bit() != 0;
        var tld = Read(Tlds);
        var sld = hasSld ? Read(Slds) : "";
        var host = new StringBuilder();
        while (host.Length <= 255)
        {
            var value = Read(Characters);
            if (value == "END")
            {
                break;
            }
            host.Append(value);
        }
        if (host.Length > 255)
        {
            throw new ArgumentException("域名长度超限");
        }
        while (position % 8 != 0)
        {
            if (Bit() != 0)
            {
                throw new ArgumentException("域名填充数据无效");
            }
        }
        var tail = data.AsSpan(position / 8);
        if (tail.IsEmpty || (tail[0] >> 3) != 8)
        {
            throw new ArgumentException("域名后续数据无效");
        }
        var prefix = Encoding.ASCII.GetBytes(host + sld + "." + tld);
        var suffix = UrlDictionaryCompression.Decode(tail);
        if (prefix.Length + suffix.Length > UrlCompressionCodec.MaxUrlBytes)
        {
            throw new ArgumentException("解压长度超限");
        }
        return prefix.Concat(suffix).ToArray();
    }

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        using var stream = typeof(UrlDomainCompression).Assembly.GetManifestResourceStream("UrlPack.UrlDomainCodes.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream);
    }

    private static Dictionary<string, string> Invert(string table)
    {
        return Tables[table].ToDictionary(pair => pair.Value, pair => pair.Key);
    }
}
