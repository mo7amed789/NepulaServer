using System;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.MediaExtraction;

public interface IMediaExtractor
{
    bool CanHandle(Uri url);
    Task<MediaResult> ExtractAsync(Uri url, CancellationToken ct = default);
}
