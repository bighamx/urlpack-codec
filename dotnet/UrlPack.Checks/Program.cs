using System.Text.Json;
using UrlPack;

var vectorCount = 0;
using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
foreach (var vector in document.RootElement.EnumerateArray())
{
    vectorCount++;
    var url = vector.GetProperty("url").GetString();
    var token = vector.GetProperty("token").GetString();
    if (UrlCompressionCodec.Encode(url) != token || UrlCompressionCodec.Decode(token) != url ||
        UrlCompressionCodec.GetRedirectUrl(url) != vector.GetProperty("redirect").GetString())
    {
        throw new Exception("Cross-runtime vector mismatch");
    }
}
var random = new Random(417);
for (var index = 0; index < 200; index++)
{
    var bytes = new byte[random.Next(1, 300)];
    random.NextBytes(bytes);
    var url = "https://example.org/search?q=" + Convert.ToHexString(bytes);
    if (UrlCompressionCodec.Decode(UrlCompressionCodec.Encode(url)) != url)
    {
        throw new Exception("Random roundtrip failed");
    }
}
Console.WriteLine($".NET: {vectorCount} shared vectors and 200 random roundtrips passed.");
