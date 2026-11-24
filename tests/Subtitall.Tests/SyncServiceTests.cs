using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Subtitall.Api;
using Subtitall.Core;
using Subtitall.Core.Sync;

namespace Subtitall.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private const string Srt = "1\r\n00:00:10,000 --> 00:00:12,000\r\nHello there\r\n\r\n2\r\n00:00:20,000 --> 00:00:22,500\r\nGeneral Kenobi\r\n";

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private readonly TempDir _dir = new();
    private readonly LibraryService _library = new();
    private readonly SyncService _sync;
    private readonly MediaItem _video;

    public SyncServiceTests()
    {
        var settings = new SettingsService(System.IO.Path.Combine(_dir.Path, "settings"));
        var events = new EventBus();
        var ffmpeg = new FfmpegService(settings, null!, events, NullLogger<FfmpegService>.Instance);
        _sync = new SyncService(_library, settings, ffmpeg, events, NullLogger<SyncService>.Instance, new Lifetime());
        _dir.CreateFile(@"video\Show.S01E01.mkv");
        _video = _library.AddPaths([System.IO.Path.Combine(_dir.Path, "video")]).Single();
    }

    public void Dispose() => _dir.Dispose();

    private static SyncAnalysis Analysis(SyncVerdict verdict, SyncFix? fix) => new()
    {
        Verdict = verdict, Fix = fix, FixedScore = 0.6, RankScore = 0.5, Confidence = "high", Headline = "x",
    };

    // The comparison itself needs ffmpeg and audio; what is tested here is what the service does with its result.
    private void Compared(string path, SyncAnalysis analysis)
    {
        var info = new FileInfo(path);
        _sync.SessionFor(_video.Id).Results[path] = new SyncResult(info.Length, info.LastWriteTimeUtc, 0, analysis, null);
    }

    private static JsonElement Candidates(object? description) =>
        JsonSerializer.SerializeToElement(description, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("candidates");

    [Fact]
    public void Candidates_are_the_subtitles_next_to_the_video_and_the_ones_added()
    {
        _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        _dir.CreateFile(@"video\Show.S01E01.fr.ass", "[Events]");
        _dir.CreateFile(@"video\Other.Show.S01E01.en.srt", Srt); // another video's: not a companion
        var added = _dir.CreateFile(@"elsewhere\release.de.srt", Srt);
        _dir.CreateFile(@"elsewhere\notes.txt", "x");

        Assert.Equal(1, _sync.AddExtras(_video.Id, [System.IO.Path.GetDirectoryName(added)!]));
        Assert.Equal(0, _sync.AddExtras(_video.Id, [added])); // already there

        var list = Candidates(_sync.Describe(_video.Id)).EnumerateArray().ToList();
        Assert.Equal(["release.de.srt", "Show.S01E01.en.srt", "Show.S01E01.fr.ass"],
            list.Select(c => c.GetProperty("name").GetString()!).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var release = list.Single(c => c.GetProperty("name").GetString() == "release.de.srt");
        Assert.Equal("added", release.GetProperty("source").GetString());
        Assert.Equal("de", release.GetProperty("language").GetString());
        Assert.False(release.GetProperty("analyzed").GetBoolean());
    }

    [Fact]
    public void A_fix_rewrites_the_subtitle_keeping_the_original_and_undo_puts_it_back()
    {
        var path = _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        var original = File.ReadAllBytes(path);
        Compared(path, Analysis(SyncVerdict.Shift, new SyncFix(1, 2.5, null)));

        var outcome = _sync.Apply(_video.Id, path.Replace('\\', '/')); // slashes do not matter

        var fixedText = File.ReadAllText(path);
        Assert.Contains("00:00:12,500 --> 00:00:14,500", fixedText);
        Assert.Contains("00:00:22,500 --> 00:00:25,000", fixedText);
        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Contains("kept as .bak", outcome.Message);

        _sync.Undo(_video.Id, path);

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void The_first_backup_survives_a_second_fix()
    {
        var path = _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        var original = File.ReadAllBytes(path);
        Compared(path, Analysis(SyncVerdict.Shift, new SyncFix(1, 1, null)));
        _sync.Apply(_video.Id, path);

        Compared(path, Analysis(SyncVerdict.Shift, new SyncFix(1, 1, null)));
        _sync.Apply(_video.Id, path);

        Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        Assert.Contains("00:00:12,000 --> 00:00:14,000", File.ReadAllText(path));
    }

    [Fact]
    public void An_added_subtitle_is_saved_under_the_videos_name_and_undo_takes_it_back_out()
    {
        var added = _dir.CreateFile(@"elsewhere\release.fr.srt", Srt);
        _sync.AddExtras(_video.Id, [added]);
        Compared(added, Analysis(SyncVerdict.Rescale, new SyncFix(1.0427, -0.5, "25 → 23.976 fps")));

        _sync.Apply(_video.Id, added);

        var saved = System.IO.Path.Combine(_dir.Path, "video", "Show.S01E01.fr.srt");
        Assert.True(File.Exists(saved));
        Assert.Contains("00:00:09,927 --> 00:00:12,012", File.ReadAllText(saved)); // 10 × 1.0427 − 0.5 = 9.927, 12 × 1.0427 − 0.5 = 12.0124
        Assert.Equal(Srt, File.ReadAllText(added)); // the file it came from is not touched
        var sources = Candidates(_sync.Describe(_video.Id)).EnumerateArray().Select(c => c.GetProperty("source").GetString()).ToList();
        Assert.Equal(["folder"], sources); // it is a companion now, no longer an added file

        _sync.Undo(_video.Id, saved);

        Assert.False(File.Exists(saved));
        var again = Candidates(_sync.Describe(_video.Id)).EnumerateArray().Single();
        Assert.Equal("added", again.GetProperty("source").GetString());
    }

    [Fact]
    public void A_subtitle_in_sync_is_copied_untouched_when_it_comes_from_elsewhere()
    {
        var added = _dir.CreateFile(@"elsewhere\release.en.srt", Srt);
        _sync.AddExtras(_video.Id, [added]);
        Compared(added, Analysis(SyncVerdict.InSync, null));

        _sync.Apply(_video.Id, added);

        Assert.Equal(Srt, File.ReadAllText(System.IO.Path.Combine(_dir.Path, "video", "Show.S01E01.en.srt")));
    }

    [Theory]
    [InlineData(SyncVerdict.Unclear)]
    [InlineData(SyncVerdict.DifferentCut)]
    [InlineData(SyncVerdict.Unreadable)]
    public void No_fix_is_applied_where_none_is_safe(SyncVerdict verdict)
    {
        var path = _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        Compared(path, Analysis(verdict, null));

        Assert.Throws<InvalidOperationException>(() => _sync.Apply(_video.Id, path));
        Assert.Equal(Srt, File.ReadAllText(path));
    }

    [Fact]
    public void A_subtitle_already_in_place_and_in_sync_needs_nothing()
    {
        var path = _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        Compared(path, Analysis(SyncVerdict.InSync, null));

        Assert.Throws<InvalidOperationException>(() => _sync.Apply(_video.Id, path));
    }

    [Fact]
    public void A_fix_is_refused_when_the_file_changed_since_it_was_compared()
    {
        var path = _dir.CreateFile(@"video\Show.S01E01.en.srt", Srt);
        Compared(path, Analysis(SyncVerdict.Shift, new SyncFix(1, 2.5, null)));
        File.WriteAllText(path, Srt + "\r\n3\r\n00:00:30,000 --> 00:00:31,000\r\nMore\r\n");

        Assert.Throws<InvalidOperationException>(() => _sync.Apply(_video.Id, path));
    }

    [Fact]
    public void A_fix_keeps_the_utf8_bom_the_file_had()
    {
        var path = System.IO.Path.Combine(_dir.Path, "video", "Show.S01E01.en.srt");
        File.WriteAllBytes(path, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(Srt.Replace("Hello", "Héllo"))]);
        Compared(path, Analysis(SyncVerdict.Shift, new SyncFix(1, 1, null)));

        _sync.Apply(_video.Id, path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Contains("Héllo", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }
}
