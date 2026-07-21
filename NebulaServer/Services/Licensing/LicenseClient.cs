using Microsoft.Extensions.Logging;
using NebulaServer.Models;
using NebulaServer.Models.Common;
using NebulaServer.Models.Licensing;
using NebulaServer.Services.Pairing;
using NebulaServer.Settings;
using System.Net.Http.Json;
using System.Text.Json;
using ValidationResult = NebulaServer.Models.Licensing.ValidationResult;

namespace NebulaServer.Services.Licensing;

public sealed class LicenseClient : ILicenseClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<LicenseClient> _logger;
    private readonly IPairingService _pairingService;

    public LicenseClient(
        HttpClient httpClient,
        IPairingService pairingService,
        ILogger<LicenseClient> logger)
    {
        _httpClient = httpClient;
        _pairingService = pairingService;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(LicensingSecurity.LicensingServerBaseUrl, UriKind.Absolute);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        // تمرير الـ CancellationToken لضمان الاستجابة لطلبات الإلغاء
        var pairing = await _pairingService
            .GetPairingInfoAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new HttpRequestMessage(method, url);

        // إضافة المفتاح الخاص بالمصادقة
        request.Headers.Add("X-Nebula-Key", pairing.PairingKey);

        request.Content = JsonContent.Create(body, options: JsonOptions);

        return request;
    }

    public async Task<bool> IsServerReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // نعتبر الخادم متاحاً فقط إذا كان الرد ناجحاً 
            // أو على الأقل ليس خطأ من فئة 5xx مثل 502 Bad Gateway أو 530 Cloudflare Error
            return (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Licensing server is not reachable.");
            return false;
        }
    }

    public async Task<ActivationResult> ActivateAsync(
        ClientActivateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var requestMessage = await CreateRequestAsync(
            HttpMethod.Post,
            "/api/client/activate",
            request,
            cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient
            .SendAsync(requestMessage, cancellationToken)
            .ConfigureAwait(false);

        var wrapper = await DeserializeEnvelopeAsync<ActivateResponse>(
            response,
            "POST /api/client/activate",
            cancellationToken).ConfigureAwait(false);

        var activateResponse = wrapper?.Data;

        return new ActivationResult
        {
            Success = wrapper?.Success ?? response.IsSuccessStatusCode,
            Status = activateResponse?.Status ?? wrapper?.Message ?? string.Empty,
            Message = wrapper?.Message ?? activateResponse?.Message ?? string.Empty,
            LicenseKey = activateResponse?.LicenseKey ?? request.LicenseKey,
            ExpiresAt = activateResponse?.ExpiresAt ?? DateTime.UtcNow,
            SignedAtUtc = activateResponse?.SignedAtUtc,
            OfflineGraceUntilUtc = activateResponse?.OfflineGraceUntilUtc,
            SignatureAlgorithm = activateResponse?.SignatureAlgorithm ?? string.Empty,
            Signature = activateResponse?.Signature ?? string.Empty
        };
    }

    public async Task<ValidationResult> ValidateAsync(
        ClientValidateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var requestMessage = await CreateRequestAsync(
            HttpMethod.Post,
            "/api/client/validate",
            request,
            cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient
            .SendAsync(requestMessage, cancellationToken)
            .ConfigureAwait(false);

        var wrapper = await DeserializeEnvelopeAsync<ValidateResponse>(
            response,
            "POST /api/client/validate",
            cancellationToken).ConfigureAwait(false);

        var validateResponse = wrapper?.Data;

        return new ValidationResult
        {
            IsValid = wrapper?.Success == true && (validateResponse?.IsValid ?? false),
            Status = validateResponse?.Status ?? wrapper?.Message ?? "Unknown",
            Message = validateResponse?.Message ?? wrapper?.Message ?? "Validation failed.",
            ExpiresAt = validateResponse?.ExpiresAt ?? DateTime.UtcNow,
            DaysRemaining = validateResponse?.DaysRemaining ?? 0,
            SignedAtUtc = validateResponse?.SignedAtUtc,
            OfflineGraceUntilUtc = validateResponse?.OfflineGraceUntilUtc,
            SignatureAlgorithm = validateResponse?.SignatureAlgorithm ?? string.Empty,
            Signature = validateResponse?.Signature ?? string.Empty
        };
    }

    // التعديل الرئيسي هنا: إرجاع Task<HeartbeatResult> ومعالجة الرد بشكل صحيح
    public async Task<HeartbeatResult> HeartbeatAsync(
        ClientHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        using var requestMessage = await CreateRequestAsync(
            HttpMethod.Post,
            "/api/client/heartbeat",
            request,
            cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient
            .SendAsync(requestMessage, cancellationToken)
            .ConfigureAwait(false);

        // نقوم بفك تشفير الرد لنحصل على حالة النبضة والوقت من الخادم
        var wrapper = await DeserializeEnvelopeAsync<HeartbeatResponse>(
            response,
            "POST /api/client/heartbeat",
            cancellationToken).ConfigureAwait(false);

        var heartbeatResponse = wrapper?.Data;

        return new HeartbeatResult
        {
            Success = wrapper?.Success ?? response.IsSuccessStatusCode,
            Message = wrapper?.Message ?? string.Empty,
            ErrorCode = wrapper?.ErrorCode,
            Status = heartbeatResponse?.Status ?? string.Empty,
            CurrentServerTimeUtc = heartbeatResponse?.CurrentServerTimeUtc
        };
    }

    private async Task<ApiResponse<T>?> DeserializeEnvelopeAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Licensing request body could not be read for {Operation}.", operation);
            throw;
        }

        // تسجيل الخطأ فوراً في حال كان الرد غير ناجح لتجنب تكرار الكود
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Licensing request failed for {operation} with an empty response body.");
            }

            return default;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Licensing server returned {(int)response.StatusCode}: {body}");
            }

            throw new HttpRequestException(
                $"Licensing server returned non-JSON response: {body}");
        }

        try
        {
            return JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Licensing response JSON could not be parsed for {Operation}. Body={Body}",
                operation,
                body);

            throw new HttpRequestException(
                "Licensing server returned an invalid response.",
                ex);
        }
    }

    private async Task LogFailureAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            body = $"<failed to read response body: {ex.Message}>";
        }

        _logger.LogWarning(
            "Licensing request failed for {Operation}. StatusCode={StatusCode}. ResponseBody={ResponseBody}",
            operation,
            (int)response.StatusCode,
            body);

        var validationErrors = ExtractValidationErrors(body);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning(
                "Licensing request validation errors for {Operation}: {ValidationErrors}",
                operation,
                string.Join(" | ", validationErrors));
        }
    }

    private static List<string> ExtractValidationErrors(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var errors = new List<string>();

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var errorProperty in errorsElement.EnumerateObject())
                    {
                        foreach (var item in errorProperty.Value.EnumerateArray())
                        {
                            errors.Add($"{errorProperty.Name}: {item.GetString()}");
                        }
                    }
                }

                if (root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                {
                    errors.Add($"title: {titleElement.GetString()}");
                }

                if (root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String)
                {
                    errors.Add($"detail: {detailElement.GetString()}");
                }
            }

            return errors;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}