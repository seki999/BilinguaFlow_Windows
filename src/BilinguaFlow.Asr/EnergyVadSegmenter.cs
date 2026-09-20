using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Asr;

public sealed record SpeechSegmentationOptions(
    int SampleRate = 16_000,
    int FrameMilliseconds = 20,
    int MinimumSpeechMilliseconds = 500,
    int MinimumRecognitionSegmentMilliseconds = 1_200,
    int EndSilenceMilliseconds = 900,
    int MergeGapMilliseconds = 1_200,
    int PreRollMilliseconds = 250,
    int PostRollMilliseconds = 250,
    int MaximumSegmentMilliseconds = 12_000,
    float SpeechThreshold = 0.012f,
    float MinimumZeroCrossingRate = 0.008f,
    float MaximumZeroCrossingRate = 0.45f,
    bool UseZeroCrossingHeuristic = true,
    bool EnableActivityFallback = false,
    int ActivityFlushMilliseconds = 3_000,
    float MeaningfulAudioRms = 0.003f,
    bool DebugFixedChunkMode = false);

public static class SegmentationProfiles
{
    public static SpeechSegmentationOptions For(CaptureMode mode, CaptureSource source, bool movieDebugMode = false,
        AsrEngine engine = AsrEngine.SenseVoice) => (mode, source, engine) switch
    {
        (CaptureMode.Movie, _, AsrEngine.Whisper) => new(
            MinimumSpeechMilliseconds: 500,
            MinimumRecognitionSegmentMilliseconds: 1_500,
            EndSilenceMilliseconds: 900,
            MergeGapMilliseconds: 1_200,
            PreRollMilliseconds: 250,
            PostRollMilliseconds: 250,
            MaximumSegmentMilliseconds: 12_000,
            SpeechThreshold: 0.006f,
            UseZeroCrossingHeuristic: false,
            EnableActivityFallback: true,
            ActivityFlushMilliseconds: movieDebugMode ? 3_000 : 6_000,
            MeaningfulAudioRms: 0.0025f,
            DebugFixedChunkMode: movieDebugMode),
        (CaptureMode.Movie, _, _) => new(
            SpeechThreshold: 0.006f,
            UseZeroCrossingHeuristic: false,
            EnableActivityFallback: true,
            ActivityFlushMilliseconds: movieDebugMode ? 2_500 : 3_000,
            MeaningfulAudioRms: 0.0025f,
            DebugFixedChunkMode: movieDebugMode),
        (CaptureMode.Microphone, _, _) => new(
            MinimumSpeechMilliseconds: 350,
            MinimumRecognitionSegmentMilliseconds: 800,
            EndSilenceMilliseconds: 650,
            MergeGapMilliseconds: 900,
            PreRollMilliseconds: 200,
            PostRollMilliseconds: 200),
        (CaptureMode.Meeting, CaptureSource.Microphone, _) => new(
            MinimumSpeechMilliseconds: 400,
            MinimumRecognitionSegmentMilliseconds: 1_000,
            EndSilenceMilliseconds: 750,
            MergeGapMilliseconds: 1_000),
        _ => new()
    };
}

public sealed record SpeechSegment(
    float[] Samples,
    int SampleRate,
    bool WasMerged,
    int OriginalSegmentCount,
    bool FlushedByTimeout,
    bool FlushedByStop,
    SegmentSubmissionReason SubmissionReason,
    float Rms)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

public enum SegmentSubmissionReason { VadEnd, Timeout, MaxLength, StopFlush, DebugChunk }
public enum SegmentationEventKind { SpeechDetected, Rejected, Buffered, Merged, TimeoutFlush, StopFlush }
public sealed record SegmentationEvent(SegmentationEventKind Kind, string Message);

public sealed class EnergyVadSegmenter
{
    private sealed record Candidate(float[] Samples, long SpeechStart, long SpeechEnd, int SpeechSampleCount, int Count, bool Merged);

    private readonly SpeechSegmentationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<float> _frame = [];
    private readonly List<float> _utterance = [];
    private readonly float[] _preRoll;
    private readonly int _frameSamples;
    private readonly int _minimumSpeechSamples;
    private readonly int _minimumRecognitionSamples;
    private readonly int _endSilenceSamples;
    private readonly int _mergeGapSamples;
    private readonly int _postRollSamples;
    private readonly int _maximumSamples;
    private readonly int _activityFlushSamples;
    private readonly List<float> _activityBuffer = [];
    private double _activityEnergy;
    private int _activityNonSilentSamples;
    private int _preRollWriteIndex;
    private int _preRollCount;
    private int _speechSamples;
    private int _silenceSamples;
    private long _sampleCursor;
    private long _speechStart;
    private long _lastSpeechEnd;
    private Candidate? _pending;
    private DateTimeOffset _pendingDeadline;
    private DateTimeOffset _activityDeadline;
    private bool _activeWillMergePending;
    private double _noiseFloor = 0.002;

    public EnergyVadSegmenter(SpeechSegmentationOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new SpeechSegmentationOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _frameSamples = MillisecondsToSamples(_options.FrameMilliseconds);
        _minimumSpeechSamples = MillisecondsToSamples(_options.MinimumSpeechMilliseconds);
        _minimumRecognitionSamples = MillisecondsToSamples(_options.MinimumRecognitionSegmentMilliseconds);
        _endSilenceSamples = MillisecondsToSamples(_options.EndSilenceMilliseconds);
        _mergeGapSamples = MillisecondsToSamples(_options.MergeGapMilliseconds);
        _postRollSamples = MillisecondsToSamples(_options.PostRollMilliseconds);
        _maximumSamples = MillisecondsToSamples(_options.MaximumSegmentMilliseconds);
        _activityFlushSamples = MillisecondsToSamples(_options.ActivityFlushMilliseconds);
        _preRoll = new float[MillisecondsToSamples(_options.PreRollMilliseconds)];
    }

    public event EventHandler<SegmentationEvent>? Diagnostic;

    public IReadOnlyList<SpeechSegment> Process(ReadOnlySpan<float> samples)
    {
        var completed = new List<SpeechSegment>();
        foreach (var sample in samples)
        {
            _frame.Add(sample);
            if (_frame.Count == _frameSamples) ProcessFrame(completed);
        }
        return completed;
    }

    private void ProcessFrame(List<SpeechSegment> completed)
    {
        var (rms, zeroCrossingRate) = AnalyzeFrame(_frame);
        var threshold = Math.Max(_options.SpeechThreshold, (float)_noiseFloor * 2.5f);
        var crossingAccepted = !_options.UseZeroCrossingHeuristic ||
            (zeroCrossingRate >= _options.MinimumZeroCrossingRate && zeroCrossingRate <= _options.MaximumZeroCrossingRate);
        var speechLike = rms >= threshold && crossingAccepted;

        if (!speechLike && rms < threshold) _noiseFloor = _noiseFloor * 0.98 + rms * 0.02;

        if (_options.EnableActivityFallback && ProcessActivityFallback(rms, completed))
        {
            _sampleCursor += _frame.Count;
            _frame.Clear();
            return;
        }

        if (_options.DebugFixedChunkMode)
        {
            _sampleCursor += _frame.Count;
            _frame.Clear();
            return;
        }

        if (speechLike)
        {
            if (_speechSamples == 0)
            {
                _speechStart = _sampleCursor;
                _activeWillMergePending = _pending is not null && _speechStart - _pending.SpeechEnd <= _mergeGapSamples;
                AppendPreRoll();
            }
            _utterance.AddRange(_frame);
            _speechSamples += _frame.Count;
            _silenceSamples = 0;
            _lastSpeechEnd = _sampleCursor + _frame.Count;
        }
        else if (_speechSamples > 0)
        {
            _utterance.AddRange(_frame);
            _silenceSamples += _frame.Count;
        }
        else
        {
            AddToPreRoll(_frame);
        }

        _sampleCursor += _frame.Count;
        _frame.Clear();

        if (_utterance.Count >= _maximumSamples)
            CompleteRawSegment(completed, includePostRoll: false, SegmentSubmissionReason.MaxLength);
        else if (_speechSamples > 0 && _silenceSamples >= _endSilenceSamples)
            CompleteRawSegment(completed, includePostRoll: true, SegmentSubmissionReason.VadEnd);

        FlushPendingIfTimedOut(completed);
    }

    private void CompleteRawSegment(List<SpeechSegment> completed, bool includePostRoll, SegmentSubmissionReason reason)
    {
        if (_speechSamples < _minimumSpeechSamples)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Rejected,
                $"Rejected short/noisy speech candidate: {_speechSamples * 1000.0 / _options.SampleRate:F0} ms."));
            ResetActiveSegment();
            return;
        }

        var trailing = includePostRoll ? Math.Min(_postRollSamples, _silenceSamples) : 0;
        var contentCount = Math.Min(_utterance.Count, _utterance.Count - _silenceSamples + trailing);
        var candidate = new Candidate(_utterance.GetRange(0, Math.Max(0, contentCount)).ToArray(),
            _speechStart, _lastSpeechEnd, _speechSamples, 1, false);
        Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.SpeechDetected,
            $"Detected speech candidate: {_speechSamples * 1000.0 / _options.SampleRate:F0} ms."));
        ResetActiveSegment();
        AcceptCandidate(candidate, completed, reason);
    }

    private void AcceptCandidate(Candidate candidate, List<SpeechSegment> completed, SegmentSubmissionReason reason)
    {
        if (_pending is not null && candidate.SpeechStart - _pending.SpeechEnd <= _mergeGapSamples)
        {
            candidate = new Candidate(Combine(_pending.Samples, candidate.Samples), _pending.SpeechStart,
                candidate.SpeechEnd, _pending.SpeechSampleCount + candidate.SpeechSampleCount,
                _pending.Count + 1, true);
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Merged,
                $"Merged {_pending.Count + 1} short speech segments."));
            _pending = null;
        }

        if (candidate.SpeechSampleCount >= _minimumRecognitionSamples)
        {
            completed.Add(ToSegment(candidate, false, false, reason));
            ResetActivityFallback();
            return;
        }

        _pending = candidate;
        var remainingMergeTime = Math.Max(0, _options.MergeGapMilliseconds - _options.EndSilenceMilliseconds);
        _pendingDeadline = _timeProvider.GetUtcNow().AddMilliseconds(remainingMergeTime);
        Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Buffered,
            $"Buffered short speech segment: {candidate.SpeechSampleCount * 1000.0 / _options.SampleRate:F0} ms speech."));
    }

    private void FlushPendingIfTimedOut(List<SpeechSegment> completed)
    {
        if (_pending is null || _activeWillMergePending || _sampleCursor - _pending.SpeechEnd < _mergeGapSamples) return;
        Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.TimeoutFlush,
            "Flushed buffered speech segment after merge timeout."));
        completed.Add(ToSegment(_pending, true, false, SegmentSubmissionReason.Timeout));
        _pending = null;
        ResetActivityFallback();
    }

    public IReadOnlyList<SpeechSegment> FlushExpired()
    {
        var now = _timeProvider.GetUtcNow();
        if (_pending is not null && !_activeWillMergePending && now >= _pendingDeadline)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.TimeoutFlush,
                "Flushed buffered speech segment after wall-clock merge timeout."));
            var segment = ToSegment(_pending, true, false, SegmentSubmissionReason.Timeout);
            _pending = null;
            ResetActivityFallback();
            return [segment];
        }

        if (_options.EnableActivityFallback && _activityBuffer.Count > 0 && now >= _activityDeadline)
        {
            if (!ActivityIsMeaningful())
            {
                Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Rejected,
                    "Rejected movie fallback buffer because it contained only near-silence."));
                ResetActivityFallback();
                return [];
            }

            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.TimeoutFlush,
                "Flushed meaningful movie activity after wall-clock timeout."));
            var segment = CreateActivitySegment(
                _options.DebugFixedChunkMode ? SegmentSubmissionReason.DebugChunk : SegmentSubmissionReason.Timeout,
                true, false);
            ResetAfterActivitySubmission();
            return [segment];
        }

        return [];
    }

    public IReadOnlyList<SpeechSegment> Flush()
    {
        var completed = new List<SpeechSegment>();
        if (_frame.Count > 0)
        {
            while (_frame.Count < _frameSamples) _frame.Add(0);
            ProcessFrame(completed);
        }
        if (_speechSamples >= _minimumSpeechSamples)
            CompleteRawSegment(completed, includePostRoll: true, SegmentSubmissionReason.StopFlush);
        else if (_speechSamples > 0)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Rejected,
                $"Rejected short/noisy speech candidate on Stop: {_speechSamples * 1000.0 / _options.SampleRate:F0} ms."));
            ResetActiveSegment();
        }
        if (_pending is not null)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.StopFlush,
                "Flushed buffered speech segment on Stop."));
            completed.Add(ToSegment(_pending, false, true, SegmentSubmissionReason.StopFlush));
            _pending = null;
        }
        else if (_activityBuffer.Count > 0 && ActivityIsMeaningful())
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.StopFlush,
                "Flushed meaningful movie activity on Stop."));
            completed.Add(CreateActivitySegment(SegmentSubmissionReason.StopFlush, false, true));
        }
        ResetActivityFallback();
        return completed;
    }

    private SpeechSegment ToSegment(Candidate candidate, bool timeout, bool stop, SegmentSubmissionReason reason) =>
        new(candidate.Samples, _options.SampleRate, candidate.Merged, candidate.Count, timeout, stop,
            reason, CalculateRms(candidate.Samples));

    private bool ProcessActivityFallback(float frameRms, List<SpeechSegment> completed)
    {
        if (_activityBuffer.Count == 0)
        {
            if (frameRms < _options.MeaningfulAudioRms) return false;
            _activityDeadline = _timeProvider.GetUtcNow().AddMilliseconds(_options.ActivityFlushMilliseconds);
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.SpeechDetected,
                "Detected meaningful movie-audio activity; fallback buffering started."));
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Buffered,
                "Buffered meaningful movie audio for conservative fallback submission."));
        }

        foreach (var sample in _frame)
        {
            _activityBuffer.Add(sample);
            _activityEnergy += sample * sample;
        }
        if (frameRms >= _options.MeaningfulAudioRms) _activityNonSilentSamples += _frame.Count;

        if (_activityBuffer.Count < _activityFlushSamples) return false;
        if (!ActivityIsMeaningful())
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.Rejected,
                "Rejected movie fallback buffer because it contained only near-silence."));
            ResetActivityFallback();
            return false;
        }

        var reason = _options.DebugFixedChunkMode
            ? SegmentSubmissionReason.DebugChunk
            : SegmentSubmissionReason.Timeout;
        Diagnostic?.Invoke(this, new SegmentationEvent(SegmentationEventKind.TimeoutFlush,
            $"Flushed movie activity fallback after {_activityBuffer.Count * 1000.0 / _options.SampleRate:F0} ms."));
        completed.Add(CreateActivitySegment(reason, true, false));
        ResetAfterActivitySubmission();
        return true;
    }

    private bool ActivityIsMeaningful()
    {
        if (_activityBuffer.Count == 0 || _activityNonSilentSamples < _frameSamples) return false;
        var rms = Math.Sqrt(_activityEnergy / _activityBuffer.Count);
        return rms >= _options.MeaningfulAudioRms * 0.5;
    }

    private SpeechSegment CreateActivitySegment(SegmentSubmissionReason reason, bool timeout, bool stop)
    {
        var samples = _activityBuffer.ToArray();
        return new SpeechSegment(samples, _options.SampleRate, false, 1, timeout, stop, reason,
            CalculateRms(samples));
    }

    private void ResetAfterActivitySubmission()
    {
        ResetActivityFallback();
        _pending = null;
        ResetActiveSegment();
    }

    private void ResetActivityFallback()
    {
        _activityBuffer.Clear();
        _activityEnergy = 0;
        _activityNonSilentSamples = 0;
        _activityDeadline = default;
    }

    private static float CalculateRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        return (float)Math.Sqrt(energy / samples.Length);
    }

    private void ResetActiveSegment()
    {
        _utterance.Clear(); _speechSamples = 0; _silenceSamples = 0;
        _activeWillMergePending = false;
        _preRollCount = 0; _preRollWriteIndex = 0;
    }

    private void AppendPreRoll()
    {
        if (_preRollCount == 0) return;
        var start = (_preRollWriteIndex - _preRollCount + _preRoll.Length) % _preRoll.Length;
        for (var i = 0; i < _preRollCount; i++) _utterance.Add(_preRoll[(start + i) % _preRoll.Length]);
    }

    private void AddToPreRoll(List<float> samples)
    {
        if (_preRoll.Length == 0) return;
        foreach (var sample in samples)
        {
            _preRoll[_preRollWriteIndex] = sample;
            _preRollWriteIndex = (_preRollWriteIndex + 1) % _preRoll.Length;
            if (_preRollCount < _preRoll.Length) _preRollCount++;
        }
    }

    private static (float Rms, float ZeroCrossingRate) AnalyzeFrame(List<float> samples)
    {
        double energy = 0; var crossings = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            energy += samples[i] * samples[i];
            if (i > 0 && (samples[i - 1] >= 0) != (samples[i] >= 0)) crossings++;
        }
        return ((float)Math.Sqrt(energy / samples.Count), (float)crossings / Math.Max(1, samples.Count - 1));
    }

    private int MillisecondsToSamples(int milliseconds) => _options.SampleRate * milliseconds / 1000;

    private static float[] Combine(float[] first, float[] second)
    {
        var combined = new float[first.Length + second.Length];
        first.CopyTo(combined, 0); second.CopyTo(combined, first.Length);
        return combined;
    }
}
