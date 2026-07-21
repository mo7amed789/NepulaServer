namespace NebulaServer.Models.Licensing
{
public enum LicenseState
{
    NotActivated = 0,
    Valid = 1,
    Expired = 2,
    Revoked = 3,
    OfflineGrace = 4,
    ValidationFailed = 5,
    MachineMismatch = 6,
    NetworkFailure = 7,
    Timeout = 8,
    ServerUnavailable = 9,
    InvalidSignature = 10,
    TamperedLicense = 11
}
}
