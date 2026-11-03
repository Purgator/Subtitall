using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Subtitall.Core;
using Subtitall.Core.Providers;

namespace Subtitall.Tests;

public class ProviderQuotaTests
{
    [Fact]
    public void Quota_is_exhausted_until_it_resets()
    {
        var time = new FakeTimeProvider();
        var health = new ProviderHealth(time);
        Assert.False(health.IsQuotaExhausted("X"));
        Assert.Null(health.Quota("X"));

        health.ReportQuota("X", 3, null);
        Assert.False(health.IsQuotaExhausted("X"));

        var reset = time.GetUtcNow().AddHours(2);
        health.ReportQuota("X", 0, reset);
        Assert.True(health.IsQuotaExhausted("X"));

        time.Advance(TimeSpan.FromHours(3));
        Assert.False(health.IsQuotaExhausted("X"));
    }

    [Fact]
    public void Cooldown_end_time_is_exposed()
    {
        var time = new FakeTimeProvider();
        var health = new ProviderHealth(time);
        Assert.Null(health.CooldownUntil("X"));
        health.ReportFailure("X");
        health.ReportFailure("X");
        Assert.Equal(time.GetUtcNow().AddSeconds(30), health.CooldownUntil("X"));
    }
}

public class SearchReportTests
{
    private sealed class FakeProvider(string name, Func<MediaItem, IReadOnlyList<SubtitleCandidate>> search,
        Func<SubtitleCandidate, SubtitlePayload>? download = null, string? cannotSearch = null, bool configured = true)
        : ISubtitleProvider
    {
        public string Name => name;
        public bool IsConfigured(AppSettings settings) => configured;
        public string? CannotSearchReason(MediaItem item) => cannotSearch;
        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaItem item, string language, AppSettings settings, CancellationToken ct) =>
            Task.FromResult(search(item));
        public Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, AppSettings settings, CancellationToken ct) =>
            Task.FromResult((download ?? throw new InvalidOperationException("boom"))(candidate));
    }

    private static SubtitleCandidate Candidate(string provider) => new()
    {
        Provider = provider, DownloadRef = "1", Language = "en", ReleaseName = "Show.S01E01.720p",
    };

    private static readonly byte[] ValidSrt = System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello there\n");

    private static (SubtitleSearchService Service, ProviderHealth Health, MediaItem Item, TempDir Dir) Build(params ISubtitleProvider[] providers)
    {
        var dir = new TempDir();
        var item = new LibraryService().AddPaths([dir.CreateFile("Show.S01E01.720p.mkv")])[0];
        var health = new ProviderHealth();
        var settings = new SettingsService(dir.Path);
        var service = new SubtitleSearchService(providers, settings, health, NullLogger<SubtitleSearchService>.Instance);
        return (service, health, item, dir);
    }

    [Fact]
    public async Task Every_source_reports_what_it_did()
    {
        var (service, _, item, dir) = Build(
            new FakeProvider("Off", _ => [], configured: false),
            new FakeProvider("TvOnly", _ => [], cannotSearch: "TV shows only"),
            new FakeProvider("Empty", _ => []),
            new FakeProvider("Broken", _ => throw new InvalidOperationException("API key rejected")),
            new FakeProvider("Full", _ => [Candidate("Full")]));
        using (dir)
        {
            var report = await service.SearchAsync(item, "en");
            Assert.Single(report.Candidates);
            var byName = report.Sources.ToDictionary(s => s.Provider);
            Assert.Equal(SourceStatus.Off, byName["Off"].Status);
            Assert.Equal(SourceStatus.NotApplicable, byName["TvOnly"].Status);
            Assert.Equal("TV shows only", byName["TvOnly"].Detail);
            Assert.Equal(SourceStatus.Nothing, byName["Empty"].Status);
            Assert.Equal(SourceStatus.Error, byName["Broken"].Status);
            Assert.Contains("API key rejected", byName["Broken"].Detail);
            Assert.Equal(SourceStatus.Found, byName["Full"].Status);
            Assert.Equal(1, byName["Full"].Found);
        }
    }

    [Fact]
    public async Task A_used_up_quota_is_reported_as_such_and_does_not_trip_the_breaker()
    {
        var (service, health, item, dir) = Build(
            new FakeProvider("Quota", _ => [Candidate("Quota"), Candidate("Quota")],
                _ => throw new QuotaExceededException("Daily download quota used up", DateTimeOffset.UtcNow.AddHours(1))));
        using (dir)
        {
            var outcome = await service.DownloadBestAsync(item, "en");
            Assert.False(outcome.Downloaded);
            Assert.Contains("quota", outcome.Error, StringComparison.OrdinalIgnoreCase);
            var source = Assert.Single(outcome.Sources);
            Assert.Equal(SourceStatus.Quota, source.Status);
            Assert.Equal(2, source.Found);
            // Quota is not a failure: the provider stays available for searching.
            Assert.True(health.IsAvailable("Quota"));
        }
    }

    [Fact]
    public async Task Download_failures_are_explained_and_a_working_source_still_wins()
    {
        var (service, _, item, dir) = Build(
            new FakeProvider("Flaky", _ => [Candidate("Flaky")], _ => throw new InvalidOperationException("HTTP 503")),
            new FakeProvider("Solid", _ => [Candidate("Solid")], _ => new SubtitlePayload(ValidSrt, ".srt")));
        using (dir)
        {
            var outcome = await service.DownloadBestAsync(item, "en");
            Assert.True(outcome.Downloaded);
            var byName = outcome.Sources.ToDictionary(s => s.Provider);
            Assert.Equal(SourceStatus.Found, byName["Solid"].Status);
            Assert.Contains("downloaded", byName["Solid"].Detail);
            // Both rank equally, so whichever went first may have failed — a failure must be explained.
            if (byName["Flaky"].Status == SourceStatus.DownloadFailed)
                Assert.Contains("HTTP 503", byName["Flaky"].Detail);
        }
    }

    [Fact]
    public async Task Exhausted_quota_skips_the_provider_s_candidates_without_calling_it()
    {
        var calls = 0;
        var (service, health, item, dir) = Build(
            new FakeProvider("Quota", _ => [Candidate("Quota")], _ => { calls++; throw new InvalidOperationException("should not be called"); }));
        using (dir)
        {
            health.ReportQuota("Quota", 0, DateTimeOffset.UtcNow.AddHours(1));
            var outcome = await service.DownloadBestAsync(item, "en");
            Assert.False(outcome.Downloaded);
            Assert.Equal(0, calls);
            Assert.Equal(SourceStatus.Quota, Assert.Single(outcome.Sources).Status);
        }
    }
}
