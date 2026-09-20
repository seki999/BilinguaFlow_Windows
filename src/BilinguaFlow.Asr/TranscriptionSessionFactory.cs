using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Asr;

public sealed class TranscriptionSessionFactory(ISpeechRecognitionService recognizer, ILoggerFactory loggerFactory)
    : ITranscriptionSessionFactory
{
    public ITranscriptionSession Create(CaptureSource source) =>
        new TranscriptionSession(source, recognizer, loggerFactory.CreateLogger<TranscriptionSession>());
}
