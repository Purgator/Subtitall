namespace Subtitall.Core.Sync;

/// <summary>
/// Turns raw audio (mono, 16-bit little-endian PCM at 8 kHz, as ffmpeg writes it) into one loudness
/// value per 20 ms frame, in dB relative to full scale. Fed in chunks as the audio streams in.
/// </summary>
public sealed class FrameEnergy
{
    public const int SampleRate = 8000;
    public const double FrameSeconds = 0.02;
    public const int FrameSamples = (int)(SampleRate * FrameSeconds);

    private readonly List<float> _frames = [];
    private double _sum;
    private int _count;
    private int _carry = -1; // the first byte of a sample cut by a chunk boundary

    public int Frames => _frames.Count;

    public void Add(ReadOnlySpan<byte> pcm)
    {
        var i = 0;
        if (_carry >= 0 && pcm.Length > 0)
        {
            Sample((short)(_carry | (pcm[0] << 8)));
            _carry = -1;
            i = 1;
        }
        for (; i + 1 < pcm.Length; i += 2)
            Sample((short)(pcm[i] | (pcm[i + 1] << 8)));
        if (i < pcm.Length)
            _carry = pcm[i];
    }

    private void Sample(short value)
    {
        var v = value / 32768.0;
        _sum += v * v;
        if (++_count < FrameSamples)
            return;
        _frames.Add((float)Math.Max(-100, 10 * Math.Log10(_sum / FrameSamples + 1e-10)));
        _sum = 0;
        _count = 0;
    }

    public float[] ToArray() => [.. _frames];
}

/// <summary>
/// Decides, frame by frame, whether somebody is speaking. No model: speech is louder than the
/// background around it (the floor is measured locally, so a quiet scene and a loud one both work)
/// and its loudness keeps moving at the rhythm of syllables, which sustained music and engine noise
/// do not. Short gaps inside a phrase are closed and blips are dropped.
/// </summary>
public static class SpeechDetector
{
    /// <summary>Loudness above the local background, in dB, from which a frame can be speech.</summary>
    private const double MarginDb = 9;
    /// <summary>Below this nothing is speech, whatever the background.</summary>
    private const double AbsoluteFloorDb = -62;
    /// <summary>Spread of the loudness over half a second, in dB, from which it counts as moving.</summary>
    private const double MinSpreadDb = 2.5;
    private const int WindowFrames = 25;       // 0.5 s
    private const int BlockFrames = 500;       // the floor is measured per 10 s…
    private const int BlockReach = 2;          // …over this many blocks on each side
    private const int CloseGapFrames = 15;     // gaps up to 0.3 s inside a phrase are closed
    private const int MinRunFrames = 6;        // speech shorter than 0.12 s is a click

    /// <summary>1 where somebody speaks, 0 elsewhere, one value per frame.</summary>
    public static float[] Detect(float[] frameDb)
    {
        var n = frameDb.Length;
        var speech = new float[n];
        if (n == 0)
            return speech;

        var smooth = Smooth(frameDb);
        var floor = LocalFloor(smooth);
        var spread = LocalSpread(frameDb);

        var flags = new bool[n];
        for (var i = 0; i < n; i++)
            flags[i] = smooth[i] > AbsoluteFloorDb && smooth[i] - floor[i] >= MarginDb && spread[i] >= MinSpreadDb;

        CloseGaps(flags, CloseGapFrames);
        DropShortRuns(flags, MinRunFrames);

        for (var i = 0; i < n; i++)
            speech[i] = flags[i] ? 1f : 0f;
        return speech;
    }

    // Three frames: the jitter of a single frame is not information.
    private static float[] Smooth(float[] db)
    {
        var result = new float[db.Length];
        for (var i = 0; i < db.Length; i++)
        {
            var a = db[Math.Max(0, i - 1)];
            var c = db[Math.Min(db.Length - 1, i + 1)];
            result[i] = (a + db[i] + c) / 3;
        }
        return result;
    }

    // The 10th percentile of the loudness around each 10 s block: what the scene sounds like
    // between the words.
    private static float[] LocalFloor(float[] db)
    {
        var n = db.Length;
        var blocks = (n + BlockFrames - 1) / BlockFrames;
        var perBlock = new float[blocks];
        var buffer = new float[(2 * BlockReach + 1) * BlockFrames];
        for (var b = 0; b < blocks; b++)
        {
            var from = Math.Max(0, (b - BlockReach) * BlockFrames);
            var to = Math.Min(n, (b + BlockReach + 1) * BlockFrames);
            var length = to - from;
            Array.Copy(db, from, buffer, 0, length);
            Array.Sort(buffer, 0, length);
            perBlock[b] = buffer[(int)(length * 0.10)];
        }
        var floor = new float[n];
        for (var i = 0; i < n; i++)
            floor[i] = perBlock[i / BlockFrames];
        return floor;
    }

    // Standard deviation of the loudness over a half second around each frame.
    private static float[] LocalSpread(float[] db)
    {
        var n = db.Length;
        var sum = new double[n + 1];
        var sumSquares = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            sum[i + 1] = sum[i] + db[i];
            sumSquares[i + 1] = sumSquares[i] + (double)db[i] * db[i];
        }
        var spread = new float[n];
        for (var i = 0; i < n; i++)
        {
            var from = Math.Max(0, i - WindowFrames / 2);
            var to = Math.Min(n, i + WindowFrames / 2 + 1);
            var count = to - from;
            var mean = (sum[to] - sum[from]) / count;
            var variance = (sumSquares[to] - sumSquares[from]) / count - mean * mean;
            spread[i] = (float)Math.Sqrt(Math.Max(0, variance));
        }
        return spread;
    }

    private static void CloseGaps(bool[] flags, int maxGap)
    {
        var last = -1; // the last speech frame seen
        for (var i = 0; i < flags.Length; i++)
        {
            if (!flags[i])
                continue;
            if (last >= 0 && i - last - 1 <= maxGap)
                for (var j = last + 1; j < i; j++)
                    flags[j] = true;
            last = i;
        }
    }

    private static void DropShortRuns(bool[] flags, int minRun)
    {
        var i = 0;
        while (i < flags.Length)
        {
            if (!flags[i]) { i++; continue; }
            var end = i;
            while (end < flags.Length && flags[end]) end++;
            if (end - i < minRun)
                for (var j = i; j < end; j++)
                    flags[j] = false;
            i = end;
        }
    }
}
