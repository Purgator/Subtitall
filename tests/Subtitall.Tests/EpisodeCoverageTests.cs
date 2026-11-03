using Subtitall.Core;

namespace Subtitall.Tests;

public class EpisodeCoverageTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static readonly List<TvEpisode> Episodes =
    [
        new(1, 1, "Pilot", "2020-01-01"),
        new(1, 2, "Two", "2020-01-08"),
        new(1, 3, "Three", "2020-01-15"),
        new(2, 1, "Return", "2021-01-01"),
        new(2, 2, "Undated", null),
        new(2, 3, "Future", "2027-05-01"),
        new(0, 1, "Special", "2020-06-01"), // specials are not counted
    ];

    [Fact]
    public void Reports_missing_aired_episodes_per_season()
    {
        var coverage = EpisodeCoverage.Compute(Episodes,
        [
            new EpisodeCoverage.LinkedFile(1, 1, null, "s01e01.mkv"),
            new EpisodeCoverage.LinkedFile(2, 1, null, "s02e01.mkv"),
        ], Today);

        Assert.Equal(2, coverage.Count);
        var s1 = coverage[0];
        Assert.Equal(1, s1.Season);
        Assert.Equal(3, s1.Aired);
        Assert.Equal(1, s1.Have);
        Assert.Equal(2, s1.Missing);
        Assert.Equal(["Two", "Three"], s1.Episodes.Where(e => e.Aired && !e.Have).Select(e => e.Name));
        Assert.Equal("s01e01.mkv", s1.Episodes[0].FileName);

        var s2 = coverage[1];
        Assert.Equal(1, s2.Have);
        Assert.Equal(1, s2.Missing);   // "Undated" counts as aired and missing
        Assert.Equal(1, s2.Unaired);   // "Future" is not missing, it hasn't aired
        Assert.False(s2.Episodes.Single(e => e.Name == "Future").Aired);
    }

    [Fact]
    public void Multi_episode_files_cover_their_whole_range()
    {
        var coverage = EpisodeCoverage.Compute(Episodes,
            [new EpisodeCoverage.LinkedFile(1, 1, 3, "s01e01-03.mkv")], Today);
        Assert.Equal(0, coverage[0].Missing);
        Assert.All(coverage[0].Episodes, e => Assert.Equal("s01e01-03.mkv", e.FileName));
    }

    [Fact]
    public void Episodes_are_listed_in_viewing_order()
    {
        var shuffled = Episodes.OrderByDescending(e => e.Number).ThenByDescending(e => e.Season).ToList();
        var coverage = EpisodeCoverage.Compute(shuffled, [], Today);
        Assert.Equal([1, 2], coverage.Select(s => s.Season));
        Assert.Equal([1, 2, 3], coverage[0].Episodes.Select(e => e.Number));
    }

    [Fact]
    public void Summary_html_is_flattened()
    {
        Assert.Equal("Hello world. Bye.", ShowInfo.PlainText("<p>Hello <b>world</b>.</p>\n<p>Bye.</p>"));
        Assert.Null(ShowInfo.PlainText(null));
    }
}
