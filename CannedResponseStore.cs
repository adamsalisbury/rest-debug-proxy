using System.Text.Json;

namespace RestDebugProxy;

public sealed class CannedResponseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private List<CannedResponse> _responses = new();

    public string Directory { get; }

    public CannedResponseStore(string directory)
    {
        Directory = directory;
    }

    public int LoadAll()
    {
        var loaded = new List<CannedResponse>();

        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonSerializer.Deserialize<CannedResponse>(json, JsonOptions);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Method) && !string.IsNullOrWhiteSpace(entry.Route))
                    {
                        loaded.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // Skip invalid files silently — they will not contribute to the canned set.
                }
                catch (IOException)
                {
                    // Skip unreadable files silently.
                }
            }
        }

        lock (_lock)
        {
            _responses = loaded;
        }

        return loaded.Count;
    }

    public CannedResponse? Find(string method, string route)
    {
        List<CannedResponse> snapshot;
        lock (_lock)
        {
            snapshot = _responses;
        }

        foreach (var entry in snapshot)
        {
            if (string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Route, route, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _responses.Count;
            }
        }
    }
}
