using System.Text;
using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>Where the name a file was identified from came from.</summary>
public enum NameSource
{
    /// <summary>The filename as it is on disk.</summary>
    FileName,

    /// <summary>The filename was Base64 — it was decoded first.</summary>
    DecodedFileName,

    /// <summary>The title stored inside the video container.</summary>
    EmbeddedTitle,

    /// <summary>A lookup of the file's hash (size + content checksum) on OpenSubtitles.</summary>
    FileHash,

    /// <summary>The user said which show and episode this is.</summary>
    User,
}

/// <summary>What a file was read as, and from what.</summary>
public sealed record MediaReading(ParsedMedia Parsed, NameSource Source, string? Name);

/// <summary>
/// Works out what a video file is from what the file itself offers, without the network: its
/// name, the name hidden in a Base64 filename (some download bots encode it), and the title
/// stored in the container when the filename says nothing usable.
/// </summary>
public static class MediaNameReader
{
    public static MediaReading Read(string path)
    {
        var stem = FilenameParser.IsVideoFile(path)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path);

        var reading = DecodeBase64Name(stem) is { } decoded
            ? new MediaReading(FilenameParser.Parse(decoded), NameSource.DecodedFileName, decoded)
            : new MediaReading(FilenameParser.Parse(path), NameSource.FileName, null);
        if (IsUsable(reading.Parsed))
            return reading;

        if (MatroskaTitle.Read(path) is { } embedded)
        {
            var title = AsFileName(embedded);
            var parsed = FilenameParser.Parse(title);
            if (IsUsable(parsed))
                return new MediaReading(parsed, NameSource.EmbeddedTitle, title);
        }
        return reading;
    }

    /// <summary>A name that pins the file down: an episode, or a title with its year (a movie).</summary>
    public static bool IsUsable(ParsedMedia parsed) => parsed.IsEpisode || parsed.Year.HasValue;

    /// <summary>
    /// The text behind a Base64 filename, or null when the name is not Base64. Only names that
    /// decode to clean printable text count, so ordinary one-word names are left alone.
    /// </summary>
    public static string? DecodeBase64Name(string stem)
    {
        stem = stem.Trim();
        if (stem.Length < 16 || !Regex.IsMatch(stem, @"^[A-Za-z0-9+/_\-]+={0,2}$"))
            return null;

        // URL-safe alphabet and stripped padding are both seen in the wild.
        var base64 = stem.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 == 1)
            return null;
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        var bytes = new byte[base64.Length / 4 * 3];
        if (!Convert.TryFromBase64String(base64, bytes, out var written))
            return null;

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, written);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (text.Any(char.IsControl) || text.Count(char.IsLetter) < 4)
            return null;
        text = AsFileName(text);
        return text.Length > 0 ? text : null;
    }

    /// <summary>Free text made safe to parse as a filename (path separators would cut it short).</summary>
    private static string AsFileName(string text) =>
        Regex.Replace(text, @"[\\/:]+", " ").Trim();
}
