namespace Major.Core;

public sealed record DeviceInfo(string Serial, string State, string Model, string Connection)
{
    public bool IsAuthorized => State == "device";
}
