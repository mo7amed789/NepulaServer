using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NebulaServer.Models.ImageEngine;

namespace NebulaServer.Services.ImageEngine.Extractors
{
    public interface IImageExtractor
    {
        bool CanHandle(Uri url);
        Task<IReadOnlyList<ImageCandidate>> ExtractAsync(Uri url, CancellationToken cancellationToken = default);
    }
}