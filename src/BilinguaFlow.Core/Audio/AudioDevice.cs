namespace BilinguaFlow.Core.Audio;

public enum AudioDeviceKind { Input, Output }

public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind)
{
    public override string ToString() => Name;
}
