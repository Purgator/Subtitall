using Subtitall.Core;

namespace Subtitall.Tests;

public class MediaGrouperTests
{
    private static readonly TvShow AttackOnTitan = new(1, "Attack on Titan", "2013-04-07", "NHK", null);
    private static readonly TvShow StrangerThings = new(2, "Stranger Things", "2016-07-15", "Netflix", null);

    private static void Resolve(IEnumerable<MediaItem> items, TvShow show)
    {
        foreach (var item in items)
            item.Detection = new ShowDetection(item.Parsed.Title, DetectionStatus.Resolved, new ResolvedShow(show, [], []), [show]);
    }

    private static List<MediaItem> Add(LibraryService library, TempDir dir, params string[] relativePaths) =>
        [.. library.AddPaths(relativePaths.Select(p => dir.CreateFile(p)))];

    [Fact]
    public void Two_seasons_of_a_show_a_second_show_and_a_movie_series_make_three_groups()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var snk = Add(library, dir,
            "snk/[Grp] Shingeki no Kyojin - 01.mkv",
            "snk/[Grp] Shingeki no Kyojin S2 - 01.mkv",
            "snk/Attack.on.Titan.S02E02.1080p.mkv");
        var stranger = Add(library, dir, "st/Stranger.Things.S01E01.720p.WEB.mkv", "st/Stranger.Things.S01E02.720p.WEB.mkv");
        Add(library, dir,
            "hp/Harry.Potter.and.the.Philosophers.Stone.2001.1080p.BluRay.mkv",
            "hp/Harry.Potter.and.the.Chamber.of.Secrets.2002.1080p.BluRay.mkv");
        Resolve(snk, AttackOnTitan);
        Resolve(stranger, StrangerThings);

        var groups = MediaGrouper.Group(library.GetAll());

        Assert.Equal(3, groups.Count);
        var titan = Assert.Single(groups, g => g.Name == "Attack on Titan");
        Assert.Equal(MediaGroupKind.Show, titan.Kind);
        Assert.Equal(3, titan.Items.Count); // Romaji and English names of the show end up together.
        var things = Assert.Single(groups, g => g.Name == "Stranger Things");
        Assert.Equal(2, things.Items.Count);
        var potter = Assert.Single(groups, g => g.Kind == MediaGroupKind.MovieSeries);
        Assert.Equal("Harry Potter", potter.Name);
        Assert.Equal(2, potter.Items.Count);
        Assert.Empty(potter.Blocks);
    }

    [Fact]
    public void A_show_is_split_into_one_block_per_season_of_files()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var items = Add(library, dir,
            "Season 2/[Grp] Cyberpunk Edgerunners S2 - 01.mkv",
            "Season 2/[Grp] Cyberpunk Edgerunners S2 - 02.mkv",
            "Season 1/[Grp] Cyberpunk Edgerunners - 01.mkv",
            "Season 1/[Grp] Cyberpunk Edgerunners - 02.mkv");
        Resolve(items, new TvShow(3, "Cyberpunk: Edgerunners", "2022-09-13", "Netflix", null));
        foreach (var item in items)
            item.Guess = new EpisodeGuess(item.Parsed.SeasonHint ?? 1, item.Parsed.AbsoluteEpisode!.Value, null, GuessConfidence.High, "test");

        var group = Assert.Single(MediaGrouper.Group(library.GetAll()));

        Assert.Equal(2, group.Blocks.Count);
        Assert.All(group.Blocks, b => Assert.Equal(2, b.Items.Count));
        // Ordered by season, not by the order the files were added in.
        Assert.All(group.Blocks[0].Items, i => Assert.Equal(1, i.Effective.Season));
        Assert.All(group.Blocks[1].Items, i => Assert.Equal(2, i.Effective.Season));
    }

    [Fact]
    public void Same_named_files_in_two_folders_are_two_blocks()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var items = Add(library, dir, "Season 1/Show - 01.mkv", "Season 2/Show - 01.mkv");
        Resolve(items, StrangerThings);

        var group = Assert.Single(MediaGrouper.Group(library.GetAll()));
        Assert.Equal(2, group.Blocks.Count);
    }

    [Fact]
    public void Episodes_without_a_detected_show_are_grouped_by_title()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        Add(library, dir, "Meridian.S01E01.mkv", "Meridian.S01E02.mkv", "Other.Show.S01E01.mkv");

        var groups = MediaGrouper.Group(library.GetAll());

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(MediaGroupKind.Title, g.Kind));
        Assert.Equal(2, groups[0].Items.Count);
    }

    [Theory]
    [InlineData("Iron.Man.2008.mkv", "Iron.Man.2.2010.mkv", "Iron Man")]
    [InlineData("Alien.1979.mkv", "Alien.3.1992.mkv", "Alien")]
    [InlineData("The.Matrix.1999.mkv", "The.Matrix.Reloaded.2003.mkv", "Matrix")]
    [InlineData("The.Lord.of.the.Rings.The.Two.Towers.2002.mkv", "The.Lord.of.the.Rings.The.Return.of.the.King.2003.mkv", "Lord of the Rings")]
    public void Movies_of_a_series_are_grouped(string first, string second, string name)
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        Add(library, dir, first, second);

        var group = Assert.Single(MediaGrouper.Group(library.GetAll()));
        Assert.Equal(MediaGroupKind.MovieSeries, group.Kind);
        Assert.Equal(name, group.Name);
    }

    [Theory]
    [InlineData("Star.Wars.1977.mkv", "Star.Trek.2009.mkv")]
    [InlineData("The.Last.Samurai.2003.mkv", "The.Last.Emperor.1987.mkv")]
    [InlineData("Dawn.of.the.Dead.2004.mkv", "Dawn.of.the.Planet.of.the.Apes.2014.mkv")]
    [InlineData("Inception.2010.mkv", "Interstellar.2014.mkv")]
    public void Unrelated_movies_share_the_last_group(string first, string second)
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var show = Add(library, dir, "Stranger.Things.S01E01.mkv");
        Resolve(show, StrangerThings);
        Add(library, dir, first, second);

        var groups = MediaGrouper.Group(library.GetAll());

        Assert.Equal(2, groups.Count);
        Assert.Equal(MediaGroupKind.Other, groups[^1].Kind);
        Assert.Equal(MediaGrouper.OtherKey, groups[^1].Key);
        Assert.Equal(2, groups[^1].Items.Count);
    }
}
