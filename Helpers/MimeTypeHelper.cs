using System.IO;

namespace NebulaServer.Helpers;

/// <summary>
/// Maps media and archive file extensions to HTTP Content-Type MIME strings.
/// </summary>
public static class MimeTypeHelper
{
    public static string FromFilePath(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
}
