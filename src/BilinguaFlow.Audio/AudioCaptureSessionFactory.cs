using BilinguaFlow.Core.Audio;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Audio;

public sealed class AudioCaptureSessionFactory(ILoggerFactory loggerFactory) : IAudioCaptureSessionFactory
{
    public IAudioCaptureSession Create(CaptureSource source) =>
        new WasapiCaptureSession(source, loggerFactory.CreateLogger<WasapiCaptureSession>());
}
