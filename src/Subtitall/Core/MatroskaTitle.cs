using System.Text;

namespace Subtitall.Core;

/// <summary>
/// Reads the title stored inside a Matroska file (.mkv/.webm: Segment → Info → Title). Release
/// tools often leave the original release name there, which still says what the file is after
/// the file itself got a meaningless name. Only the start of the file is walked — a title that
/// sits behind the video data is not worth the read.
/// </summary>
public static class MatroskaTitle
{
    private const uint EbmlHeader = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint Info = 0x1549A966;
    private const uint Cluster = 0x1F43B675;
    private const uint Title = 0x7BA9;
    private const long MaxInfoSize = 1024 * 1024;

    public static string? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? Read(Stream stream)
    {
        try
        {
            if (ReadId(stream) != EbmlHeader)
                return null;
            Skip(stream, ReadSize(stream));
            if (ReadId(stream) != Segment)
                return null;
            ReadSize(stream);

            // Top-level elements before the video data: SeekHead, Void, Info, Tracks…
            for (var i = 0; i < 64 && stream.Position < stream.Length; i++)
            {
                var id = ReadId(stream);
                var size = ReadSize(stream);
                if (id == Info)
                    return size is >= 0 and <= MaxInfoSize ? ReadTitle(stream, stream.Position + size) : null;
                if (id == Cluster || size < 0)
                    return null;
                Skip(stream, size);
            }
            return null;
        }
        catch (EndOfStreamException)
        {
            return null; // Truncated or not really Matroska.
        }
    }

    private static string? ReadTitle(Stream stream, long end)
    {
        while (stream.Position < end)
        {
            var id = ReadId(stream);
            var size = ReadSize(stream);
            if (size < 0 || stream.Position + size > end)
                return null;
            if (id != Title)
            {
                Skip(stream, size);
                continue;
            }
            var bytes = new byte[size];
            stream.ReadExactly(bytes);
            var title = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            return title.Length > 0 ? title : null;
        }
        return null;
    }

    private static void Skip(Stream stream, long size)
    {
        if (size < 0 || stream.Position + size > stream.Length)
            throw new EndOfStreamException();
        stream.Seek(size, SeekOrigin.Current);
    }

    /// <summary>Element id: 1–4 bytes, the length marker bits stay part of the id.</summary>
    private static uint ReadId(Stream stream)
    {
        var first = ReadByte(stream);
        var length = LengthOf(first, 4);
        uint id = first;
        for (var i = 1; i < length; i++)
            id = (id << 8) | ReadByte(stream);
        return id;
    }

    /// <summary>Element size: 1–8 bytes without the marker bit; -1 for "unknown size".</summary>
    private static long ReadSize(Stream stream)
    {
        var first = ReadByte(stream);
        var length = LengthOf(first, 8);
        long size = first & (0xFF >> length);
        var allOnes = size == (0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            var next = ReadByte(stream);
            allOnes &= next == 0xFF;
            size = (size << 8) | next;
        }
        return allOnes ? -1 : size;
    }

    private static int LengthOf(byte first, int max)
    {
        for (var length = 1; length <= max; length++)
            if ((first & (0x80 >> (length - 1))) != 0)
                return length;
        throw new EndOfStreamException(); // Not a valid EBML length marker.
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value >= 0 ? (byte)value : throw new EndOfStreamException();
    }
}
