using System.Text.Json.Serialization;

namespace NebulaServer.Models.Errors;

/// <summary>
/// Uniform error body for every failed API call. The shape is deliberately
/// backwards compatible with what the dashboard and the Android client already
/// read (<c>success</c> + <c>message</c>), while adding the structured
/// <c>error</c> object that explains <em>why</em> it failed and <em>how</em> to
/// fix it.
/// </summary>
public sealed class ApiErrorResponse
{
    [JsonPropertyName("success")] public bool Success => false;

    /// <summary>
    /// One line the UI can show as-is: what went wrong plus the fix.
    /// </summary>
    [JsonPropertyName("message")] public required string Message { get; init; }

    [JsonPropertyName("error")] public required ApiError Error { get; init; }

    /// <summary>Same response with the pre-existing code attached.</summary>
    public ApiErrorResponse WithLegacyCode(string legacyCode) =>
        new() { Message = Message, Error = Error with { LegacyCode = legacyCode } };
}

public sealed record ApiError
{
    /// <summary>Stable machine-readable code, e.g. <c>cookies_required</c>.</summary>
    [JsonPropertyName("code")] public required string Code { get; init; }

    /// <summary>Short headline for the failure.</summary>
    [JsonPropertyName("title")] public required string Title { get; init; }

    /// <summary>Why the request failed, in plain language.</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>Concrete steps the user can take to fix it.</summary>
    [JsonPropertyName("fix")] public required string Fix { get; init; }

    /// <summary>HTTP status that was returned.</summary>
    [JsonPropertyName("status")] public required int Status { get; init; }

    /// <summary>True when simply repeating the request may succeed.</summary>
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }

    /// <summary>Correlates the response with the server log line.</summary>
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }

    /// <summary>
    /// Previous enum-style code kept for clients that already branch on it
    /// (for example "AuthenticationRequired"), null when there was none.
    /// </summary>
    [JsonPropertyName("legacyCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCode { get; init; }

    /// <summary>Extra context, only populated in the Development environment.</summary>
    [JsonPropertyName("details")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; init; }
}
