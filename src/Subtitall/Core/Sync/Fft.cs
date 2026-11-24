namespace Subtitall.Core.Sync;

/// <summary>An in-place radix-2 FFT for one fixed power-of-two size (tables are built once).</summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("The size must be a power of two", nameof(size));
        _size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            var angle = -2 * Math.PI * i / size;
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }
        var bits = System.Numerics.BitOperations.Log2((uint)size);
        _reverse = new int[size];
        for (var i = 1; i < size; i++)
            _reverse[i] = (_reverse[i >> 1] >> 1) | ((i & 1) << (bits - 1));
    }

    public int Size => _size;

    public static int NextPowerOfTwo(int value) =>
        value <= 2 ? 2 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value);

    /// <summary>Forward transform, or the inverse (scaled by 1/size) when <paramref name="inverse"/> is set.</summary>
    public void Transform(double[] re, double[] im, bool inverse)
    {
        for (var i = 0; i < _size; i++)
        {
            var j = _reverse[i];
            if (j <= i)
                continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }
        for (var length = 2; length <= _size; length <<= 1)
        {
            var half = length / 2;
            var step = _size / length;
            for (var start = 0; start < _size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = inverse ? -_sin[k * step] : _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
        if (inverse)
        {
            var scale = 1.0 / _size;
            for (var i = 0; i < _size; i++)
            {
                re[i] *= scale;
                im[i] *= scale;
            }
        }
    }
}
