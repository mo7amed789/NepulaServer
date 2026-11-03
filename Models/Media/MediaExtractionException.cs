using System;

namespace NebulaServer.Models.Media;

public sealed class MediaExtractionException : Exception
{
    public ExtractionErrorCode Code { get; }

    public MediaExtractionException(ExtractionErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public MediaExtractionException(ExtractionErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}
