using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Speech;
using BilinguaFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Asr;

public sealed class TranscriptionSessionFactory(SenseVoiceSpeechRecognitionService senseVoice,
    WhisperSpeechRecognitionService whisper, ILoggerFactory loggerFactory)
    : ITranscriptionSessionFactory
{
    public ITranscriptionSession Create(CaptureSource source, CaptureMode mode, AsrEngine engine,
        bool movieDebugMode = false, bool compareMode = false)
    {
        var primary = engine == AsrEngine.Whisper ? (ISpeechRecognitionService)whisper : senseVoice;
        var comparison = compareMode
            ? engine == AsrEngine.Whisper ? (ISpeechRecognitionService)senseVoice : whisper
            : null;
        return new TranscriptionSession(source, primary, loggerFactory.CreateLogger<TranscriptionSession>(),
            SegmentationProfiles.For(mode, source, movieDebugMode, engine), comparison);
    }
}
