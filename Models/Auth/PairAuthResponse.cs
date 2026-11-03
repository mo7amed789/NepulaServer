namespace NebulaServer.Models.Auth;

public sealed class PairAuthResponse
{
    public string AccessToken { get; set; } = string.Empty;

    public string TokenType { get; set; } = "Bearer";

    public int ExpiresInSeconds { get; set; }
}
