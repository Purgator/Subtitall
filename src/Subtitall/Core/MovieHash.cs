using System.Buffers.Binary;

namespace Subtitall.Core;

/// <summary>
/// OpenSubtitles moviehash: file size + sum of the 64-bit little-endian words of the
/// first and last 64 KiB, as a 16-char lowercase hex string.
/// </summary>
public static class MovieHash
{
    private const int ChunkSize = 64 * 1024;

    public static string? Compute(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;
            if (length < ChunkSize * 2)
                return null; // Too small for a meaningful hash (spec requires >= 128 KiB).

            ulong hash = (ulong)length;
            hash = unchecked(hash + SumChunk(stream, 0));
            hash = unchecked(hash + SumChunk(stream, length - ChunkSize));
            return hash.ToString("x16");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ulong SumChunk(FileStream stream, long offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ChunkSize];
        stream.ReadExactly(buffer);

        ulong sum = 0;
        for (var i = 0; i < ChunkSize; i += 8)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)));
        return sum;
    }
}
