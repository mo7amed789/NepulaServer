using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Models.ImageEngine;

namespace NebulaServer.Services.ImageEngine;

public sealed class ImageDownloadManager
{
    private const long MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxDownloadAttempts = 5;
    private readonly HttpClient _httpClient;

    public ImageDownloadManager(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("ImageDownloaderClient");
    }

    public async Task<string> DownloadAndValidateAsync(
        ImageCandidate candidate,
        string destinationDir,
        Uri? sourcePage = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);
        var uri = new Uri(candidate.Url);
        var tempFilePath = Path.Combine(destinationDir, $"{Guid.NewGuid():N}.tmp");

        try
        {
            await DownloadResumableAsync(uri, sourcePage, tempFilePath, cancellationToken);

            var extension = await DetectImageFormatAsync(tempFilePath, cancellationToken);
            if (extension is null)
                throw new InvalidDataException("Downloaded file is not a supported image.");

            var finalFilePath = Path.Combine(destinationDir, $"{Guid.NewGuid():N}.{extension}");
            File.Move(tempFilePath, finalFilePath);
            return finalFilePath;
        }
        catch
        {
            TryDelete(tempFilePath);
            throw;
        }
    }

    private async Task DownloadResumableAsync(
        Uri uri,
        Uri? sourcePage,
        string tempFilePath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingLength = File.Exists(tempFilePath) ? new FileInfo(tempFilePath).Length : 0;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (existingLength > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                if (sourcePage is not null &&
                    (sourcePage.Scheme == Uri.UriSchemeHttp || sourcePage.Scheme == Uri.UriSchemeHttps))
                {
                    request.Headers.Referrer = sourcePage;
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();
                var append = existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (existingLength > 0 && !append)
                {
                    existingLength = 0;
                    TryDelete(tempFilePath);
                }

                var expectedLength = response.Content.Headers.ContentRange?.Length
                    ?? (response.Content.Headers.ContentLength is long contentLength
                        ? contentLength + existingLength
                        : null);
                if (expectedLength is > MaxImageBytes)
                    throw new InvalidDataException("Image exceeds the 50 MB size limit.");

                var mode = append ? FileMode.Append : FileMode.Create;
                await using var fileStream = new FileStream(
                    tempFilePath, mode, FileAccess.Write, FileShare.None, 81920, true);
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await CopyWithLimitAsync(contentStream, fileStream, existingLength, expectedLength, cancellationToken);
                return;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                lastError = ex;
                if (attempt < MaxDownloadAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        throw new HttpRequestException(
            $"Image download failed after {MaxDownloadAttempts} attempts; partial data could not be completed.",
            lastError);
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long existingLength,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = existingLength;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxImageBytes)
                throw new InvalidDataException("Image exceeds the 50 MB size limit.");
            if (expectedLength is > MaxImageBytes)
                throw new InvalidDataException("Image exceeds the 50 MB size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<string?> DetectImageFormatAsync(string filePath, CancellationToken cancellationToken)
    {
        var header = new byte[32];
        await using var stream = File.OpenRead(filePath);
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken);

        if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "png";
        if (read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff) return "jpg";
        if (read >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF89a"u8))) return "gif";
        if (read >= 2 && header[0] == 'B' && header[1] == 'M') return "bmp";
        if (read >= 4 && (header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x49, 0x49, 0x2a, 0x00 }) || header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x4d, 0x4d, 0x00, 0x2a }))) return "tiff";
        if (read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        if (read >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8) && (header.AsSpan(8, 4).SequenceEqual("avif"u8) || header.AsSpan(8, 4).SequenceEqual("avis"u8))) return "avif";

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is best effort; preserve the original failure.
        }
    }
}
