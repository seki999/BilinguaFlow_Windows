using System.Diagnostics;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace BilinguaFlow.Asr;

public sealed class SenseVoiceSpeechRecognitionService(ILogger<SenseVoiceSpeechRecognitionService> logger)
    : ISpeechRecognitionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private SourceLanguage? _loadedLanguage;
    public bool IsInitialized => _recognizer is not null;

    public async Task<TimeSpan> InitializeAsync(SenseVoiceModelFiles files, SourceLanguage language, CancellationToken cancellationToken)
    {
        if (!files.Exists) throw new FileNotFoundException($"SenseVoice model not found. Expected directory: {files.Directory}");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is not null && _loadedLanguage == language) return TimeSpan.Zero;
            DisposeRecognizer();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _recognizer = await Task.Run(() =>
                {
                    var config = new OfflineRecognizerConfig();
                    config.FeatConfig.SampleRate = AudioPreprocessor.TargetSampleRate;
                    config.FeatConfig.FeatureDim = 80;
                    config.ModelConfig.Tokens = files.TokensPath;
                    config.ModelConfig.SenseVoice.Model = files.ModelPath;
                    config.ModelConfig.SenseVoice.Language = language == SourceLanguage.Japanese ? "ja" : "en";
                    config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                    config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                    config.ModelConfig.Provider = "cpu";
                    config.ModelConfig.Debug = 0;
                    config.DecodingMethod = "greedy_search";
                    return new OfflineRecognizer(config);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (DllNotFoundException ex) { throw new InvalidOperationException("SenseVoice native library could not be loaded. Verify the Windows x64 sherpa-onnx runtime files.", ex); }
            catch (BadImageFormatException ex) { throw new InvalidOperationException("The SenseVoice native runtime does not match this application's x64 architecture.", ex); }
            _loadedLanguage = language;
            stopwatch.Stop();
            logger.LogInformation("SenseVoice initialized in {ElapsedMs} ms for {Language}", stopwatch.ElapsedMilliseconds, language);
            return stopwatch.Elapsed;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RecognizeAsync(float[] samples, SourceLanguage language, CancellationToken cancellationToken)
    {
        if (samples.Length == 0) return string.Empty;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recognizer = _recognizer ?? throw new InvalidOperationException("SenseVoice has not been initialized.");
            return await Task.Run(() =>
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(AudioPreprocessor.TargetSampleRate, samples);
                recognizer.Decode(stream);
                return stream.Result.Text?.Trim() ?? string.Empty;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("SenseVoice native runtime failed during recognition.", ex);
        }
        finally { _gate.Release(); }
    }

    private void DisposeRecognizer()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _loadedLanguage = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { DisposeRecognizer(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
