namespace NebulaServer.Models.Media;

public enum ExtractionErrorCode
{
    UnsupportedUrl,
    AuthenticationRequired,
    MediaUnavailable,
    PlatformBlocked,
    AdultContentBlocked,
    ExtractionFailed,
    NetworkError
}
