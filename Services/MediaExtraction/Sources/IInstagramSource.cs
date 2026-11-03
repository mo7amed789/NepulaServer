using System;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Models.Media;

namespace NebulaServer.Services.MediaExtraction.Sources;

public interface IInstagramSource
{
    Task<InstagramMediaData?> ExtractAsync(Uri url, CancellationToken ct = default);
}
