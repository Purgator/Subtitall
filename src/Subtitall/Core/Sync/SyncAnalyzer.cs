using System.Globalization;

namespace Subtitall.Core.Sync;

public enum SyncVerdict
{
    /// <summary>Follows the speech within a fraction of a second, start to end.</summary>
    InSync,
    /// <summary>Right all along, but every subtitle is early or late by the same time.</summary>
    Shift,
    /// <summary>The offset grows or shrinks along the video: another frame rate. Shift and stretch fixes it.</summary>
    Rescale,
    /// <summary>No single shift fits: another cut of the video (an intro, ads, a recap…).</summary>
    DifferentCut,
    /// <summary>Too little speech, or no relation between this subtitle and this audio.</summary>
    Unclear,
    /// <summary>Nothing to compare: the file could not be read, or holds (almost) no dialogue.</summary>
    Unreadable,
}

/// <summary>How to move a subtitle: <c>new time = Scale × time + Offset</c>. <see cref="Rate"/> names the frame-rate change when it is one.</summary>
public sealed record SyncFix(double Scale, double Offset, string? Rate);

/// <summary>
/// One stretch of the video compared on its own. <see cref="Shift"/> is the shift the subtitle needs
/// there (positive = it appears too early). <see cref="Valid"/>: a clear match was found for it.
/// <see cref="Outlier"/>: that match does not agree with the rest of the file.
/// </summary>
public sealed record SyncSegment(double Time, double Shift, double Score, double Dominance, bool Valid, bool Outlier);

public sealed record SyncAnalysis
{
    public required SyncVerdict Verdict { get; init; }

    /// <summary>"high", "medium", "low" — or empty when there is no measure.</summary>
    public string Confidence { get; init; } = "";

    /// <summary>What to do to the file; null when it needs nothing (or nothing can be done).</summary>
    public SyncFix? Fix { get; init; }

    /// <summary>Seconds the subtitles are off at the start of the video, and at its end (positive = too early).</summary>
    public double StartError { get; init; }
    public double EndError { get; init; }

    /// <summary>How well the subtitle follows the speech as it is, and once fixed (0–1; typical good matches are 0.3–0.6).</summary>
    public double CurrentScore { get; init; }
    public double FixedScore { get; init; }

    /// <summary>Share of the time a subtitle is displayed during which somebody speaks, as it is and once fixed.</summary>
    public double CurrentCoverage { get; init; }
    public double FixedCoverage { get; init; }

    /// <summary>How many times better the best alignment matches than the best one more than a second away from it.</summary>
    public double Dominance { get; init; }

    public int SpeechCues { get; init; }
    public IReadOnlyList<SyncSegment> Segments { get; init; } = [];

    /// <summary>Used to put candidates in order: the match once fixed, discounted for what it takes to get there.</summary>
    public double RankScore { get; init; }

    public string Headline { get; init; } = "";
    public string Advice { get; init; } = "";
}

/// <summary>
/// Compares when subtitles are shown with when somebody speaks (see <see cref="SpeechDetector"/>) and finds
/// how the subtitle has to move to follow the speech: first one shift for the whole file (an FFT
/// cross-correlation of both signals), then — video by video stretch — how that shift changes along
/// the video, to tell a plain shift from a frame-rate mismatch or a different cut.
/// </summary>
public sealed class SyncAnalyzer
{
    private const double Frame = FrameEnergy.FrameSeconds;

    /// <summary>Within this, nobody notices: the subtitle counts as in sync.</summary>
    public const double InSyncTolerance = 0.3;
    /// <summary>A drift smaller than this over the whole video is not worth a stretch.</summary>
    public const double MinCorrectableDrift = 0.4;

    private const double SegmentSeconds = 360;
    private const double LocalReachSeconds = 10;
    /// <summary>Where to look for a segment that has no match close by (a different cut moves things by tens of seconds).</summary>
    private const double WideReachSeconds = 60;
    /// <summary>A match is clear from this score on, and when it beats the best alternative by this factor.</summary>
    private const double MinMatchScore = 0.12;
    private const double MinDominance = 1.25;
    /// <summary>How far the cue starts may move the block match when they line up with where speech starts.</summary>
    private const double RefineReachSeconds = 1.5;
    /// <summary>A rival alignment counts only when it is at least this far from the best one: a sentence matches nearly as well a second off.</summary>
    private const double ApartSeconds = 8;
    /// <summary>Segments agree with a model when they are this close to it (seconds).</summary>
    private const double AgreeSeconds = 0.6;

    private static readonly (double Scale, string Label)[] Rates = BuildRates();

    private readonly float[] _speech;
    private readonly int _n;
    private readonly int _maxLag;
    private readonly Fft _fft;
    private readonly double[] _aRe;
    private readonly double[] _aIm;
    private readonly double _aNorm;
    private readonly double[] _prefix; // prefix sums of the speech (it is 0/1, so also of its squares)
    private readonly float[] _onsets;  // where speech starts, blurred over a few frames

    public SyncAnalyzer(float[] speech, double maxOffsetSeconds = 120)
    {
        _speech = speech;
        _n = speech.Length;
        DurationSeconds = _n * Frame;
        _maxLag = Math.Max(1, Math.Min((int)(maxOffsetSeconds / Frame), _n / 2));
        _prefix = new double[_n + 1];
        for (var i = 0; i < _n; i++)
            _prefix[i + 1] = _prefix[i] + speech[i];
        SpeechRatio = _n == 0 ? 0 : _prefix[_n] / _n;
        _onsets = BlurOnsets(speech);

        if (_n < 16)
        {
            _fft = new Fft(2);
            _aRe = _aIm = [];
            return;
        }
        var size = Fft.NextPowerOfTwo(_n + _maxLag + 1);
        _fft = new Fft(size);
        _aRe = new double[size];
        _aIm = new double[size];
        var mean = SpeechRatio;
        for (var i = 0; i < _n; i++)
        {
            var v = speech[i] - mean;
            _aRe[i] = v;
            _aNorm += v * v;
        }
        _aNorm = Math.Sqrt(_aNorm);
        _fft.Transform(_aRe, _aIm, inverse: false);
    }

    private static float[] BlurOnsets(float[] speech)
    {
        const int radius = 18; // three standard deviations of 0.12 s
        var kernel = new float[2 * radius + 1];
        for (var k = 0; k < kernel.Length; k++)
            kernel[k] = (float)Math.Exp(-Math.Pow(k - radius, 2) / (2 * 6 * 6));
        var onsets = new float[speech.Length];
        for (var i = 1; i < speech.Length; i++)
        {
            if (speech[i] < 0.5f || speech[i - 1] >= 0.5f)
                continue;
            for (var k = 0; k < kernel.Length; k++)
            {
                var at = i - radius + k;
                if (at >= 0 && at < onsets.Length)
                    onsets[at] += kernel[k];
            }
        }
        return onsets;
    }

    public double DurationSeconds { get; }

    /// <summary>The share of the audio where somebody speaks.</summary>
    public double SpeechRatio { get; }

    // ---- Public entry --------------------------------------------------------------------------

    public SyncAnalysis Analyze(IReadOnlyList<SubtitleCue> cues)
    {
        var spoken = cues.Where(c => c.IsSpeech && c.Duration <= 30).ToList();
        if (spoken.Count < 10 || spoken.Sum(c => c.Duration) < 30)
            return Empty(SyncVerdict.Unreadable, spoken.Count, "Too few subtitles to compare with the audio");
        if (_n < 3000 || _aNorm < 1e-6 || SpeechRatio < 0.01)
            return Empty(SyncVerdict.Unclear, spoken.Count, "No speech could be heard in the audio");

        var cache = new Dictionary<double, Peak>();
        var one = Evaluate(spoken, 1, cache);
        var oneFinal = Finalize(spoken, one, cache);
        var best = (Trial: one, Final: oneFinal);

        if (!IsSolid(one))
        {
            // The subtitle may be timed for another frame rate: try the usual ones, keep a stretch only
            // when it clearly explains the speech better.
            var ratios = Rates
                .Select(r => (r.Scale, Peak: PeakFor(spoken, r.Scale, cache)))
                .OrderByDescending(r => r.Peak.Score)
                .Take(2);
            foreach (var (scale, _) in ratios)
            {
                var trial = Evaluate(spoken, scale, cache);
                var final = Finalize(spoken, trial, cache);
                if (final.Peak.Score > best.Final.Peak.Score * 1.1 + 0.01)
                    best = (trial, final);
            }
        }
        return Conclude(spoken, best.Trial, best.Final, cache);
    }

    // ---- Verdict -------------------------------------------------------------------------------

    private SyncAnalysis Conclude(List<SubtitleCue> spoken, Trial trial, Final final, Dictionary<double, Peak> cache)
    {
        var valid = trial.Segments.Count(s => s.Valid);
        var outliers = trial.Segments.Count(s => s.Valid && !s.Inlier);
        var segments = trial.Segments.Select(s => new SyncSegment(s.Time, s.Shift, s.Score, s.Dominance, s.Valid, s.Valid && !s.Inlier)).ToList();
        var peak = final.Peak;

        // The block match says roughly where the subtitle goes (cues usually outlast the speech, which
        // leaves it a second of play); the cue starts, which authors time to the first word, say exactly.
        var (aligned, sharp) = AlignStarts(spoken.Select(c => c.Start * final.Scale / Frame).ToList(), peak.Lag / Frame, (int)(RefineReachSeconds / Frame));
        var lag = sharp ? aligned * Frame : peak.Lag;

        var currentScore = PeakFor(spoken, 1, cache).ScoreAtZero;
        var currentCoverage = Coverage(BuildMask(spoken, 1, 0));
        var fixedCoverage = Coverage(BuildMask(spoken, final.Scale, lag));
        var confidence = peak.Dominance >= 1.8 && valid >= 3 ? "high" : peak.Dominance >= 1.3 ? "medium" : "low";

        SyncAnalysis Make(SyncVerdict verdict, SyncFix? fix, double startError, double endError, string headline, string advice) => new()
        {
            Verdict = verdict,
            Confidence = confidence,
            Fix = fix,
            StartError = startError,
            EndError = endError,
            CurrentScore = currentScore,
            FixedScore = peak.Score,
            CurrentCoverage = currentCoverage,
            FixedCoverage = fixedCoverage,
            Dominance = peak.Dominance,
            SpeechCues = spoken.Count,
            Segments = segments,
            RankScore = RankScore(verdict, peak.Score, confidence),
            Headline = headline,
            Advice = advice,
        };

        // A match this weak is what unrelated subtitles score by chance.
        if (peak.Score < 0.10 || (peak.Dominance < 1.3 && peak.Score < 0.20) || valid < 2)
            return Make(SyncVerdict.Unclear, null, 0, 0,
                "Can't tell: too little speech, or this subtitle does not match this audio", "");

        if (valid >= 3 && outliers >= 2 && outliers >= 0.25 * valid)
            return Make(SyncVerdict.DifferentCut, null, lag, lag,
                "Does not follow the video with one shift: probably made for another cut", "Look for another subtitle");

        var scale = final.Scale;
        var offset = Math.Round(lag, 2);
        var start = offset;
        var end = (scale - 1) * DurationSeconds + offset;
        if (Math.Max(Math.Abs(start), Math.Abs(end)) <= InSyncTolerance)
            return Make(SyncVerdict.InSync, null, start, end, "In sync", "");

        if (scale == 1)
            return Make(SyncVerdict.Shift, new SyncFix(1, offset, null), start, end,
                offset > 0 ? $"Appears {Span(offset)} too early" : $"Appears {Span(offset)} too late",
                offset > 0 ? $"Delay it by {Span(offset)}" : $"Bring it forward by {Span(offset)}");

        var rate = final.Rate;
        return Make(SyncVerdict.Rescale, new SyncFix(Math.Round(scale, 7), offset, rate), start, end,
            $"Drifts: {Direction(start)} at the start, {Direction(end)} at the end" + (rate is null ? "" : $" — looks like a {rate} mismatch"),
            $"Stretch it ×{scale.ToString("0.0000", CultureInfo.InvariantCulture)} and shift it by {offset.ToString("+0.00;-0.00;0", CultureInfo.InvariantCulture)} s");
    }

    private static double RankScore(SyncVerdict verdict, double score, string confidence)
    {
        var verdictFactor = verdict switch
        {
            SyncVerdict.InSync => 1.0,
            SyncVerdict.Shift => 0.97,
            SyncVerdict.Rescale => 0.93,
            SyncVerdict.DifferentCut => 0.5,
            SyncVerdict.Unclear => 0.2,
            _ => 0.0,
        };
        var confidenceFactor = confidence switch { "high" => 1.0, "medium" => 0.9, _ => 0.75 };
        return score * verdictFactor * confidenceFactor;
    }

    private static SyncAnalysis Empty(SyncVerdict verdict, int speechCues, string headline) => new()
    {
        Verdict = verdict,
        SpeechCues = speechCues,
        Headline = headline,
    };

    private static string Span(double seconds)
    {
        var s = Math.Abs(seconds);
        if (s < 1)
            return $"{s.ToString("0.00", CultureInfo.InvariantCulture)} s";
        if (s < 10)
            return $"{s.ToString("0.0", CultureInfo.InvariantCulture)} s";
        if (s < 60)
            return $"{Math.Round(s).ToString(CultureInfo.InvariantCulture)} s";
        var minutes = (int)(s / 60);
        return $"{minutes} min {Math.Round(s - minutes * 60).ToString(CultureInfo.InvariantCulture)} s";
    }

    private static string Direction(double error) =>
        Math.Abs(error) <= InSyncTolerance ? "in sync" : $"{Span(error)} {(error > 0 ? "early" : "late")}";

    // ---- Trials --------------------------------------------------------------------------------

    private sealed class Seg
    {
        public double Time;
        public double D;       // the offset left over once the trial's own transform is applied
        public double Shift;   // the shift the original subtitle needs here
        public double Score;
        public double Dominance;
        public bool Contentful; // enough subtitle time to compare
        public bool Valid;
        public bool Inlier;
    }

    private sealed record Model(double Slope, double Intercept, bool Linear);

    private sealed record Trial(double Scale, Peak Global, Seg[] Segments, Model Model);

    private sealed record Final(double Scale, string? Rate, Peak Peak);

    private Trial Evaluate(List<SubtitleCue> cues, double scale, Dictionary<double, Peak> cache)
    {
        var global = PeakFor(cues, scale, cache);
        var segments = Measure(cues, scale, global.Lag);
        return new Trial(scale, global, segments, Fit(segments));
    }

    // One shift for the whole file already explains it: no need to look for a stretch.
    private static bool IsSolid(Trial trial)
    {
        var valid = trial.Segments.Count(s => s.Valid);
        var outliers = trial.Segments.Count(s => s.Valid && !s.Inlier);
        return valid >= 3 && outliers == 0 && !trial.Model.Linear && trial.Global.Score >= 0.2 && trial.Global.Dominance >= 1.3;
    }

    // The transform the measures say, snapped to "no stretch" or a usual frame-rate change when close
    // to one, and the shift that goes with it.
    private Final Finalize(List<SubtitleCue> cues, Trial trial, Dictionary<double, Peak> cache)
    {
        var scale = trial.Model.Linear ? trial.Scale / (1 - trial.Model.Slope) : trial.Scale;
        string? rate = null;
        if (Math.Abs(scale - 1) * DurationSeconds < MinCorrectableDrift)
        {
            scale = 1;
        }
        else
        {
            foreach (var known in Rates)
            {
                if (Math.Abs(scale - known.Scale) <= 0.0004)
                {
                    (scale, rate) = (known.Scale, known.Label);
                    break;
                }
            }
        }
        return new Final(scale, rate, PeakFor(cues, scale, cache));
    }

    // ---- Global alignment ----------------------------------------------------------------------

    private readonly record struct Peak(double Lag, double Score, double Dominance, double ScoreAtZero);

    private Peak PeakFor(List<SubtitleCue> cues, double scale, Dictionary<double, Peak> cache)
    {
        if (!cache.TryGetValue(scale, out var peak))
            cache[scale] = peak = Correlate(BuildMask(cues, scale, 0));
        return peak;
    }

    /// <summary>1 on the frames where a subtitle shows, once its times are moved to <c>scale × t + offset</c>.</summary>
    private float[] BuildMask(List<SubtitleCue> cues, double scale, double offset)
    {
        var mask = new float[_n];
        foreach (var cue in cues)
        {
            var from = (int)Math.Round((cue.Start * scale + offset) / Frame);
            var to = (int)Math.Round((cue.End * scale + offset) / Frame);
            from = Math.Max(0, from);
            to = Math.Min(_n, to);
            for (var i = from; i < to; i++)
                mask[i] = 1;
        }
        return mask;
    }

    private double Coverage(float[] mask)
    {
        double shown = 0, spoken = 0;
        for (var i = 0; i < _n; i++)
        {
            shown += mask[i];
            spoken += mask[i] * _speech[i];
        }
        return shown > 0 ? spoken / shown : 0;
    }

    /// <summary>
    /// Normalised cross-correlation of the speech and the mask for every shift within the allowed
    /// range; the best one, refined between frames, with how much it stands out from all the others.
    /// </summary>
    private Peak Correlate(float[] mask)
    {
        var size = _fft.Size;
        var re = new double[size];
        var im = new double[size];
        double mean = 0;
        for (var i = 0; i < _n; i++)
            mean += mask[i];
        mean /= _n;
        double norm = 0;
        for (var i = 0; i < _n; i++)
        {
            var v = mask[i] - mean;
            re[i] = v;
            norm += v * v;
        }
        norm = Math.Sqrt(norm);
        if (norm < 1e-9 || _aNorm < 1e-9)
            return new Peak(0, 0, 0, 0);

        _fft.Transform(re, im, inverse: false);
        for (var k = 0; k < size; k++)
        {
            // speech × conj(mask): the correlation c[L] = Σ speech[t + L] · mask[t]
            double sr = re[k], si = im[k], ar = _aRe[k], ai = _aIm[k];
            re[k] = ar * sr + ai * si;
            im[k] = ai * sr - ar * si;
        }
        _fft.Transform(re, im, inverse: true);

        var count = 2 * _maxLag + 1;
        var scale = 1.0 / (_aNorm * norm);
        var corr = new double[count];
        var bestIndex = 0;
        for (var lag = -_maxLag; lag <= _maxLag; lag++)
        {
            var c = re[lag >= 0 ? lag : size + lag] * scale;
            corr[lag + _maxLag] = c;
            if (c > corr[bestIndex])
                bestIndex = lag + _maxLag;
        }
        var best = corr[bestIndex];
        return new Peak(
            (bestIndex - _maxLag + Refine(corr, bestIndex)) * Frame,
            best,
            Dominance(corr, bestIndex, best, (int)(ApartSeconds / Frame)),
            corr[_maxLag]);
    }

    /// <summary>The best score divided by the best one more than <paramref name="apart"/> frames from it (the lookalike at another offset).</summary>
    private static double Dominance(double[] scores, int bestIndex, double best, int apart)
    {
        var other = 0.0;
        for (var i = 0; i < scores.Length; i++)
        {
            if (Math.Abs(i - bestIndex) > apart && scores[i] > other)
                other = scores[i];
        }
        return best <= 0 ? 0 : best / Math.Max(other, 0.02);
    }

    // Where the top of the parabola through a peak and its two neighbours is, in frames from the peak.
    private static double Refine(double[] values, int index)
    {
        if (index <= 0 || index >= values.Length - 1)
            return 0;
        double a = values[index - 1], b = values[index], c = values[index + 1];
        var denominator = a - 2 * b + c;
        if (Math.Abs(denominator) < 1e-12)
            return 0;
        return Math.Clamp(0.5 * (a - c) / denominator, -1, 1);
    }

    /// <summary>
    /// Refines a lag (frames) so that the cue starts (frames, already moved by the transform) fall on the
    /// speech onsets. Returns the refined lag, and whether the onsets lined up clearly enough to trust it.
    /// </summary>
    private (double Lag, bool Sharp) AlignStarts(IReadOnlyList<double> starts, double aroundFrames, int radius)
    {
        if (starts.Count < 5)
            return (aroundFrames, false);
        var center = (int)Math.Round(aroundFrames);
        var scores = new double[2 * radius + 1];
        double sum = 0;
        var best = 0;
        for (var k = 0; k < scores.Length; k++)
        {
            var lag = center - radius + k;
            double score = 0;
            foreach (var at in starts)
            {
                var index = (int)Math.Round(at) + lag;
                if (index >= 0 && index < _n)
                    score += _onsets[index];
            }
            scores[k] = score;
            sum += score;
            if (score > scores[best])
                best = k;
        }
        var mean = sum / scores.Length;
        if (scores[best] <= 0 || scores[best] < 1.4 * mean || best == 0 || best == scores.Length - 1)
            return (aroundFrames, false);
        return (center - radius + best + Refine(scores, best), true);
    }

    // ---- Local alignment -----------------------------------------------------------------------

    /// <summary>Compares each stretch of the video (about six minutes) with the subtitle, on its own.</summary>
    private Seg[] Measure(List<SubtitleCue> cues, double scale, double offset)
    {
        var mask = BuildMask(cues, scale, offset);
        var starts = cues.Select(c => (c.Start * scale + offset) / Frame).ToArray();
        // Stay clear of both ends: a stretch there cannot be moved past the start or the end of the audio.
        var margin = (int)((DurationSeconds >= 600 ? WideReachSeconds : LocalReachSeconds) / Frame);
        var usable = _n - 2 * margin;
        var count = Math.Clamp((int)Math.Round(usable * Frame / SegmentSeconds), 3, 12);
        var length = usable / count;
        var segments = new Seg[count];
        Parallel.For(0, count, k =>
        {
            var start = margin + k * length;
            var end = k == count - 1 ? _n - margin : start + length;
            segments[k] = MeasureOne(mask, starts, scale, offset, start, end, (int)(LocalReachSeconds / Frame));
        });

        // A stretch with subtitles but no match close by may match far away: another cut. Looking
        // that wide costs, and is pointless when most stretches miss (the scale is wrong, not the cut).
        var missed = segments.Count(s => s.Contentful && !s.Valid);
        if (missed > 0 && missed <= segments.Count(s => s.Contentful) / 2)
        {
            Parallel.For(0, count, k =>
            {
                if (!segments[k].Contentful || segments[k].Valid)
                    return;
                var start = margin + k * length;
                var end = k == count - 1 ? _n - margin : start + length;
                segments[k] = MeasureOne(mask, starts, scale, offset, start, end, (int)(WideReachSeconds / Frame));
            });
        }
        return segments;
    }

    private Seg MeasureOne(float[] mask, double[] starts, double scale, double offset, int start, int end, int reach)
    {
        var length = end - start;
        var time = (start + end) / 2.0 * Frame;
        var seg = new Seg { Time = time, Shift = double.NaN };

        double shown = 0;
        for (var t = start; t < end; t++)
            shown += mask[t];
        if (shown * Frame < 10)
            return seg;
        seg.Contentful = true;
        if (length <= 2 * reach)
            return seg;

        var mean = shown / length;
        var m = new double[length];
        double norm2 = 0;
        for (var t = 0; t < length; t++)
        {
            m[t] = mask[start + t] - mean;
            norm2 += m[t] * m[t];
        }
        if (norm2 < 1e-6)
            return seg;

        // The location of the best match is the top of the plain cross-covariance (the mask has no
        // mean): dividing each lag by the speech energy of its own window would pull the peak toward
        // quieter windows, by up to a sentence.
        var dots = new double[2 * reach + 1];
        var energies = new double[2 * reach + 1];
        var bestIndex = -1;
        var counted = 0;
        for (var lag = -reach; lag <= reach; lag++)
        {
            var from = start + lag;
            var cell = lag + reach;
            if (from < 0 || from + length > _n)
            {
                dots[cell] = double.NegativeInfinity;
                continue;
            }
            double dot = 0;
            for (var t = 0; t < length; t++)
                dot += _speech[from + t] * m[t];
            var speech = _prefix[from + length] - _prefix[from];
            dots[cell] = dot;
            energies[cell] = speech - speech * speech / length;
            counted++;
            if (bestIndex < 0 || dot > dots[bestIndex])
                bestIndex = cell;
        }
        var reference = _prefix[end] - _prefix[start];
        reference -= reference * reference / length; // the speech energy right where the mask is
        if (counted < 10 || bestIndex < 0 || reference < 1e-6 || energies[bestIndex] < 1e-6)
            return seg;

        var curveScale = 1 / Math.Sqrt(reference * norm2);
        var curve = new double[dots.Length];
        for (var i = 0; i < curve.Length; i++)
            curve[i] = dots[i] * curveScale;
        var neighboursKnown = bestIndex > 0 && bestIndex < curve.Length - 1
            && !double.IsNegativeInfinity(curve[bestIndex - 1]) && !double.IsNegativeInfinity(curve[bestIndex + 1]);
        seg.D = (bestIndex - reach + (neighboursKnown ? Refine(curve, bestIndex) : 0)) * Frame;
        seg.Score = dots[bestIndex] / Math.Sqrt(energies[bestIndex] * norm2);
        var inside = starts.Where(f => f >= start && f < end).ToList();
        if (inside.Count >= 5)
        {
            var (lagFrames, sharp) = AlignStarts(inside, seg.D / Frame, (int)(RefineReachSeconds / Frame));
            if (sharp)
                seg.D = lagFrames * Frame;
        }
        seg.Dominance = Dominance(curve, bestIndex, curve[bestIndex], (int)(Math.Min(ApartSeconds, reach * Frame / 2) / Frame));
        seg.Valid = seg.Score >= MinMatchScore && seg.Dominance >= MinDominance;
        // The subtitle time that lands on this point once the trial's transform and the residual are
        // applied, and so what moving the original there takes.
        var subtitleTime = (time - offset - seg.D) / scale;
        seg.Shift = time - subtitleTime;
        return seg;
    }

    // ---- Models --------------------------------------------------------------------------------

    /// <summary>
    /// Fits how the leftover offset changes with time: a flat line (one shift) unless a sloped one
    /// is worth at least <see cref="MinCorrectableDrift"/> over the video and fits more segments.
    /// Segments that agree with neither are outliers. Marks <see cref="Seg.Inlier"/>.
    /// </summary>
    private Model Fit(Seg[] segments)
    {
        var points = segments.Where(s => s.Valid).ToList();
        if (points.Count == 0)
            return new Model(0, 0, false);

        var flat = Median(points.Select(p => p.D).ToList());
        var flatInliers = points.Where(p => Math.Abs(p.D - flat) <= AgreeSeconds).ToList();
        if (flatInliers.Count > 0)
            flat = Median(flatInliers.Select(p => p.D).ToList());
        flatInliers = points.Where(p => Math.Abs(p.D - flat) <= AgreeSeconds).ToList();

        var chosen = new Model(0, flat, false);
        var inliers = flatInliers;

        if (points.Count >= 3)
        {
            var slopes = new List<double>();
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    var dx = points[j].Time - points[i].Time;
                    if (Math.Abs(dx) > 60)
                        slopes.Add((points[j].D - points[i].D) / dx);
                }
            }
            if (slopes.Count > 0)
            {
                var slope = Median(slopes);
                var intercept = Median(points.Select(p => p.D - slope * p.Time).ToList());
                var near = points.Where(p => Math.Abs(p.D - (slope * p.Time + intercept)) <= AgreeSeconds).ToList();
                if (near.Count >= 3)
                    (slope, intercept) = LeastSquares(near);
                var lineInliers = points.Where(p => Math.Abs(p.D - (slope * p.Time + intercept)) <= AgreeSeconds).ToList();
                // A slope is taken when it is a real drift and explains the segments clearly better than one
                // shift does: with a small drift every segment still agrees with the flat model, but its
                // leftovers line up along the slope.
                var flatError = lineInliers.Count == 0 ? 0 : Math.Sqrt(lineInliers.Average(p => Math.Pow(p.D - flat, 2)));
                var lineError = lineInliers.Count == 0 ? 0 : Math.Sqrt(lineInliers.Average(p => Math.Pow(p.D - (slope * p.Time + intercept), 2)));
                if (lineInliers.Count >= 3
                    && Math.Abs(slope) * DurationSeconds >= MinCorrectableDrift
                    && lineInliers.Count >= flatInliers.Count
                    && (lineInliers.Count > flatInliers.Count || lineError < 0.6 * flatError))
                {
                    chosen = new Model(slope, intercept, true);
                    inliers = lineInliers;
                }
            }
        }

        foreach (var s in segments)
            s.Inlier = s.Valid && inliers.Contains(s);
        return chosen;
    }

    private static (double Slope, double Intercept) LeastSquares(List<Seg> points)
    {
        double n = points.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in points)
        {
            sx += p.Time;
            sy += p.D;
            sxx += p.Time * p.Time;
            sxy += p.Time * p.D;
        }
        var denominator = n * sxx - sx * sx;
        if (Math.Abs(denominator) < 1e-9)
            return (0, sy / n);
        var slope = (n * sxy - sx * sy) / denominator;
        return (slope, (sy - slope * sx) / n);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    // ---- Frame rates ---------------------------------------------------------------------------

    /// <summary>Stretches that come from a subtitle timed for one frame rate played on a video of another.</summary>
    private static (double Scale, string Label)[] BuildRates()
    {
        (double Fps, string Label)[] rates =
        [
            (24000.0 / 1001, "23.976"), (24, "24"), (25, "25"), (30000.0 / 1001, "29.97"), (30, "30"),
        ];
        var result = new List<(double, string)>();
        foreach (var from in rates)
        {
            foreach (var to in rates)
            {
                if (from.Label == to.Label)
                    continue;
                var scale = from.Fps / to.Fps;
                if (result.All(r => Math.Abs(r.Item1 - scale) > 1e-4))
                    result.Add((scale, $"{from.Label} → {to.Label} fps"));
            }
        }
        return [.. result];
    }
}

/// <summary>Chooses, among the subtitles tried for one video, the best one of each language.</summary>
public static class SyncRanking
{
    /// <summary>
    /// The items to recommend: per language (those whose language is unknown share one group), the
    /// one with the highest rank score — and only if it is a real match (in sync, or fixable).
    /// </summary>
    public static HashSet<T> PickBest<T>(IEnumerable<T> items, Func<T, string?> language, Func<T, SyncAnalysis?> analysis) where T : notnull
    {
        var best = new HashSet<T>();
        foreach (var group in items.GroupBy(i => language(i) ?? ""))
        {
            var top = group
                .Select(i => (Item: i, Analysis: analysis(i)))
                .Where(x => x.Analysis is { Verdict: SyncVerdict.InSync or SyncVerdict.Shift or SyncVerdict.Rescale })
                .OrderByDescending(x => x.Analysis!.RankScore)
                .FirstOrDefault();
            if (top.Analysis is not null)
                best.Add(top.Item);
        }
        return best;
    }
}
