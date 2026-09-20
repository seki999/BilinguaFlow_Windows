using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Speech;
using BilinguaFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Asr;

public sealed class TranscriptionSessionFactory(ISpeechRecognitionService recognizer, ILoggerFactory loggerFactory)
    : ITranscriptionSessionFactory
{
    public ITranscriptionSession Create(CaptureSource source, CaptureMode mode, bool movieDebugMode = false) =>
        new TranscriptionSession(source, recognizer, loggerFactory.CreateLogger<TranscriptionSession>(),
            SegmentationProfiles.For(mode, source, movieDebugMode));
}
