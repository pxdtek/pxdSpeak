using System.Collections.Concurrent;
using NAudio.Wave;
using SherpaOnnx;

namespace pxdSpeak.Speech;

public enum ReaderState { Idle, Loading, Speaking, Paused }

/// <summary>
/// Reads text aloud with the chosen voice. One reading at a time: a new one stops the last. The voice model stays
/// loaded between readings so the next one starts at once; sound starts after the first sentence is ready while the
/// rest is still being made.
/// </summary>
public sealed class Reader : IDisposable
{
    private readonly object _gate = new();
    private readonly object _modelGate = new();
    private OfflineTts? _model;
    private string? _modelKey;
    private CancellationTokenSource? _current;
    private WaveOutEvent? _out;
    private ChunkQueue? _queue;
    private int _generation;

    public ReaderState State { get; private set; } = ReaderState.Idle;

    /// <summary>Raised on a worker thread whenever <see cref="State"/> changes.</summary>
    public event Action<ReaderState>? StateChanged;

    /// <summary>Raised when a reading couldn't be done (voice missing, model broken…).</summary>
    public event Action<string>? Failed;

    public float Volume { get; set; } = 1f;

    public bool IsBusy => State is ReaderState.Loading or ReaderState.Speaking or ReaderState.Paused;

    public void Speak(string text, VoiceInfo voice, float speed)
    {
        text = TextPrep.Clean(text);
        if (text.Length == 0)
            return;
        Begin((ct, gen) => Run(text, null, voice, speed, ct, gen));
    }

    /// <summary>
    /// Reads words one sentence at a time and reports each word as it becomes audible (for highlighting it where the
    /// user selected it). Windows voices give exact word times; the others are shared out within each sentence.
    /// </summary>
    public void SpeakTracked(TrackedText tracked, VoiceInfo voice, float speed)
    {
        if (tracked.Words.Count == 0 || tracked.Start >= tracked.Words.Count)
            return;
        Begin((ct, gen) => Run(null, tracked, voice, speed, ct, gen));
    }

    private void Begin(Action<CancellationToken, int> run)
    {
        Stop();
        var cts = new CancellationTokenSource();
        int gen;
        lock (_gate)
        {
            _current = cts;
            gen = ++_generation;
        }

        Set(ReaderState.Loading);
        var thread = new Thread(() => run(cts.Token, gen)) { IsBackground = true, Name = "pxdSpeak reader" };
        thread.Start();
    }

    /// <summary>How far ahead of the speaker the audio is handed over; word markers are delayed by this much.</summary>
    private const int LatencyMs = 120;

    private void Run(string? text, TrackedText? tracked, VoiceInfo voice, float speed, CancellationToken ct, int gen)
    {
        ChunkQueue? queue = null;
        WaveOutEvent? output = null;

        bool Play(float[] samples, int rate, List<(int Offset, Action Fire)>? markers)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (queue is null)
            {
                queue = new ChunkQueue(rate, this);
                output = new WaveOutEvent { DesiredLatency = LatencyMs, NumberOfBuffers = 3 };
                output.Init(queue);
                lock (_gate)
                {
                    if (gen != _generation)
                        return false;
                    _queue = queue;
                    _out = output;
                }

                output.Play();
                Set(ReaderState.Speaking);
            }

            queue.Add(samples, markers);
            return true;
        }

        try
        {
            using var synth = CreateSynth(voice);
            if (tracked is null)
                synth.Speak(text!, speed, (s, r) => Play(s, r, null), ct);
            else
            {
                foreach (var (first, last) in Tracking.Sentences(tracked))
                {
                    if (ct.IsCancellationRequested)
                        break;
                    var words = Enumerable.Range(first, last - first + 1).Select(i => Tracking.Spoken(tracked.Words[i])).ToList();
                    var sentence = string.Join(" ", words);
                    float[] samples;
                    int rate;
                    double[] starts;
                    if (synth is IWordTimed timed)
                    {
                        var (s, r, cues) = timed.SpeakTimed(sentence, speed, ct);
                        (samples, rate) = (s, r);
                        starts = FromCues(words, cues) ?? Tracking.Estimate(s, r, words);
                    }
                    else
                    {
                        var parts = new List<float>();
                        rate = 22050;
                        synth.Speak(sentence, speed, (s, r) =>
                        {
                            rate = r;
                            parts.AddRange(s);
                            return !ct.IsCancellationRequested;
                        }, ct);
                        samples = parts.ToArray();
                        starts = Tracking.Estimate(samples, rate, words);
                    }

                    if (samples.Length == 0)
                        continue;
                    var markers = new List<(int, Action)>();
                    for (var k = 0; k < words.Count; k++)
                    {
                        var word = first + k;
                        var (f, l) = (first, last);
                        markers.Add(((int)(starts[k] * rate), () => tracked.OnWord?.Invoke(word, f, l)));
                    }

                    if (!Play(samples, rate, markers))
                        break;
                }
            }

            if (queue is null)
            {
                Finish(gen);
                return;
            }

            queue.Complete();
            // Wait for the last sound to play out (or a stop).
            while (!ct.IsCancellationRequested && output!.PlaybackState != PlaybackState.Stopped)
                Thread.Sleep(50);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Failed?.Invoke(ex.Message);
        }
        finally
        {
            try { output?.Stop(); } catch { /* already gone */ }
            output?.Dispose();
            Finish(gen);
        }
    }

    /// <summary>Word starts from a voice's own word cues (character positions in the sentence); null if they don't fit.</summary>
    private static double[]? FromCues(List<string> words, List<(int Char, double Seconds)> cues)
    {
        if (cues.Count == 0)
            return null;
        var starts = new double[words.Count];
        var pos = 0;
        for (var i = 0; i < words.Count; i++)
        {
            // The cue at or just before where this word starts in the joined sentence.
            var cue = cues.LastOrDefault(c => c.Char <= pos + 1);
            starts[i] = cue == default ? (i > 0 ? starts[i - 1] : 0) : cue.Seconds;
            if (i > 0 && starts[i] < starts[i - 1])
                starts[i] = starts[i - 1];
            pos += words[i].Length + 1;
        }

        return starts;
    }

    private void Finish(int gen)
    {
        lock (_gate)
        {
            if (gen != _generation)
                return;
            _out = null;
            _queue = null;
        }

        Set(ReaderState.Idle);
    }

    private ISynth CreateSynth(VoiceInfo voice)
    {
        if (voice.Family == VoiceFamily.Windows)
            return new WindowsSynth(voice);
        if (!VoiceStore.IsInstalled(voice))
            throw new InvalidOperationException($"{voice.Name} isn't downloaded yet.");
        if (voice.Family == VoiceFamily.Chatterbox)
            return new ChatterboxSynth(ClipFor(voice));
        return new SherpaSynth(ModelFor(voice), voice);
    }

    /// <summary>A Kokoro voice reading this (about 15 s) is what Chatterbox copies a "from Kokoro" voice from.</summary>
    private const string ReferenceText =
        "The old lighthouse stood at the edge of the cliff, its paint worn thin by a hundred winters. Every evening the keeper " +
        "climbed the spiral stairs, lit the lamp, and watched the ships slip past in the dark. Some nights were calm. Others roared " +
        "with wind and rain, but the light never failed.";

    /// <summary>The audio clip a Chatterbox voice copies: none (its built-in voice), the user's clip, or a Kokoro recording made once.</summary>
    private string ClipFor(VoiceInfo voice)
    {
        if (voice.Id.StartsWith(VoiceCatalog.ChatterboxClipPrefix, StringComparison.Ordinal))
            return Path.Combine(Chatterbox.ClipsFolder, voice.Id[VoiceCatalog.ChatterboxClipPrefix.Length..]);
        if (!voice.Id.StartsWith(VoiceCatalog.ChatterboxKokoroPrefix, StringComparison.Ordinal))
            return "";
        var kokoro = VoiceCatalog.Find("kokoro:" + voice.Id[VoiceCatalog.ChatterboxKokoroPrefix.Length..])
                     ?? throw new InvalidOperationException("Unknown Kokoro voice");
        var dir = Path.Combine(Chatterbox.Folder, "kokoro-clips");
        var path = Path.Combine(dir, kokoro.Id[7..] + ".wav");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(dir);
            SaveWav(ReferenceText, kokoro, 1f, path + ".tmp");
            File.Move(path + ".tmp", path, overwrite: true);
        }

        return path;
    }

    /// <summary>The loaded model for a voice, loading it (and unloading the previous one) when it changes.</summary>
    private OfflineTts ModelFor(VoiceInfo voice)
    {
        var key = SherpaSynth.KeyOf(voice);
        lock (_modelGate)
        {
            if (_model is not null && _modelKey == key)
                return _model;
            DisposeModel();
            _model = null;
            _model = SherpaSynth.Load(voice);
            _modelKey = key;
            return _model;
        }
    }

    /// <summary>Waits for a generation still running on the old model (it stops at its next sentence) before freeing it.</summary>
    private void DisposeModel()
    {
        if (_model is { } old)
            lock (old)
                old.Dispose();
    }

    /// <summary>Loads a voice's model ahead of time so the first reading starts quickly. Quietly does nothing on failure.</summary>
    public void Preload(VoiceInfo voice)
    {
        if (voice.Family == VoiceFamily.Windows || !VoiceStore.IsInstalled(voice))
            return;
        if (voice.Family == VoiceFamily.Chatterbox)
        {
            Chatterbox.Preload();
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { ModelFor(voice); } catch { /* reported when it is used */ }
        });
    }

    /// <summary>Unloads the model (e.g. before its pack is deleted).</summary>
    public void Unload()
    {
        Stop();
        lock (_modelGate)
        {
            DisposeModel();
            _model = null;
            _modelKey = null;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        WaveOutEvent? output;
        lock (_gate)
        {
            cts = _current;
            output = _out;
            _current = null;
            _generation++;
            _out = null;
            _queue = null;
        }

        if (cts is null && output is null)
            return;
        cts?.Cancel();
        try { output?.Stop(); } catch { /* already stopped */ }
        Set(ReaderState.Idle);
    }

    public void TogglePause()
    {
        WaveOutEvent? output;
        lock (_gate)
            output = _out;
        if (output is null)
            return;
        if (output.PlaybackState == PlaybackState.Playing)
        {
            output.Pause();
            Set(ReaderState.Paused);
        }
        else if (output.PlaybackState == PlaybackState.Paused)
        {
            output.Play();
            Set(ReaderState.Speaking);
        }
    }

    /// <summary>Writes the voice reading <paramref name="text"/> to a WAV file (self-test and "Save as audio").</summary>
    public TimeSpan SaveWav(string text, VoiceInfo voice, float speed, string path)
    {
        text = TextPrep.Clean(text);
        var samples = new List<float>();
        var rate = 22050;
        using (var synth = CreateSynth(voice))
            synth.Speak(text, speed, (s, r) =>
            {
                rate = r;
                samples.AddRange(s);
                return true;
            }, CancellationToken.None);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
        return TimeSpan.FromSeconds(samples.Count / (double)rate);
    }

    /// <summary>
    /// The whole audio for <paramref name="text"/> (for saving to a file), made a piece at a time so it can report
    /// progress (0…1) and be cancelled. Runs on the caller's thread; it can run while something is being read aloud.
    /// </summary>
    public (float[] Samples, int Rate) Render(string text, VoiceInfo voice, float speed, Action<double>? progress, CancellationToken ct)
    {
        text = TextPrep.Clean(text);
        var pieces = TextPrep.Pieces(text, 400).ToList();
        var samples = new List<float>();
        var rate = 22050;
        for (var i = 0; i < pieces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // A fresh synth per piece: the shared model may be swapped (voice changed) between pieces.
            using (var synth = CreateSynth(voice))
                synth.Speak(pieces[i], speed, (s, r) =>
                {
                    rate = r;
                    samples.AddRange(s);
                    return !ct.IsCancellationRequested;
                }, ct);
            progress?.Invoke((i + 1) / (double)pieces.Count);
        }

        ct.ThrowIfCancellationRequested();
        return (samples.ToArray(), rate);
    }

    private void Set(ReaderState s)
    {
        if (State == s)
            return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        Unload();
    }

    /// <summary>
    /// Audio waiting to be played. Plays silence while the next sentence is still being made; ends once completed and
    /// drained. Word markers fire when their sample is handed to the sound device, delayed by the device latency so they
    /// line up with what is heard.
    /// </summary>
    private sealed class ChunkQueue(int rate, Reader owner) : ISampleProvider
    {
        private readonly ConcurrentQueue<(float[] Samples, List<(int Offset, Action Fire)>? Markers)> _chunks = new();
        private float[]? _chunk;
        private List<(int Offset, Action Fire)>? _markers;
        private int _nextMarker;
        private int _pos;
        private volatile bool _complete;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public void Add(float[] samples, List<(int Offset, Action Fire)>? markers = null) => _chunks.Enqueue((samples, markers));

        public void Complete() => _complete = true;

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            var volume = Math.Clamp(owner.Volume, 0f, 1f);
            while (written < count)
            {
                if (_chunk is null || _pos >= _chunk.Length)
                {
                    if (!_chunks.TryDequeue(out var next))
                    {
                        _chunk = null;
                        break;
                    }

                    (_chunk, _markers) = next;
                    _nextMarker = 0;
                    _pos = 0;
                }

                var n = Math.Min(count - written, _chunk.Length - _pos);
                for (var i = 0; i < n; i++)
                    buffer[offset + written + i] = _chunk[_pos + i] * volume;
                if (_markers is not null)
                {
                    while (_nextMarker < _markers.Count && _markers[_nextMarker].Offset < _pos + n)
                    {
                        var fire = _markers[_nextMarker].Fire;
                        var lead = Math.Max(0, _markers[_nextMarker].Offset - _pos) * 1000 / rate;
                        _ = Task.Delay(LatencyMs + lead).ContinueWith(_ =>
                        {
                            try { fire(); } catch { /* a highlighter that went away */ }
                        }, TaskScheduler.Default);
                        _nextMarker++;
                    }
                }

                _pos += n;
                written += n;
            }

            if (written < count && !_complete)
            {
                // Still making the next sentence: keep the device running with silence.
                Array.Clear(buffer, offset + written, count - written);
                return count;
            }

            return written;
        }
    }
}
