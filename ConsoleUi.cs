using System.Text;

namespace RestDebugProxy;

public sealed class ConsoleUi
{
    private const int BodyDisplayLimit = 256;
    private const int HeaderValueDisplayLimit = 50;

    private readonly AppState _state;
    private readonly CannedResponseStore _cannedStore;
    private readonly Func<Task> _shutdown;

    private int _renderedLines;

    public ConsoleUi(AppState state, CannedResponseStore cannedStore, Func<Task> shutdown)
    {
        _state = state;
        _cannedStore = cannedStore;
        _shutdown = shutdown;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Console.CursorVisible = false;
        }
        catch (IOException)
        {
            // Not a real console — UI will degrade gracefully.
        }

        SafeClear();

        var keyTask = Task.Run(() => KeyLoopAsync(cancellationToken), cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Render();
                try
                {
                    await Task.Delay(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try { Console.CursorVisible = true; }
            catch (IOException) { /* not a real console */ }
        }

        try { await keyTask; }
        catch (OperationCanceledException) { /* expected */ }
    }

    private async Task KeyLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.C:
                        lock (_state.RenderLock)
                        {
                            _state.CannedResponsesEnabled = !_state.CannedResponsesEnabled;
                        }
                        break;
                    case ConsoleKey.L:
                        lock (_state.RenderLock)
                        {
                            _state.CycleLatency();
                        }
                        break;
                    case ConsoleKey.Q:
                        await _shutdown();
                        return;
                }
            }

            try
            {
                await Task.Delay(40, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Render()
    {
        var sb = new StringBuilder();

        var width = SafeWindowWidth();
        var separator = new string('-', Math.Max(20, width));

        sb.Append("REST Debug Proxy").Append('\n');
        sb.Append(separator).Append('\n');

        var cannedState = _state.CannedResponsesEnabled ? "ON " : "OFF";
        sb.Append("[C] Canned Responses: ").Append(cannedState)
          .Append("   [L] Latency: ").Append(_state.LatencyMs).Append("ms")
          .Append("   [Q] Quit").Append('\n');
        sb.Append(separator).Append('\n');

        sb.Append("Listening:        ").Append(_state.ListenEndpoint).Append('\n');
        sb.Append("Forwarding to:    ").Append(_state.TargetEndpoint).Append('\n');
        sb.Append("Requests handled: ").Append(_state.RequestCount).Append('\n');
        sb.Append("Canned responses: ").Append(_cannedStore.Count).Append(" loaded from ").Append(_cannedStore.Directory).Append('\n');
        sb.Append(separator).Append('\n');

        var exchange = _state.LastExchange;
        if (exchange is null)
        {
            sb.Append("Awaiting first request...").Append('\n');
        }
        else
        {
            sb.Append("Last exchange at ").Append(exchange.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("  (").Append(exchange.ElapsedMs).Append("ms")
              .Append(exchange.WasCanned ? ", CANNED" : ", forwarded")
              .Append(')').Append('\n');
            sb.Append('\n');

            sb.Append(">>> Request").Append('\n');
            sb.Append("  URL:     ").Append(exchange.Request.Url).Append('\n');
            sb.Append("  Method:  ").Append(exchange.Request.Method).Append('\n');
            sb.Append("  Body:    ").Append(FormatBody(exchange.Request.Body)).Append('\n');
            sb.Append("  Headers:").Append('\n');
            AppendHeaders(sb, exchange.Request.Headers);
            sb.Append('\n');

            sb.Append("<<< Response").Append('\n');
            sb.Append("  Status:  ").Append(exchange.Response.StatusCode).Append('\n');
            sb.Append("  Body:    ").Append(FormatBody(exchange.Response.Body)).Append('\n');
            sb.Append("  Headers:").Append('\n');
            AppendHeaders(sb, exchange.Response.Headers);
        }

        WriteFrame(sb.ToString());
    }

    private static void AppendHeaders(StringBuilder sb, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (headers.Count == 0)
        {
            sb.Append("    (none)").Append('\n');
            return;
        }

        foreach (var header in headers)
        {
            var value = header.Value ?? string.Empty;
            if (value.Length > HeaderValueDisplayLimit)
            {
                value = value.Substring(0, HeaderValueDisplayLimit) + "...";
            }
            sb.Append("    ").Append(header.Key).Append(": ").Append(value).Append('\n');
        }
    }

    private static string FormatBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return "(empty)";
        }

        var take = Math.Min(BodyDisplayLimit, body.Length);
        string text;
        try
        {
            text = Encoding.UTF8.GetString(body, 0, take);
        }
        catch (DecoderFallbackException)
        {
            text = Convert.ToHexString(body, 0, take);
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ');

        if (body.Length > BodyDisplayLimit)
        {
            text += $"...[+{body.Length - BodyDisplayLimit}b]";
        }

        return text;
    }

    private void WriteFrame(string content)
    {
        lock (_state.RenderLock)
        {
            var width = SafeWindowWidth();
            var lines = content.Split('\n');

            try
            {
                Console.SetCursorPosition(0, 0);
            }
            catch (IOException)
            {
                Console.Write(content);
                return;
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.Write(content);
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > width)
                {
                    line = line.Substring(0, width);
                }
                else if (line.Length < width)
                {
                    line = line.PadRight(width);
                }
                Console.Write(line);
                if (i < lines.Length - 1)
                {
                    Console.Write('\n');
                }
            }

            // Clear any leftover lines from previous render.
            for (int i = lines.Length; i < _renderedLines; i++)
            {
                Console.Write('\n');
                Console.Write(new string(' ', width));
            }

            _renderedLines = lines.Length;
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : 120;
        }
        catch (IOException)
        {
            return 120;
        }
    }

    private static void SafeClear()
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // Output is redirected — nothing to clear.
        }
    }
}
