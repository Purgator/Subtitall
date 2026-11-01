using Subtitall.Core;

namespace Subtitall.Tests;

public class FilenameParserTests
{
    [Theory]
    [InlineData("The.Mandalorian.S02E03.1080p.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv", "The Mandalorian", 2, 3)]
    [InlineData("Breaking Bad - 1x05 - Gray Matter.mkv", "Breaking Bad", 1, 5)]
    [InlineData("game.of.thrones.s08e06.720p.webrip.x264-tbs.mkv", "game of thrones", 8, 6)]
    [InlineData("Severance.S01E09.The.We.We.Are.2160p.ATVP.WEB-DL.mkv", "Severance", 1, 9)]
    [InlineData("The Office (US) - S05E14 - Stress Relief.avi", "The Office US", 5, 14)]
    [InlineData("show_name_S1E2.mp4", "show name", 1, 2)]
    [InlineData("Dark.Season.1.Episode.3.German.1080p.mkv", "Dark", 1, 3)]
    public void Parses_standard_episodes(string file, string title, int season, int episode)
    {
        var parsed = FilenameParser.Parse(file);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.True(parsed.IsEpisode);
    }

    [Fact]
    public void Parses_multi_episode_files()
    {
        var parsed = FilenameParser.Parse("Show.Name.S01E01E02.720p.mkv");
        Assert.Equal(1, parsed.Season);
        Assert.Equal(1, parsed.Episode);
        Assert.Equal(2, parsed.EndEpisode);

        parsed = FilenameParser.Parse("Show.Name.S01E05-E06.mkv");
        Assert.Equal(5, parsed.Episode);
        Assert.Equal(6, parsed.EndEpisode);

        parsed = FilenameParser.Parse("Show.Name.S01E01-02.mkv");
        Assert.Equal(1, parsed.Episode);
        Assert.Equal(2, parsed.EndEpisode);

        // A resolution after a dash is not a second episode.
        parsed = FilenameParser.Parse("Show.Name.S01E01-720p.mkv");
        Assert.Equal(1, parsed.Episode);
        Assert.Null(parsed.EndEpisode);
    }

    [Fact]
    public void Hyphenated_titles_keep_their_tail()
    {
        var parsed = FilenameParser.Parse("Spider-Man.mkv");
        Assert.Equal("Spider Man", parsed.Title);
        Assert.Null(parsed.ReleaseGroup);
    }

    [Fact]
    public void Split_noise_tokens_are_cut_from_titles()
    {
        Assert.Equal("Movie", FilenameParser.Parse("Movie.Blu-Ray.1080p.mkv").Title);
        Assert.Null(FilenameParser.Parse("1917.2019.2160p.WEB-DL.mkv").ReleaseGroup);
    }

    [Theory]
    [InlineData("[SubsPlease] Frieren - 28 (1080p) [ABC12345].mkv", "Frieren", 28, "SubsPlease")]
    [InlineData("[Erai-raws] One Piece - 1071 [720p].mkv", "One Piece", 1071, "Erai-raws")]
    [InlineData("Attack on Titan - 87.mkv", "Attack on Titan", 87, null)]
    // Bare number followed by noise instead of a bracket.
    [InlineData("[AnimeOut] Attack on Titan The Final Season - 16 BD 1080pp Hi10 FLAC [2636E656][1080pp][SCY][RapidBot].mkv", "Attack on Titan", 16, "AnimeOut")]
    [InlineData("Show.Name.E07.720p.mkv", "Show Name", 7, null)]
    [InlineData("Show Name Episode 7.mkv", "Show Name", 7, null)]
    public void Parses_anime_absolute_numbering(string file, string title, int abs, string? group)
    {
        var parsed = FilenameParser.Parse(file);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(abs, parsed.AbsoluteEpisode);
        Assert.Null(parsed.Season);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.True(parsed.IsEpisode);
    }

    [Theory]
    [InlineData("Inception.2010.1080p.BluRay.x264-SPARKS.mkv", "Inception", 2010)]
    [InlineData("1917.2019.2160p.WEB-DL.mkv", "1917", 2019)]
    [InlineData("Blade.Runner.2049.2017.1080p.mkv", "Blade Runner 2049", 2017)]
    [InlineData("The Matrix (1999).mkv", "The Matrix", 1999)]
    [InlineData("Parasite.2019.KOREAN.1080p.BluRay.H264.AAC-VXT.mp4", "Parasite", 2019)]
    public void Parses_movies(string file, string title, int year)
    {
        var parsed = FilenameParser.Parse(file);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.False(parsed.IsEpisode);
    }

    [Theory]
    [InlineData("[AnimeOut] Attack on Titan The Final Season - 15 BD 1080pp Hi10 FLAC [7E56EAB4][1080pp][SCY][RapidBot].mkv", "Attack on Titan", null, true, 15)]
    [InlineData("Shingeki no Kyojin Season 3 - 05 [1080p].mkv", "Shingeki no Kyojin", 3, false, 5)]
    [InlineData("My Hero Academia 2nd Season - 07.mkv", "My Hero Academia", 2, false, 7)]
    [InlineData("Attack on Titan The Final Season Part 2 - 03.mkv", "Attack on Titan", null, true, 3)]
    [InlineData("Attack on Titan S4 - 03.mkv", "Attack on Titan", 4, false, 3)]
    public void Season_hints_in_the_title_are_split_off(string file, string title, int? hint, bool final, int episode)
    {
        var parsed = FilenameParser.Parse(file);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(hint, parsed.SeasonHint);
        Assert.Equal(final, parsed.FinalSeasonHint);
        Assert.Equal(episode, parsed.AbsoluteEpisode);
        Assert.Null(parsed.Season);
        Assert.True(parsed.IsEpisode);
    }

    [Fact]
    public void Explicit_season_episode_with_v2_marker_and_episode_title()
    {
        var parsed = FilenameParser.Parse("[AnimeOut] Attack on Titan - S04E29 v4 - The Final Chapters Part 1 WEB 1080pp HEVC EAC3 AAC [8A69093D][1080pp][Baws][RapidBot].mkv");
        Assert.Equal("Attack on Titan", parsed.Title);
        Assert.Equal(4, parsed.Season);
        Assert.Equal(29, parsed.Episode);
        Assert.Null(parsed.EndEpisode);
        Assert.Equal("AnimeOut", parsed.ReleaseGroup);
    }

    [Fact]
    public void Episode_marker_next_to_a_year_is_a_movie()
    {
        var parsed = FilenameParser.Parse("Star.Wars.Episode.1.The.Phantom.Menace.1999.1080p.mkv");
        Assert.False(parsed.IsEpisode);
        Assert.Equal(1999, parsed.Year);
    }

    [Fact]
    public void Episode_with_year_in_show_name_keeps_year()
    {
        var parsed = FilenameParser.Parse("Doctor.Who.2005.S04E12.720p.mkv");
        Assert.Equal("Doctor Who", parsed.Title);
        Assert.Equal(2005, parsed.Year);
        Assert.Equal(4, parsed.Season);
        Assert.Equal(12, parsed.Episode);
    }

    [Fact]
    public void Resolution_is_not_mistaken_for_season_episode_or_year()
    {
        var parsed = FilenameParser.Parse("Some.Movie.1920x1080.mkv");
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Year);

        parsed = FilenameParser.Parse("Show.S01E01.2160p.mkv");
        Assert.Null(parsed.Year);
        Assert.Equal(1, parsed.Season);
    }

    [Fact]
    public void Extracts_trailing_release_group()
    {
        var parsed = FilenameParser.Parse("The.Mandalorian.S02E03.1080p.WEB-DL.H.264-FLUX.mkv");
        Assert.Equal("FLUX", parsed.ReleaseGroup);
    }

    [Fact]
    public void Noise_only_title_is_not_emptied()
    {
        var parsed = FilenameParser.Parse("720p.mkv");
        Assert.False(string.IsNullOrWhiteSpace(parsed.Title));
    }

    [Theory]
    [InlineData("movie.mkv", true)]
    [InlineData("movie.MP4", true)]
    [InlineData("movie.srt", false)]
    [InlineData("movie.txt", false)]
    public void Detects_video_files(string file, bool expected)
    {
        Assert.Equal(expected, FilenameParser.IsVideoFile(file));
    }
}
