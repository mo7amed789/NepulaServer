namespace NebulaServer.Services.Ngrok;

public sealed class PublicUrlProvider
{
    private readonly object _lock = new();
    private string? _url;

    public string? PublicUrl
    {
        get
        {
            lock (_lock)
            {
                return _url;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrWhiteSpace(_url);
            }
        }
    }

    public string? Get()
    {
        lock (_lock)
        {
            return _url;
        }
    }

    public void Set(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty.", nameof(url));

        lock (_lock)
        {
            _url = url;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _url = null;
        }
    }
}
