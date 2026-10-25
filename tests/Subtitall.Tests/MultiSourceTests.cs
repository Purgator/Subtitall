using Microsoft.Extensions.Time.Testing;
using Subtitall.Core;
using Subtitall.Core.Providers;

namespace Subtitall.Tests;

public class LanguagesSubSourceTests
{
    [Theory]
    [InlineData("en", "english")]
    [InlineData("fr", "french")]
    [InlineData("pt-br", "brazilian_portuguese")]
    [InlineData("pt-pt", "portuguese")]
    [InlineData("zh-cn", "chinese_bg_code")]
    [InlineData("zh-tw", "chinese_bg_code")]
    [InlineData("fa", "farsi_persian")]
    [InlineData("no", "norwegian")]
    public void Maps_internal_codes_to_subsource_names(string code, string expected) =>
        Assert.Equal(expected, Languages.ToSubSource(code));

    [Fact]
    public void Every_catalog_language_maps_to_a_plain_name()
    {
        foreach (var (code, _) in Languages.All)
        {
            var mapped = Languages.ToSubSource(code);
            Assert.Matches("^[a-z_]+$", mapped);
        }
    }
}

public class SubtitleValidatorTests
{
    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Accepts_a_normal_srt()
    {
        var srt = "1\n00:00:01,000 --> 00:00:02,000\nHello there\n";
        Assert.True(SubtitleValidator.LooksLikeSubtitle(Bytes(srt), ".srt"));
    }

    [Fact]
    public void Accepts_an_ass_file()
    {
        var ass = "[Script Info]\nTitle: x\n\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hi\n";
        Assert.True(SubtitleValidator.LooksLikeSubtitle(Bytes(ass), ".ass"));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Rate limited</body></html>")]
    [InlineData("<html><head><title>503</title></head></html>")]
    [InlineData("{\"error\":\"quota exceeded, come back tomorrow\"}")]
    public void Rejects_error_pages_saved_as_srt(string body) =>
        Assert.False(SubtitleValidator.LooksLikeSubtitle(Bytes(body), ".srt"));

    [Fact]
    public void Rejects_tiny_or_empty_payloads()
    {
        Assert.False(SubtitleValidator.LooksLikeSubtitle([], ".srt"));
        Assert.False(SubtitleValidator.LooksLikeSubtitle(Bytes("oops"), ".srt"));
    }

    [Fact]
    public void Rejects_srt_without_timestamps() =>
        Assert.False(SubtitleValidator.LooksLikeSubtitle(Bytes(new string('a', 100)), ".srt"));
}

public class ProviderHealthTests
{
    [Fact]
    public void One_failure_does_not_open_the_breaker()
    {
        var health = new ProviderHealth();
        health.ReportFailure("X");
        Assert.True(health.IsAvailable("X"));
    }

    [Fact]
    public void Repeated_failures_trigger_a_cooldown_that_expires()
    {
        var time = new FakeTimeProvider();
        var health = new ProviderHealth(time);
        health.ReportFailure("X");
        health.ReportFailure("X");
        Assert.False(health.IsAvailable("X"));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(health.IsAvailable("X"));
    }

    [Fact]
    public void A_success_closes_the_breaker()
    {
        var time = new FakeTimeProvider();
        var health = new ProviderHealth(time);
        health.ReportFailure("X");
        health.ReportFailure("X");
        health.ReportSuccess("X");
        Assert.True(health.IsAvailable("X"));
    }

    [Fact]
    public void Success_rate_degrades_with_failures_but_never_hits_zero()
    {
        var health = new ProviderHealth();
        Assert.Equal(1.0, health.SuccessRate("fresh"));

        for (var i = 0; i < 8; i++)
            health.ReportFailure("flaky");
        Assert.InRange(health.SuccessRate("flaky"), 0.01, 0.5);

        health.ReportSuccess("solid");
        health.ReportSuccess("solid");
        Assert.InRange(health.SuccessRate("solid"), 0.9, 1.0);
    }
}

public class SubtitleRankingTests
{
    private static MediaItem Item(string fileName) => new()
    {
        Id = "i1",
        Path = Path.Combine(Path.GetTempPath(), fileName),
        Parsed = FilenameParser.Parse(fileName),
    };

    private static SubtitleCandidate Candidate(
        string provider, string release = "", bool hi = false, bool hash = false) => new()
    {
        Provider = provider,
        DownloadRef = "r",
        Language = "en",
        ReleaseName = release,
        HearingImpaired = hi,
        HashMatch = hash,
    };

    [Fact]
    public void Hearing_impaired_ranks_below_a_regular_subtitle()
    {
        var item = Item("Show.S01E01.720p.WEB-GROUP.mkv");
        var regular = SubtitleSearchService.ComputeScore(Candidate("A", "Show.S01E01.720p.WEB-GROUP"), item);
        var hi = SubtitleSearchService.ComputeScore(Candidate("A", "Show.S01E01.720p.WEB-GROUP", hi: true), item);
        Assert.True(regular > hi);
    }

    [Fact]
    public void Unreliable_providers_rank_lower_on_equal_releases()
    {
        var item = Item("Show.S01E01.mkv");
        var reliable = SubtitleSearchService.ComputeScore(Candidate("A", "Show.S01E01"), item, providerSuccessRate: 1.0);
        var flaky = SubtitleSearchService.ComputeScore(Candidate("B", "Show.S01E01"), item, providerSuccessRate: 0.2);
        Assert.True(reliable > flaky);
    }

    [Fact]
    public void Hash_match_beats_provider_reliability()
    {
        var item = Item("Show.S01E01.mkv");
        var hashed = SubtitleSearchService.ComputeScore(Candidate("B", hash: true), item, providerSuccessRate: 0.2);
        var plain = SubtitleSearchService.ComputeScore(Candidate("A", "Show.S01E01"), item, providerSuccessRate: 1.0);
        Assert.True(hashed > plain);
    }

    [Fact]
    public void Download_order_caps_attempts_per_provider()
    {
        var ranked = new[]
        {
            Candidate("A"), Candidate("A"), Candidate("A"), Candidate("A"),
            Candidate("B"), Candidate("C"),
        };
        var selected = SubtitleSearchService.SelectDownloadOrder(ranked);

        Assert.Equal(2, selected.Count(c => c.Provider == "A"));
        Assert.Contains(selected, c => c.Provider == "B");
        Assert.Contains(selected, c => c.Provider == "C");
        // Order is preserved: best-scored first.
        Assert.Equal("A", selected[0].Provider);
    }

    [Fact]
    public void Download_order_is_capped_overall()
    {
        var ranked = Enumerable.Range(0, 20).Select(i => Candidate($"P{i}")).ToArray();
        Assert.Equal(6, SubtitleSearchService.SelectDownloadOrder(ranked).Count);
    }
}

public class ShowMetadataMergeTests
{
    private static TvShow Show(string name, string? premiered, string source, int id = 1) =>
        new(id, name, premiered, null, null, source);

    [Fact]
    public void Merge_keeps_primary_first_and_drops_duplicates()
    {
        var tvmaze = new[] { Show("Attack on Titan", "2013-04-07", "TVMaze") };
        var tmdb = new[]
        {
            Show("Attack on Titan", "2013-04-06", "TMDB", 2), // same show, same year → duplicate
            Show("Attack on Titan: Junior High", "2015-10-04", "TMDB", 3),
        };

        var merged = ShowMetadataService.MergeShows(tvmaze, tmdb);

        Assert.Equal(2, merged.Count);
        Assert.Equal("TVMaze", merged[0].Source);
        Assert.Equal("Attack on Titan: Junior High", merged[1].Name);
    }

    [Fact]
    public void Same_name_different_year_is_not_a_duplicate()
    {
        var merged = ShowMetadataService.MergeShows(
            [Show("Shameless", "2011-01-09", "TVMaze")],
            [Show("Shameless", "2004-01-13", "TMDB", 2)]);
        Assert.Equal(2, merged.Count);
    }
}
