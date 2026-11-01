using Subtitall.Core;

namespace Subtitall.Tests;

public class EpisodeGuesserTests
{
    // Attack on Titan on TVMaze: 25 + 12 + 22 + 28 regular episodes.
    private static readonly List<TvEpisode> AttackOnTitan =
    [
        .. Enumerable.Range(1, 25).Select(n => new TvEpisode(1, n, $"S1E{n}", null)),
        .. Enumerable.Range(1, 12).Select(n => new TvEpisode(2, n, $"S2E{n}", null)),
        .. Enumerable.Range(1, 22).Select(n => new TvEpisode(3, n, $"S3E{n}", null)),
        .. Enumerable.Range(1, 28).Select(n => new TvEpisode(4, n, $"S4E{n}", null)),
    ];

    private static GuessInput Input(string file, int? seasonOverride = null) =>
        new(FilenameParser.Parse(file), seasonOverride);

    private static (int Season, int Episode) SE(EpisodeGuess? guess) =>
        guess is null ? (0, 0) : (guess.Season, guess.Episode);

    [Fact]
    public void Explicit_season_and_episode_are_kept()
    {
        var guesses = EpisodeGuesser.Guess([Input("[AnimeOut] Attack on Titan - S04E30 - The Final Chapters Part 2 WEB 1080pp Hi10 EAC3 [90248211][1080pp][Baws][RapidBot].mkv")], AttackOnTitan);
        Assert.Equal((4, 30), SE(guesses[0]));
        Assert.Equal(GuessConfidence.Explicit, guesses[0]!.Confidence);
    }

    [Fact]
    public void Final_season_in_the_title_means_the_last_season()
    {
        var guesses = EpisodeGuesser.Guess(
            [Input("[AnimeOut] Attack on Titan The Final Season - 15 BD 1080pp Hi10 FLAC [7E56EAB4][1080pp][SCY][RapidBot].mkv")],
            AttackOnTitan);
        Assert.Equal((4, 15), SE(guesses[0]));
        Assert.Equal(GuessConfidence.High, guesses[0]!.Confidence);
    }

    [Fact]
    public void Final_season_needs_the_episode_list()
    {
        var guesses = EpisodeGuesser.Guess([Input("Attack on Titan The Final Season - 15.mkv")], null);
        Assert.Null(guesses[0]);
    }

    [Fact]
    public void Season_named_in_the_title_wins()
    {
        var guesses = EpisodeGuesser.Guess([Input("Shingeki no Kyojin Season 3 - 05 [1080p].mkv")], AttackOnTitan);
        Assert.Equal((3, 5), SE(guesses[0]));
        guesses = EpisodeGuesser.Guess([Input("My Hero Academia 2nd Season - 07.mkv")], null);
        Assert.Equal((2, 7), SE(guesses[0]));
    }

    [Fact]
    public void Siblings_with_a_season_vouch_for_bare_numbers()
    {
        var guesses = EpisodeGuesser.Guess(
        [
            Input("Attack on Titan - S04E29.mkv"),
            Input("Attack on Titan - S04E30.mkv"),
            Input("Attack on Titan - 15.mkv"),
            Input("Attack on Titan - 16.mkv"),
        ], AttackOnTitan);
        Assert.Equal((4, 15), SE(guesses[2]));
        Assert.Equal((4, 16), SE(guesses[3]));
        Assert.Equal(GuessConfidence.High, guesses[2]!.Confidence);
    }

    [Fact]
    public void Siblings_vouch_even_without_an_episode_list_but_with_low_confidence()
    {
        var guesses = EpisodeGuesser.Guess([Input("Show - S02E01.mkv"), Input("Show - 03.mkv")], null);
        Assert.Equal((2, 3), SE(guesses[1]));
        Assert.Equal(GuessConfidence.Low, guesses[1]!.Confidence);

        // Two different sibling seasons: nothing to go on.
        guesses = EpisodeGuesser.Guess([Input("Show - S01E01.mkv"), Input("Show - S02E01.mkv"), Input("Show - 03.mkv")], null);
        Assert.Null(guesses[2]);
    }

    [Fact]
    public void Numbers_past_every_season_are_absolute()
    {
        // Episode 59 overall = S03E22 (25 + 12 + 22).
        var guesses = EpisodeGuesser.Guess([Input("Attack on Titan - 59.mkv")], AttackOnTitan);
        Assert.Equal((3, 22), SE(guesses[0]));
        Assert.Contains("absolute", guesses[0]!.Reason);
    }

    [Fact]
    public void A_group_running_past_season_one_is_absolute_numbering()
    {
        // 1..30 can't be one season (S1 has 25) — even the low numbers are absolute, i.e. season 1.
        var files = Enumerable.Range(1, 30).Select(n => Input($"Attack on Titan - {n:00}.mkv")).ToList();
        var guesses = EpisodeGuesser.Guess(files, AttackOnTitan);
        Assert.Equal((1, 10), SE(guesses[9]));
        Assert.Equal((2, 5), SE(guesses[29]));
        Assert.All(guesses, g => Assert.Equal(GuessConfidence.High, g!.Confidence));
    }

    [Fact]
    public void Ambiguous_bare_numbers_default_to_the_first_fitting_season_with_low_confidence()
    {
        var guesses = EpisodeGuesser.Guess([Input("Attack on Titan - 05.mkv")], AttackOnTitan);
        Assert.Equal((1, 5), SE(guesses[0]));
        Assert.Equal(GuessConfidence.Low, guesses[0]!.Confidence);
        Assert.Contains("pick the season", guesses[0]!.Reason);
    }

    [Fact]
    public void Only_one_season_long_enough_settles_it()
    {
        // Episode 24 exists in seasons 1 and 4 only; 26 in season 4 only.
        var guesses = EpisodeGuesser.Guess([Input("Attack on Titan - 26.mkv")], AttackOnTitan);
        Assert.Equal((4, 26), SE(guesses[0]));
        Assert.Equal(GuessConfidence.High, guesses[0]!.Confidence);
    }

    [Fact]
    public void User_season_override_beats_everything()
    {
        var guesses = EpisodeGuesser.Guess(
            [Input("Attack on Titan The Final Season - 15.mkv", seasonOverride: 2)], AttackOnTitan);
        Assert.Equal((2, 15), SE(guesses[0]));
        Assert.Contains("chosen by you", guesses[0]!.Reason);
    }

    [Fact]
    public void Movies_and_unnumbered_files_get_no_guess()
    {
        var guesses = EpisodeGuesser.Guess([Input("Inception.2010.1080p.mkv"), Input("Some show.mkv")], AttackOnTitan);
        Assert.All(guesses, Assert.Null);
    }
}

public class ShowDetectionRankTests
{
    private static TvShow Show(int id, string name, string? premiered = null, string source = TvMazeService.SourceName) =>
        new(id, name, premiered, null, null, source);

    private static readonly Dictionary<string, IReadOnlyList<string>> NoAliases = [];

    [Fact]
    public void Exact_name_match_resolves()
    {
        var results = new[] { Show(1, "Attack on Titan"), Show(2, "Attack on Titan: Junior High") };
        var detection = ShowDetectionService.Rank("Attack on Titan", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(1, detection.Candidates[0].Id);
    }

    [Fact]
    public void Alias_match_resolves_romaji_titles()
    {
        var results = new[] { Show(1, "Attack on Titan"), Show(2, "Attack on Titan: Junior High") };
        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            ["TVMaze:1"] = ["Shingeki no Kyojin", "L'attaque des Titans"],
        };
        var detection = ShowDetectionService.Rank("Shingeki no Kyojin", null, results, aliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(1, detection.Candidates[0].Id);
    }

    [Fact]
    public void Exact_name_beats_a_spin_off_that_only_carries_the_name_as_alias()
    {
        // TVMaze lists "Attack on Titan" among the akas of the spin-off too.
        var results = new[] { Show(919, "Attack on Titan"), Show(19225, "Attack on Titan: Smoke Signal of Fight Back") };
        var aliases = new Dictionary<string, IReadOnlyList<string>> { ["TVMaze:19225"] = ["Attack on Titan"] };
        var detection = ShowDetectionService.Rank("Attack on Titan", null, results, aliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(919, detection.Candidates[0].Id);
    }

    [Fact]
    public void Top_hit_wins_over_its_own_spin_offs_but_not_over_a_different_show()
    {
        var results = new[] { Show(1, "Frieren: Beyond Journey's End"), Show(2, "Frieren: Beyond Journey's End Specials") };
        var detection = ShowDetectionService.Rank("Frieren", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(1, detection.Candidates[0].Id);

        results = [Show(1, "Frieren: Beyond Journey's End"), Show(2, "Sousou no Frieren: Mahou")];
        detection = ShowDetectionService.Rank("Frieren", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Ambiguous, detection.Status);
    }

    [Fact]
    public void Remakes_with_the_same_name_are_ambiguous_unless_the_year_tells()
    {
        var results = new[] { Show(1, "Battlestar Galactica", "1978-09-17"), Show(2, "Battlestar Galactica", "2004-10-18") };
        var detection = ShowDetectionService.Rank("Battlestar Galactica", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Ambiguous, detection.Status);
        Assert.Equal(2, detection.Candidates.Count);

        detection = ShowDetectionService.Rank("Battlestar Galactica", 2004, results, NoAliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(2, detection.Candidates[0].Id);
    }

    [Fact]
    public void The_same_show_on_two_sources_is_not_a_doubt()
    {
        var results = new[] { Show(1, "Severance", "2022-02-18"), Show(95396, "Severance", "2022-02-17", TmdbService.SourceName) };
        var detection = ShowDetectionService.Rank("Severance", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(TvMazeService.SourceName, detection.Candidates[0].Source);
    }

    [Fact]
    public void A_single_fuzzy_match_is_accepted_and_several_are_offered()
    {
        var results = new[] { Show(1, "Frieren: Beyond Journey's End"), Show(2, "Something Else") };
        var detection = ShowDetectionService.Rank("Frieren", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Resolved, detection.Status);
        Assert.Equal(1, detection.Candidates[0].Id);

        results = [Show(1, "The Office"), Show(2, "Office Ladies"), Show(3, "Unrelated")];
        detection = ShowDetectionService.Rank("Office", null, results, NoAliases);
        Assert.Equal(DetectionStatus.Ambiguous, detection.Status);
        Assert.Equal(2, detection.Candidates.Count);
    }

    [Fact]
    public void No_match_still_offers_the_raw_results_to_pick_from()
    {
        var results = new[] { Show(1, "Totally Different"), Show(2, "Another One") };
        var detection = ShowDetectionService.Rank("My Show", null, results, NoAliases);
        Assert.Equal(DetectionStatus.NotFound, detection.Status);
        Assert.Equal(2, detection.Candidates.Count);

        detection = ShowDetectionService.Rank("My Show", null, [], NoAliases);
        Assert.Equal(DetectionStatus.NotFound, detection.Status);
        Assert.Empty(detection.Candidates);
    }
}
