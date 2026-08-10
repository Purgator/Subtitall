using Subtitall.Core;

namespace Subtitall.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "subtitall-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string CreateFile(string relativePath, string content = "x")
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class LibraryServiceTests
{
    [Fact]
    public void Adds_folder_recursively_and_dedupes()
    {
        using var dir = new TempDir();
        var v1 = dir.CreateFile("Show.S01E01.mkv");
        dir.CreateFile(@"nested\Show.S01E02.mp4");
        dir.CreateFile("notes.txt");
        dir.CreateFile("cover.jpg");

        var library = new LibraryService();
        var added = library.AddPaths([dir.Path]);
        Assert.Equal(2, added.Count);

        // Re-adding the same folder or file adds nothing.
        Assert.Empty(library.AddPaths([dir.Path]));
        Assert.Empty(library.AddPaths([v1]));
        Assert.Equal(2, library.GetAll().Count);
    }

    [Fact]
    public void Skips_small_sample_files()
    {
        using var dir = new TempDir();
        dir.CreateFile("Movie.2020.1080p-sample.mkv");
        dir.CreateFile("Movie.2020.1080p.mkv");

        var library = new LibraryService();
        var added = library.AddPaths([dir.Path]);
        Assert.Single(added);
        Assert.DoesNotContain("sample", added[0].FileName);
    }

    [Fact]
    public void Remove_and_clear_work()
    {
        using var dir = new TempDir();
        var v1 = dir.CreateFile("a.S01E01.mkv");
        var library = new LibraryService();
        var added = library.AddPaths([v1]);

        Assert.True(library.Remove(added[0].Id));
        Assert.Empty(library.GetAll());

        // Removed file can be re-added.
        Assert.Single(library.AddPaths([v1]));
    }

    [Fact]
    public void RefreshSubtitleStatus_detects_existing_subtitles()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("Show.S01E01.mkv");
        dir.CreateFile("Show.S01E01.en.srt");

        var library = new LibraryService();
        var item = library.AddPaths([video])[0];
        library.RefreshSubtitleStatus(["en", "fr"]);

        Assert.True(item.Subtitles["en"]);
        Assert.False(item.Subtitles["fr"]);
    }
}

public class SubtitleNamingTests
{
    [Fact]
    public void Builds_language_suffixed_path()
    {
        var path = SubtitleNaming.GetSubtitlePath(@"C:\videos\movie.mkv", "fr");
        Assert.Equal(@"C:\videos\movie.fr.srt", path);
    }

    [Fact]
    public void Finds_companion_subtitles()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("Show.S01E01.mkv");
        var srt = dir.CreateFile("Show.S01E01.en.srt");
        dir.CreateFile("Show.S01E02.en.srt"); // different episode — not a companion

        var companions = SubtitleNaming.FindCompanionFiles(video).ToList();
        Assert.Equal([srt], companions);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Roundtrips_and_normalizes()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.Path);
        service.Save(new AppSettings { Languages = ["EN", "fr ", "en", ""] });

        var reloaded = new SettingsService(dir.Path);
        Assert.Equal(["en", "fr"], reloaded.Current.Languages);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{not json!");
        var service = new SettingsService(dir.Path);
        Assert.Equal(["en"], service.Current.Languages);
    }
}

public class RenameServiceTests
{
    private static readonly List<TvEpisode> Episodes =
    [
        new(1, 1, "Pilot", "2020-01-01"),
        new(1, 2, "Second: One?", "2020-01-08"),
        new(2, 1, "Return", "2021-01-01"),
    ];

    [Fact]
    public void Proposes_template_names_and_applies_rename_with_companions()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("show.s01e02.720p.mkv");
        var sub = dir.CreateFile("show.s01e02.720p.en.srt");

        var library = new LibraryService();
        var item = library.AddPaths([video])[0];
        var service = new RenameService(library);

        var proposals = service.BuildProposals([item], "My Show", Episodes,
            "{Show} - S{Season}E{Episode} - {Title}");
        Assert.Single(proposals);
        // Invalid chars from the episode title must be stripped.
        Assert.Equal("My Show - S01E02 - Second One.mkv", proposals[0].NewName);
        Assert.True(proposals[0].CanApply);

        var outcomes = service.Apply(proposals);
        Assert.True(outcomes[0].Renamed);
        Assert.True(File.Exists(Path.Combine(dir.Path, "My Show - S01E02 - Second One.mkv")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "My Show - S01E02 - Second One.en.srt")));
        Assert.False(File.Exists(video));
        Assert.False(File.Exists(sub));
        Assert.Equal("My Show - S01E02 - Second One.mkv", item.FileName);
    }

    [Fact]
    public void Maps_absolute_numbering_onto_seasons()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("My Show - 03.mkv"); // 3rd episode overall → S02E01

        var library = new LibraryService();
        var item = library.AddPaths([video])[0];
        var service = new RenameService(library);

        var proposals = service.BuildProposals([item], "My Show", Episodes,
            "{Show} - S{Season}E{Episode} - {Title}");
        Assert.Equal("My Show - S02E01 - Return.mkv", proposals[0].NewName);
    }

    [Fact]
    public void Flags_unknown_episode_and_existing_target()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("show.s09e09.mkv");
        var library = new LibraryService();
        var item = library.AddPaths([video])[0];
        var service = new RenameService(library);

        var proposals = service.BuildProposals([item], "My Show", Episodes,
            "{Show} - S{Season}E{Episode} - {Title}");
        Assert.False(proposals[0].CanApply);
        Assert.Contains("not found", proposals[0].Problem);

        // Existing target file blocks the rename.
        var video2 = dir.CreateFile("show.s01e01.mkv");
        dir.CreateFile("My Show - S01E01 - Pilot.mkv");
        var item2 = library.AddPaths([video2])[0];
        var proposals2 = service.BuildProposals([item2], "My Show", Episodes,
            "{Show} - S{Season}E{Episode} - {Title}");
        Assert.False(proposals2[0].CanApply);
        Assert.Contains("already exists", proposals2[0].Problem);
    }

    [Fact]
    public void Missing_episode_title_leaves_clean_name()
    {
        using var dir = new TempDir();
        var video = dir.CreateFile("show.s01e01.mkv");
        var library = new LibraryService();
        var item = library.AddPaths([video])[0];
        var service = new RenameService(library);

        var episodes = new List<TvEpisode> { new(1, 1, "", null) };
        var proposals = service.BuildProposals([item], "My Show", episodes,
            "{Show} - S{Season}E{Episode} - {Title}");
        Assert.Equal("My Show - S01E01.mkv", proposals[0].NewName);
    }
}
