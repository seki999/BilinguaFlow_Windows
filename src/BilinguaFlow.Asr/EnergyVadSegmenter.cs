namespace BilinguaFlow.Asr;

public sealed record SpeechSegmentationOptions(
    int SampleRate = 16_000,
    int FrameMilliseconds = 20,
    int MinimumSpeechMilliseconds = 300,
    int EndSilenceMilliseconds = 650,
    int MaximumUtteranceMilliseconds = 12_000,
    float SpeechThreshold = 0.012f);

public sealed class EnergyVadSegmenter
{
    private readonly SpeechSegmentationOptions _options;
    private readonly List<float> _frame = [];
    private readonly List<float> _utterance = [];
    private readonly int _frameSamples;
    private readonly int _minimumSpeechSamples;
    private readonly int _endSilenceSamples;
    private readonly int _maximumSamples;
    private int _speechSamples;
    private int _silenceSamples;

    public EnergyVadSegmenter(SpeechSegmentationOptions? options = null)
    {
        _options = options ?? new SpeechSegmentationOptions();
        _frameSamples = _options.SampleRate * _options.FrameMilliseconds / 1000;
        _minimumSpeechSamples = _options.SampleRate * _options.MinimumSpeechMilliseconds / 1000;
        _endSilenceSamples = _options.SampleRate * _options.EndSilenceMilliseconds / 1000;
        _maximumSamples = _options.SampleRate * _options.MaximumUtteranceMilliseconds / 1000;
    }

    public IReadOnlyList<float[]> Process(ReadOnlySpan<float> samples)
    {
        var completed = new List<float[]>();
        foreach (var sample in samples)
        {
            _frame.Add(sample);
            if (_frame.Count == _frameSamples) ProcessFrame(completed);
        }
        return completed;
    }

    private void ProcessFrame(List<float[]> completed)
    {
        double energy = 0;
        foreach (var sample in _frame) energy += sample * sample;
        var speech = Math.Sqrt(energy / _frame.Count) >= _options.SpeechThreshold;

        if (speech)
        {
            _speechSamples += _frame.Count;
            _silenceSamples = 0;
            _utterance.AddRange(_frame);
        }
        else if (_utterance.Count > 0)
        {
            _silenceSamples += _frame.Count;
            _utterance.AddRange(_frame);
        }

        _frame.Clear();
        if (_utterance.Count >= _maximumSamples)
            Complete(completed, trimSilence: false);
        else if (_silenceSamples >= _endSilenceSamples)
            Complete(completed, trimSilence: true);
    }

    private void Complete(List<float[]> completed, bool trimSilence)
    {
        if (_speechSamples >= _minimumSpeechSamples)
        {
            var count = trimSilence ? Math.Max(0, _utterance.Count - _silenceSamples) : _utterance.Count;
            if (count > 0) completed.Add(_utterance.GetRange(0, count).ToArray());
        }
        _utterance.Clear(); _speechSamples = 0; _silenceSamples = 0;
    }

    public float[]? Flush()
    {
        if (_frame.Count > 0)
        {
            double energy = 0;
            foreach (var sample in _frame) energy += sample * sample;
            if (Math.Sqrt(energy / _frame.Count) >= _options.SpeechThreshold) _speechSamples += _frame.Count;
            _utterance.AddRange(_frame); _frame.Clear();
        }
        if (_speechSamples < _minimumSpeechSamples) { Reset(); return null; }
        var count = Math.Max(0, _utterance.Count - _silenceSamples);
        var result = count > 0 ? _utterance.GetRange(0, count).ToArray() : null;
        Reset();
        return result;
    }

    private void Reset() { _frame.Clear(); _utterance.Clear(); _speechSamples = 0; _silenceSamples = 0; }
}
