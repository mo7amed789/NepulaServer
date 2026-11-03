namespace NebulaServer.Models.Common;

public sealed class ApiResponse<T>
{
    public bool Success { get; set; }

    public string? Message { get; set; }

    public string? ErrorCode { get; set; }

    public DateTime Timestamp { get; set; }

    public T? Data { get; set; }
}