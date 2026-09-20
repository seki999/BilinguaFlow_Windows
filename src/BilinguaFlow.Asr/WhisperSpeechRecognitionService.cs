using System.Diagnostics;
using System.Text;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace BilinguaFlow.Asr;

public sealed class WhisperSpeechRecognitionService(WhisperSettings settings, ILogger<WhisperSpeechRecognitionService> logger)
    : ISpeechRecognitionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private SourceLanguage? _loadedLanguage;
    public AsrEngine Engine => AsrEngine.Whisper;
    public bool IsInitialized => _factory is not null;

    public static string LanguageCode(SourceLanguage language) => language == SourceLanguage.Japanese ? "ja" : "en";

    public async Task<TimeSpan> InitializeAsync(SenseVoiceModelFiles _, SourceLanguage language, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.ModelPath))
            throw new FileNotFoundException($"Whisper model not found. Expected: {settings.ModelPath}", settings.ModelPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is not null && _loadedLanguage == language) return TimeSpan.Zero;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await Task.Run(() =>
                {
                    _processor?.Dispose();
                    _factory ??= WhisperFactory.FromPath(settings.ModelPath);
                    _processor = _factory.CreateBuilder()
                        .WithLanguage(LanguageCode(language))
                        .WithThreads(settings.EffectiveThreads)
                        .Build();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException("Whisper native runtime could not be loaded. Verify the Windows x64 whisper.cpp runtime.", ex);
            }
            _loadedLanguage = language;
            logger.LogInformation("Whisper loaded in {ElapsedMs} ms for {Language} ({Code}); working set {WorkingSetMb:F0} MB",
                stopwatch.ElapsedMilliseconds, language, LanguageCode(language), Process.GetCurrentProcess().WorkingSet64 / 1048576d);
            return stopwatch.Elapsed;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RecognizeAsync(float[] samples, SourceLanguage language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0) return "";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processor is null || _loadedLanguage != language)
                throw new InvalidOperationException("Whisper has not been initialized for the selected language.");
            using var wav = CreateMonoWave(samples);
            var text = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(wav, cancellationToken).ConfigureAwait(false))
                text.Append(segment.Text);
            return text.ToString().Trim();
        }
        finally { _gate.Release(); }
    }

    private static MemoryStream CreateMonoWave(float[] samples)
    {
        var stream = new MemoryStream(44 + samples.Length * sizeof(float));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 4);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)3);
            writer.Write((short)1); writer.Write(16_000); writer.Write(64_000); writer.Write((short)4); writer.Write((short)32);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 4);
            foreach (var sample in samples) writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _processor?.Dispose(); _factory?.Dispose(); _processor = null; _factory = null; }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
