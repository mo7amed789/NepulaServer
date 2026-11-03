using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NebulaServer.Models.Auth;
using NebulaServer.Services.Server;
using NebulaServer.Services.Pairing;

namespace NebulaServer.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IPairingService _pairingService;
    private readonly ServerStateService _serverState;
    private readonly IConfiguration _configuration;

    public AuthController(
        IPairingService pairingService,
        ServerStateService serverState,
        IConfiguration configuration)
    {
        _pairingService = pairingService;
        _serverState = serverState;
        _configuration = configuration;
    }

    [HttpPost("pair")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("StrictAuth")]
    public async Task<ActionResult<PairAuthResponse>> Pair([FromBody] PairAuthRequest request)
    {
        if (request.Schema != 1)
        {
            return BadRequest(new { success = false, message = "Unsupported pairing schema." });
        }

        if (request.ServerId == Guid.Empty || string.IsNullOrWhiteSpace(request.PairingKey))
        {
            return BadRequest(new { success = false, message = "ServerId and PairingKey are required." });
        }

        var pairing = await _pairingService.GetPairingInfoAsync(forceRefresh: true);
        if (pairing.ServerId != request.ServerId)
        {
            return Unauthorized(new { success = false, message = "Invalid ServerId." });
        }

        if (!await _pairingService.ValidateKeyAsync(request.PairingKey))
        {
            return Unauthorized(new { success = false, message = "Invalid PairingKey." });
        }

        var state = _serverState.Get();
        var publicUrl = "";
        var now = DateTimeOffset.UtcNow;
        const int expiresInSeconds = 60 * 60 * 24 * 30;
        var secret = _configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "Server authentication is not configured."
            });
        }

        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = "nebula-client",
            ["schema"] = request.Schema,
            ["serverId"] = pairing.ServerId.ToString(),
            ["publicUrl"] = string.IsNullOrWhiteSpace(request.PublicUrl) ? publicUrl : request.PublicUrl,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(expiresInSeconds).ToUnixTimeSeconds()
        };

        var token = CreateJwt(header, payload, secret);

        return Ok(new PairAuthResponse
        {
            AccessToken = token,
            TokenType = "Bearer",
            ExpiresInSeconds = expiresInSeconds
        });
    }

    private static string CreateJwt(object header, IDictionary<string, object?> payload, string secret)
    {
        static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);
        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }
}

