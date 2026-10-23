namespace FingReader.WindowsHello;

public enum HelloStatus
{
    // Default values must never grant access.
    Error = 0,
    Verified,
    Canceled,
    DeviceNotPresent,
    NotConfiguredForUser,
    DisabledByPolicy,
    DeviceBusy,
    RetriesExhausted,
    InvalidOwnerWindow,
    OwnerNotActive,
    UnsupportedSystem
}

/// <summary>Outcome of a single verification. Only IsVerified permits access.</summary>
public sealed record HelloResult(HelloStatus Status = HelloStatus.Error, int HResult = 0)
{
    public bool IsVerified => Status == HelloStatus.Verified;
}

public enum HelloAvailability
{
    Error = 0,
    Available,
    DeviceNotPresent,
    NotConfiguredForUser,
    DisabledByPolicy,
    DeviceBusy,
    UnsupportedSystem
}
