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
    float MaximumZeroCrossingRate = 0.45f);

public static class SegmentationProfiles
{
    public static SpeechSegmentationOptions For(CaptureMode mode, CaptureSource source) => (mode, source) switch
    {
        (CaptureMode.Movie, _) => new(),
        (CaptureMode.Microphone, _) => new(
            MinimumSpeechMilliseconds: 350,
            MinimumRecognitionSegmentMilliseconds: 800,
            EndSilenceMilliseconds: 650,
            MergeGapMilliseconds: 900,
            PreRollMilliseconds: 200,
            PostRollMilliseconds: 200),
        (CaptureMode.Meeting, CaptureSource.Microphone) => new(
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
    bool FlushedByStop)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

public sealed record SegmentationEvent(string Message, bool IsRejection = false);

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
    private int _preRollWriteIndex;
    private int _preRollCount;
    private int _speechSamples;
    private int _silenceSamples;
    private long _sampleCursor;
    private long _speechStart;
    private long _lastSpeechEnd;
    private Candidate? _pending;
    private DateTimeOffset _pendingDeadline;
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
        var speechLike = rms >= threshold && zeroCrossingRate >= _options.MinimumZeroCrossingRate &&
            zeroCrossingRate <= _options.MaximumZeroCrossingRate;

        if (!speechLike && rms < threshold) _noiseFloor = _noiseFloor * 0.98 + rms * 0.02;

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
            CompleteRawSegment(completed, includePostRoll: false);
        else if (_speechSamples > 0 && _silenceSamples >= _endSilenceSamples)
            CompleteRawSegment(completed, includePostRoll: true);

        FlushPendingIfTimedOut(completed);
    }

    private void CompleteRawSegment(List<SpeechSegment> completed, bool includePostRoll)
    {
        if (_speechSamples < _minimumSpeechSamples)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(
                $"Rejected short/noisy speech candidate: {_speechSamples * 1000.0 / _options.SampleRate:F0} ms.", true));
            ResetActiveSegment();
            return;
        }

        var trailing = includePostRoll ? Math.Min(_postRollSamples, _silenceSamples) : 0;
        var contentCount = Math.Min(_utterance.Count, _utterance.Count - _silenceSamples + trailing);
        var candidate = new Candidate(_utterance.GetRange(0, Math.Max(0, contentCount)).ToArray(),
            _speechStart, _lastSpeechEnd, _speechSamples, 1, false);
        ResetActiveSegment();
        AcceptCandidate(candidate, completed);
    }

    private void AcceptCandidate(Candidate candidate, List<SpeechSegment> completed)
    {
        if (_pending is not null && candidate.SpeechStart - _pending.SpeechEnd <= _mergeGapSamples)
        {
            candidate = new Candidate(Combine(_pending.Samples, candidate.Samples), _pending.SpeechStart,
                candidate.SpeechEnd, _pending.SpeechSampleCount + candidate.SpeechSampleCount,
                _pending.Count + 1, true);
            Diagnostic?.Invoke(this, new SegmentationEvent($"Merged {_pending.Count + 1} short speech segments."));
            _pending = null;
        }

        if (candidate.SpeechSampleCount >= _minimumRecognitionSamples)
        {
            completed.Add(ToSegment(candidate, false, false));
            return;
        }

        _pending = candidate;
        var remainingMergeTime = Math.Max(0, _options.MergeGapMilliseconds - _options.EndSilenceMilliseconds);
        _pendingDeadline = _timeProvider.GetUtcNow().AddMilliseconds(remainingMergeTime);
        Diagnostic?.Invoke(this, new SegmentationEvent(
            $"Buffered short speech segment: {candidate.SpeechSampleCount * 1000.0 / _options.SampleRate:F0} ms speech."));
    }

    private void FlushPendingIfTimedOut(List<SpeechSegment> completed)
    {
        if (_pending is null || _activeWillMergePending || _sampleCursor - _pending.SpeechEnd < _mergeGapSamples) return;
        Diagnostic?.Invoke(this, new SegmentationEvent("Flushed buffered speech segment after merge timeout."));
        completed.Add(ToSegment(_pending, true, false));
        _pending = null;
    }

    public IReadOnlyList<SpeechSegment> FlushExpired()
    {
        if (_pending is null || _activeWillMergePending || _timeProvider.GetUtcNow() < _pendingDeadline)
            return [];
        Diagnostic?.Invoke(this, new SegmentationEvent("Flushed buffered speech segment after wall-clock merge timeout."));
        var segment = ToSegment(_pending, true, false);
        _pending = null;
        return [segment];
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
            CompleteRawSegment(completed, includePostRoll: true);
        else if (_speechSamples > 0)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent(
                $"Rejected short/noisy speech candidate on Stop: {_speechSamples * 1000.0 / _options.SampleRate:F0} ms.", true));
            ResetActiveSegment();
        }
        if (_pending is not null)
        {
            Diagnostic?.Invoke(this, new SegmentationEvent("Flushed buffered speech segment on Stop."));
            completed.Add(ToSegment(_pending, false, true));
            _pending = null;
        }
        return completed;
    }

    private SpeechSegment ToSegment(Candidate candidate, bool timeout, bool stop) =>
        new(candidate.Samples, _options.SampleRate, candidate.Merged, candidate.Count, timeout, stop);

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
