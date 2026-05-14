namespace RestDebugProxy;

public sealed class AppState
{
    private static readonly int[] LatencySteps = [0, 50, 250, 500, 1000];

    private long _requestCount;
    private int _latencyIndex;

    public required string ListenEndpoint { get; init; }

    public required string TargetEndpoint { get; init; }

    public bool CannedResponsesEnabled { get; set; } = true;

    public int LatencyMs => LatencySteps[_latencyIndex];

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public CapturedExchange? LastExchange { get; set; }

    public readonly object RenderLock = new();

    public void IncrementRequests()
    {
        Interlocked.Increment(ref _requestCount);
    }

    public void CycleLatency()
    {
        _latencyIndex = (_latencyIndex + 1) % LatencySteps.Length;
    }
}

public sealed class CapturedExchange
{
    public required DateTimeOffset Timestamp { get; init; }

    public required CapturedRequest Request { get; init; }

    public required CapturedResponse Response { get; init; }

    public required long ElapsedMs { get; init; }

    public required bool WasCanned { get; init; }
}

public sealed class CapturedRequest
{
    public required string Url { get; init; }

    public required string Method { get; init; }

    public required byte[] Body { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
}

public sealed class CapturedResponse
{
    public required int StatusCode { get; init; }

    public required byte[] Body { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
}
