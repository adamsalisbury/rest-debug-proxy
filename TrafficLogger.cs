using System.Text;

namespace RestDebugProxy;

public sealed class TrafficLogger
{
    private const int BodyDumpLimit = 8192;

    public string Directory { get; }

    public TrafficLogger(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public async Task LogAsync(CapturedExchange exchange, CancellationToken cancellationToken = default)
    {
        var timestamp = exchange.Timestamp.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(Directory, $"request-response-{timestamp}.txt");

        var sb = new StringBuilder();
        sb.Append("============== REST DEBUG PROXY EXCHANGE ==============\n");
        sb.Append("Timestamp:      ").Append(exchange.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")).Append('\n');
        sb.Append("Canned:         ").Append(exchange.WasCanned ? "yes" : "no").Append('\n');
        sb.Append("Response time:  ").Append(exchange.ElapsedMs).Append(" ms").Append('\n');
        sb.Append('\n');

        sb.Append("--- REQUEST ---\n");
        sb.Append("URL:     ").Append(exchange.Request.Url).Append('\n');
        sb.Append("Method:  ").Append(exchange.Request.Method).Append('\n');
        sb.Append("Headers:\n");
        foreach (var header in exchange.Request.Headers)
        {
            sb.Append("  ").Append(header.Key).Append(": ").Append(header.Value).Append('\n');
        }
        sb.Append("Body (first ").Append(BodyDumpLimit).Append(" bytes):\n");
        AppendBody(sb, exchange.Request.Body);
        sb.Append('\n');

        sb.Append("--- RESPONSE ---\n");
        sb.Append("Status:  ").Append(exchange.Response.StatusCode).Append('\n');
        sb.Append("Headers:\n");
        foreach (var header in exchange.Response.Headers)
        {
            sb.Append("  ").Append(header.Key).Append(": ").Append(header.Value).Append('\n');
        }
        sb.Append("Body (first ").Append(BodyDumpLimit).Append(" bytes):\n");
        AppendBody(sb, exchange.Response.Body);
        sb.Append('\n');

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static void AppendBody(StringBuilder sb, byte[] body)
    {
        if (body.Length == 0)
        {
            sb.Append("(empty)\n");
            return;
        }

        var take = Math.Min(BodyDumpLimit, body.Length);
        var text = TryDecodeUtf8(body, take);
        sb.Append(text).Append('\n');
        if (body.Length > BodyDumpLimit)
        {
            sb.Append("...[truncated, total ").Append(body.Length).Append(" bytes]\n");
        }
    }

    private static string TryDecodeUtf8(byte[] body, int take)
    {
        try
        {
            return Encoding.UTF8.GetString(body, 0, take);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToHexString(body, 0, take);
        }
    }
}
