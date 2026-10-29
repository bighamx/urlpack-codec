using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace UrlPack;

/// <summary>Stateless URL encoding using a single Base79 wire format.</summary>
public static class UrlCompressionCodec
{
    public const int MaxUrlBytes = 4096;
    public const int MaxTokenLength = 5500;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Prefixes = ["https://www.", "https://", "http://www.", "http://"];

    public static string Encode(string url)
    {
        ValidateUrl(url);
        var prefix = Array.FindIndex(Prefixes, value => url.StartsWith(value, StringComparison.Ordinal));
        // Uppercase schemes are valid; preserve them with the literal prefix marker.
        var body = Utf8.GetBytes(prefix < 0 ? url : url[Prefixes[prefix].Length..]);
        var marker = (byte)(prefix + 1);
        var token = Pack(0, marker, body);
        var dictionary = UrlTokenEncoding.Encode(UrlDictionaryCompression.Encode(body, marker));
        if (dictionary.Length < token.Length)
        {
            token = dictionary;
        }
        var domain = UrlDomainCompression.Encode(body, marker);
        if (domain != null)
        {
            var candidate = UrlTokenEncoding.Encode(domain);
            if (candidate.Length < token.Length)
            {
                token = candidate;
            }
        }

        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(body.Length)];
        if (BrotliEncoder.TryCompress(body, buffer, out var written, quality: 5, window: 16))
        {
            var brotli = Pack(2, marker, buffer.AsSpan(0, written));
            if (brotli.Length < token.Length)
            {
                token = brotli;
            }
        }

        return token;
    }

    public static string Decode(string token)
    {
        var payload = UrlTokenEncoding.Decode(token);
        if (payload.Length < 1)
        {
            throw new ArgumentException("压缩码格式无效");
        }

        var data = payload.AsSpan(1);
        var mode = payload[0] >> 6;
        var prefixIndex = (payload[0] >> 3) & 7;
        if (prefixIndex > Prefixes.Length || (mode is 0 or 2 && (payload[0] & 7) != 0))
        {
            throw new ArgumentException("压缩头无效");
        }
        byte[] body;
        switch (mode)
        {
            case 0:
                body = data.ToArray();
                break;
            case 1:
                body = UrlDictionaryCompression.Decode(payload);
                break;
            case 2:
                var output = new byte[MaxUrlBytes + 1];
                using (var decoder = new BrotliDecoder())
                {
                    var status = decoder.Decompress(data, output, out var consumed, out var written);
                    if (status != OperationStatus.Done || consumed != data.Length || written > MaxUrlBytes)
                    {
                        throw new ArgumentException("压缩数据损坏或解压长度超限");
                    }
                    body = output.AsSpan(0, written).ToArray();
                }
                break;
            case 3:
                body = UrlDomainCompression.Decode(payload);
                break;
            default:
                throw new ArgumentException("不支持的压缩格式");
        }

        string url;
        try
        {
            var prefix = prefixIndex == 0 ? "" : Prefixes[prefixIndex - 1];
            url = prefix + Utf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("压缩数据包含无效 UTF-8");
        }
        ValidateUrl(url);
        return url;
    }

    // HTTP Location headers must be ASCII. Preserve existing ASCII escapes/query bytes;
    // only IDN hostnames and literal non-ASCII code points need transport encoding.
    public static string GetRedirectUrl(string url)
    {
        ValidateUrl(url);
        var authorityStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }
        var authority = url[authorityStart..authorityEnd];
        if (authority.Any(c => c > 127))
        {
            var portStart = authority.LastIndexOf(':');
            var port = portStart < 0 ? "" : authority[portStart..];
            url = url[..authorityStart] + new Uri(url).IdnHost + port + url[authorityEnd..];
        }
        var output = new StringBuilder(url.Length);
        foreach (var rune in url.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                output.Append((char)rune.Value);
            }
            else
            {
                output.Append(Uri.EscapeDataString(rune.ToString()));
            }
        }
        return output.ToString();
    }

    private static void ValidateUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlBytes || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
        {
            throw new ArgumentException("请输入有效的 HTTP/HTTPS 网址，空格请使用百分号编码");
        }
        try
        {
            if (Utf8.GetByteCount(url) > MaxUrlBytes)
            {
                throw new ArgumentException("网址不能超过 4096 个 UTF-8 字节");
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("网址包含无效 Unicode 字符");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("仅支持不含用户名和密码的 HTTP/HTTPS 网址");
        }
    }

    public static string GetAlgorithm(string token)
    {
        var header = UrlTokenEncoding.Decode(token)[0];
        return (header >> 6) switch
        {
            0 => "原文编码",
            1 => "URL 词典 / 数字段压缩",
            2 => "Brotli",
            3 => "域名词典 / 分段压缩",
            _ => throw new ArgumentException("不支持的压缩格式")
        };
    }

    private static string Pack(byte mode, byte prefix, ReadOnlySpan<byte> body)
    {
        var payload = new byte[body.Length + 1];
        payload[0] = (byte)((mode << 6) | (prefix << 3));
        body.CopyTo(payload.AsSpan(1));
        return UrlTokenEncoding.Encode(payload);
    }

}
