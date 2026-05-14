using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace RestDebugProxy;

public sealed class ProxyHandler
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    private readonly AppState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CannedResponseStore _cannedStore;
    private readonly TrafficLogger _trafficLogger;

    public ProxyHandler(
        AppState state,
        IHttpClientFactory httpClientFactory,
        CannedResponseStore cannedStore,
        TrafficLogger trafficLogger)
    {
        _state = state;
        _httpClientFactory = httpClientFactory;
        _cannedStore = cannedStore;
        _trafficLogger = trafficLogger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        _state.IncrementRequests();

        var timestamp = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();

        var capturedRequest = await CaptureRequestAsync(context, context.RequestAborted);

        CapturedResponse capturedResponse;
        bool wasCanned = false;

        var canned = _state.CannedResponsesEnabled
            ? _cannedStore.Find(context.Request.Method, context.Request.Path.Value ?? string.Empty)
            : null;

        if (canned is not null)
        {
            wasCanned = true;
            capturedResponse = await ServeCannedResponseAsync(context, canned, context.RequestAborted);
        }
        else
        {
            if (_state.LatencyMs > 0)
            {
                try
                {
                    await Task.Delay(_state.LatencyMs, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            capturedResponse = await ForwardAsync(context, capturedRequest, context.RequestAborted);
        }

        stopwatch.Stop();

        var exchange = new CapturedExchange
        {
            Timestamp = timestamp,
            Request = capturedRequest,
            Response = capturedResponse,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            WasCanned = wasCanned
        };

        _state.LastExchange = exchange;

        try
        {
            await _trafficLogger.LogAsync(exchange, CancellationToken.None);
        }
        catch (IOException)
        {
            // Logging must not break the proxy — drop silently.
        }
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(HttpContext context, CancellationToken ct)
    {
        var req = context.Request;
        req.EnableBuffering();

        byte[] body;
        using (var ms = new MemoryStream())
        {
            await req.Body.CopyToAsync(ms, ct);
            body = ms.ToArray();
        }

        req.Body.Position = 0;

        var headers = new List<KeyValuePair<string, string>>(req.Headers.Count);
        foreach (var header in req.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, header.Value.ToString()));
        }

        return new CapturedRequest
        {
            Url = req.GetEncodedUrl(),
            Method = req.Method,
            Body = body,
            Headers = headers
        };
    }

    private async Task<CapturedResponse> ForwardAsync(HttpContext context, CapturedRequest captured, CancellationToken ct)
    {
        var targetUri = BuildTargetUri(context.Request);

        using var message = new HttpRequestMessage(new HttpMethod(captured.Method), targetUri);

        if (HasBody(captured.Method) && captured.Body.Length > 0)
        {
            message.Content = new ByteArrayContent(captured.Body);
        }

        foreach (var header in captured.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            var values = header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (!message.Headers.TryAddWithoutValidation(header.Key, values))
            {
                message.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        var client = _httpClientFactory.CreateClient("proxy");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            var errorBody = Encoding.UTF8.GetBytes($"Proxy forwarding failed: {ex.Message}");
            await context.Response.Body.WriteAsync(errorBody, ct);
            return new CapturedResponse
            {
                StatusCode = StatusCodes.Status502BadGateway,
                Body = errorBody,
                Headers = Array.Empty<KeyValuePair<string, string>>()
            };
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            var responseHeaders = new List<KeyValuePair<string, string>>();

            CopyResponseHeaders(response.Headers, context.Response, responseHeaders);
            CopyResponseHeaders(response.Content.Headers, context.Response, responseHeaders);

            context.Response.Headers.Remove("transfer-encoding");

            var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
            await context.Response.Body.WriteAsync(bodyBytes, ct);

            return new CapturedResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = bodyBytes,
                Headers = responseHeaders
            };
        }
    }

    private static void CopyResponseHeaders(HttpHeaders source, HttpResponse destination, List<KeyValuePair<string, string>> captured)
    {
        foreach (var header in source)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            var values = header.Value.ToArray();
            destination.Headers[header.Key] = values;
            captured.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", values)));
        }
    }

    private Uri BuildTargetUri(HttpRequest request)
    {
        var baseEndpoint = _state.TargetEndpoint.TrimEnd('/');
        var path = request.Path.HasValue ? request.Path.Value : string.Empty;
        var query = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        return new Uri(baseEndpoint + path + query);
    }

    private async Task<CapturedResponse> ServeCannedResponseAsync(HttpContext context, CannedResponse canned, CancellationToken ct)
    {
        if (canned.Response.Delay > 0)
        {
            try
            {
                await Task.Delay(canned.Response.Delay, ct);
            }
            catch (OperationCanceledException)
            {
                return EmptyResponse();
            }
        }

        var payloadBody = SerialiseCannedBody(canned.Response.Payload.Body);
        var bodyBytes = Encoding.UTF8.GetBytes(payloadBody);

        context.Response.StatusCode = canned.Response.Payload.RespCode;

        var capturedHeaders = new List<KeyValuePair<string, string>>(canned.Response.Payload.Headers.Count);

        foreach (var header in canned.Response.Payload.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
            capturedHeaders.Add(new KeyValuePair<string, string>(header.Key, header.Value));
        }

        if (!canned.Response.Payload.Headers.ContainsKey("Content-Type") &&
            (canned.Response.Payload.Body.ValueKind == JsonValueKind.Object || canned.Response.Payload.Body.ValueKind == JsonValueKind.Array))
        {
            context.Response.Headers["Content-Type"] = "application/json";
            capturedHeaders.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
        }

        await context.Response.Body.WriteAsync(bodyBytes, ct);

        return new CapturedResponse
        {
            StatusCode = canned.Response.Payload.RespCode,
            Body = bodyBytes,
            Headers = capturedHeaders
        };
    }

    private static string SerialiseCannedBody(JsonElement body)
    {
        return body.ValueKind switch
        {
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => body.GetString() ?? string.Empty,
            _ => body.GetRawText()
        };
    }

    private static bool HasBody(string method)
    {
        return !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
    }

    private static CapturedResponse EmptyResponse()
    {
        return new CapturedResponse
        {
            StatusCode = 0,
            Body = Array.Empty<byte>(),
            Headers = Array.Empty<KeyValuePair<string, string>>()
        };
    }
}
