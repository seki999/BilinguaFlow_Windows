using BilinguaFlow.Core.Audio;
using NAudio.CoreAudioApi;

namespace BilinguaFlow.Audio;

public sealed class WasapiAudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioDevice> GetInputDevices() => Enumerate(DataFlow.Capture, AudioDeviceKind.Input);

    public IReadOnlyList<AudioDevice> GetOutputDevices() => Enumerate(DataFlow.Render, AudioDeviceKind.Output);

    private static IReadOnlyList<AudioDevice> Enumerate(DataFlow flow, AudioDeviceKind kind)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDevice(device.ID, device.FriendlyName, kind))
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
