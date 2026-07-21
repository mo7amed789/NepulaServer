namespace NebulaServer.Models;

public enum JobState
{
    QUEUED = 0,

    DOWNLOADING = 1,

    PROCESSING = 2,

    SEGMENTING = 3,

    SEPARATING_VOCALS = 4,

    MERGING_AUDIO = 5,

    COMPLETED = 6,

    FAILED = 7,

    CANCELED = 8
}
