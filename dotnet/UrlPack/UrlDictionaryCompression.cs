using System.Globalization;
using System.Text;

namespace UrlPack;

// Mode-1 codebook and weights are permanent protocol data. New dictionaries need a new mode.
internal static class UrlDictionaryCompression
{
    private const int End = 256;
    private static readonly string[] Dictionary =
    [
        ".com", ".org", ".net", ".cn", ".io", ".co", ".edu", ".gov", ".html", ".php",
        "www.", "api.", "github.com", "google.com", "youtube.com", "youtu.be", "wikipedia.org",
        "microsoft.com", "cloudflare.com", "xbyham.com", "qq.com", "baidu.com", "weixin.qq.com",
        "bilibili.com", "taobao.com", "amazon.com", "stackoverflow.com", "twitter.com", "facebook.com",
        "/index.html", "/index.php", "/Index", "/search", "/article/", "/articles/", "/detail/",
        "/details/", "/video/", "/watch", "/download", "/api/", "/v1/", "/v2/", "/images/",
        "?q=", "?id=", "?v=", "&page=", "?page=", "&sort=", "&language=", "&category=",
        "utm_source=", "utm_medium=", "utm_campaign=", "redirect_uri=", "returnUrl=", "https://",
        "http://", "token=", "query=", "search=", "index", "default", "category", "language",
        "technology", "descending", "ascending", "zh-CN", "UTF-8", ".json", ".aspx", ".jpg", ".png"
    ];
    private static readonly byte[][] Words = Dictionary.Select(Encoding.ASCII.GetBytes).ToArray();
    private static readonly int Number = End + 1 + Dictionary.Length;
    private static readonly int PercentUpper = Number + 1;
    private static readonly int PercentLower = Number + 2;
    private static readonly Node Tree = BuildTree();
    private static readonly string[] Codes = BuildCodes();

    public static byte[] Encode(byte[] input, byte prefix)
    {
        var costs = new int[input.Length + 1];
        var choices = new Choice[input.Length];
        costs[^1] = Codes[End].Length;
        // Choose the cheapest complete parse, rather than greedily picking the longest word.
        for (var offset = input.Length - 1; offset >= 0; offset--)
        {
            costs[offset] = Codes[input[offset]].Length + costs[offset + 1];
            choices[offset] = new Choice(input[offset], 1, 0);
            void Consider(int symbol, int length, int extraBits, int count)
            {
                var cost = Codes[symbol].Length + extraBits + costs[offset + length];
                if (cost < costs[offset])
                {
                    costs[offset] = cost;
                    choices[offset] = new Choice(symbol, length, count);
                }
            }
            for (var word = 0; word < Words.Length; word++)
            {
                if (input.AsSpan(offset).StartsWith(Words[word]))
                {
                    Consider(End + 1 + word, Words[word].Length, 0, 0);
                }
            }
            for (var count = 1; count <= 18 && offset + count <= input.Length; count++)
            {
                if (input[offset + count - 1] is < (byte)'0' or > (byte)'9')
                {
                    break;
                }
                Consider(Number, count, 5 + DecimalBits(count), count);
            }
            for (var casing = 0; casing < 2; casing++)
            {
                var upper = casing == 0;
                for (var count = 1; count <= 32 && offset + count * 3 <= input.Length; count++)
                {
                    var start = offset + (count - 1) * 3;
                    if (input[start] != '%' || !Hex(input[start + 1], upper) || !Hex(input[start + 2], upper))
                    {
                        break;
                    }
                    Consider(upper ? PercentUpper : PercentLower, count * 3, 5 + count * 8, count);
                }
            }
        }

        var writer = new BitWriter();
        writer.Bits((ulong)(8 | prefix), 5);
        for (var offset = 0; offset < input.Length;)
        {
            var choice = choices[offset];
            writer.Code(Codes[choice.Symbol]);
            if (choice.Symbol == Number)
            {
                writer.Bits((ulong)(choice.Count - 1), 5);
                ulong value = 0;
                for (var index = 0; index < choice.Count; index++)
                {
                    value = value * 10 + (ulong)(input[offset + index] - '0');
                }
                writer.Bits(value, DecimalBits(choice.Count));
            }
            else if (choice.Symbol == PercentUpper || choice.Symbol == PercentLower)
            {
                writer.Bits((ulong)(choice.Count - 1), 5);
                for (var index = 0; index < choice.Count; index++)
                {
                    var start = offset + index * 3;
                    var value = (HexValue(input[start + 1]) << 4) | HexValue(input[start + 2]);
                    writer.Bits((ulong)value, 8);
                }
            }
            offset += choice.Length;
        }
        writer.Code(Codes[End]);
        return writer.Finish();
    }

    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        var reader = new BitReader(data);
        var header = reader.Bits(5);
        if ((header >> 3) != 1 || (header & 7) > 4)
        {
            throw new ArgumentException("无效压缩头");
        }
        using var output = new MemoryStream();
        while (true)
        {
            var node = Tree;
            while (node.Symbol < 0)
            {
                node = reader.Bits(1) == 0 ? node.Left : node.Right;
            }
            if (node.Symbol == End)
            {
                reader.End();
                return output.ToArray();
            }
            byte[] part;
            if (node.Symbol < End)
            {
                part = [(byte)node.Symbol];
            }
            else if (node.Symbol < Number)
            {
                part = Words[node.Symbol - End - 1];
            }
            else if (node.Symbol == Number)
            {
                var count = (int)reader.Bits(5) + 1;
                if (count > 18)
                {
                    throw new ArgumentException("无效数字段长度");
                }
                var value = reader.Bits(DecimalBits(count));
                var digits = value.ToString(CultureInfo.InvariantCulture);
                if (digits.Length > count)
                {
                    throw new ArgumentException("数字段超限");
                }
                part = Encoding.ASCII.GetBytes(digits.PadLeft(count, '0'));
            }
            else
            {
                var count = (int)reader.Bits(5) + 1;
                var alphabet = node.Symbol == PercentUpper ? "0123456789ABCDEF" : "0123456789abcdef";
                part = new byte[count * 3];
                for (var index = 0; index < count; index++)
                {
                    var value = (int)reader.Bits(8);
                    part[index * 3] = (byte)'%';
                    part[index * 3 + 1] = (byte)alphabet[value >> 4];
                    part[index * 3 + 2] = (byte)alphabet[value & 15];
                }
            }
            if (output.Length + part.Length > UrlCompressionCodec.MaxUrlBytes)
            {
                throw new ArgumentException("解压长度超限");
            }
            output.Write(part);
        }
    }

    private static bool Hex(byte value, bool upper)
    {
        return value is >= (byte)'0' and <= (byte)'9' ||
            (upper ? value is >= (byte)'A' and <= (byte)'F' : value is >= (byte)'a' and <= (byte)'f');
    }

    private static int HexValue(byte value)
    {
        return value <= '9' ? value - '0' : (value | 32) - 'a' + 10;
    }

    private static int DecimalBits(int count)
    {
        // ceil(log2(10^count)), computed with integers to freeze the format across runtimes.
        ulong maximum = 1;
        for (var index = 0; index < count; index++)
        {
            maximum *= 10;
        }
        return 64 - System.Numerics.BitOperations.LeadingZeroCount(maximum - 1);
    }

    private sealed record Node(int Symbol, Node Left = null, Node Right = null);
    private readonly record struct Choice(int Symbol, int Length, int Count);

    private static Node BuildTree()
    {
        var queue = new PriorityQueue<Node, (int Weight, int Order)>();
        const string common = "etaoinshrdlucmfpgywbvkxjqz";
        for (var symbol = 0; symbol <= PercentLower; symbol++)
        {
            var weight = 1;
            var rank = symbol < 256 ? common.IndexOf((char)symbol) : -1;
            if (rank >= 0)
            {
                weight = 120 - rank * 3;
            }
            else if (symbol is >= '0' and <= '9')
            {
                weight = 25;
            }
            else if (symbol < 256 && "/.-_?=&%:#~".Contains((char)symbol))
            {
                weight = 45;
            }
            else if (symbol is >= 'A' and <= 'Z')
            {
                weight = 10;
            }
            else if (symbol > End && symbol < Number)
            {
                weight = symbol == End + 1 ? 240 : 25;
            }
            else if (symbol >= Number)
            {
                weight = 50;
            }
            queue.Enqueue(new Node(symbol), (weight, symbol));
        }
        var order = PercentLower + 1;
        while (queue.Count > 1)
        {
            queue.TryDequeue(out var left, out var leftPriority);
            queue.TryDequeue(out var right, out var rightPriority);
            queue.Enqueue(new Node(-1, left, right), (leftPriority.Weight + rightPriority.Weight, order++));
        }
        return queue.Dequeue();
    }

    private static string[] BuildCodes()
    {
        var codes = new string[PercentLower + 1];
        void Visit(Node node, string code)
        {
            if (node.Symbol >= 0)
            {
                codes[node.Symbol] = code;
                return;
            }
            Visit(node.Left, code + "0");
            Visit(node.Right, code + "1");
        }
        Visit(Tree, "");
        return codes;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;
        private int _value;

        public void Code(string code)
        {
            foreach (var bit in code)
            {
                Bits((ulong)(bit - '0'), 1);
            }
        }

        public void Bits(ulong value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--)
            {
                _value = (_value << 1) | (int)((value >> bit) & 1);
                if (++_bits == 8)
                {
                    _bytes.Add((byte)_value);
                    _bits = 0;
                    _value = 0;
                }
            }
        }

        public byte[] Finish()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_value << (8 - _bits)));
            }
            return _bytes.ToArray();
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public ulong Bits(int count)
        {
            if (_position + count > _data.Length * 8)
            {
                throw new ArgumentException("压缩数据不完整");
            }
            ulong value = 0;
            for (var index = 0; index < count; index++)
            {
                var next = (uint)(_data[_position / 8] >> (7 - _position % 8)) & 1U;
                value = (value << 1) | next;
                _position++;
            }
            return value;
        }

        public void End()
        {
            var remaining = _data.Length * 8 - _position;
            if (remaining >= 8 || Bits(remaining) != 0)
            {
                throw new ArgumentException("压缩码包含多余数据或非零填充");
            }
        }
    }
}
