using System.Globalization;
using Subtitall.Core;
using Subtitall.Core.Sync;

namespace Subtitall.Tests;

public class SyncTests
{
    // ---- Synthetic dialogue --------------------------------------------------------------------
    // A film is a list of speech intervals. Its audio is a loudness per 20 ms frame (quiet background,
    // loud syllables inside the intervals); its subtitles show a little around each interval.

    private const double Frame = FrameEnergy.FrameSeconds;

    private static List<(double Start, double End)> Dialogue(int seed, double seconds)
    {
        var random = new Random(seed);
        var speech = new List<(double, double)>();
        var t = 6.0;
        while (t < seconds - 10)
        {
            var length = 1.2 + random.NextDouble() * 4;
            speech.Add((t, t + length));
            t += length + 0.6 + random.NextDouble() * 6;
        }
        return speech;
    }

    private static double Gauss(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static float[] Loudness(List<(double Start, double End)> speech, double seconds, int seed = 1)
    {
        var random = new Random(seed);
        var frames = new float[(int)(seconds / Frame)];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = (float)(-50 + Gauss(random));
        foreach (var (start, end) in speech)
        {
            var rate = 3 + random.NextDouble() * 2; // syllables per second
            for (var i = (int)(start / Frame); i < Math.Min(frames.Length, (int)(end / Frame)); i++)
                frames[i] = (float)(-35 + 12 * Math.Abs(Math.Sin(Math.PI * rate * (i * Frame - start))) + Gauss(random));
        }
        return frames;
    }

    /// <summary>The subtitles of a film: one per speech interval, a few missing, a few over silence.</summary>
    private static List<SubtitleCue> Cues(List<(double Start, double End)> speech, int seed = 2)
    {
        var random = new Random(seed);
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < speech.Count; i++)
        {
            if (random.NextDouble() < 0.08)
                continue;
            cues.Add(new SubtitleCue(speech[i].Start - 0.1 + random.NextDouble() * 0.1, speech[i].End + 0.1 + random.NextDouble() * 0.3, $"Line {i} of the dialogue"));
        }
        return cues;
    }

    /// <summary>The same subtitles seen through a transform: <c>t' = scale × t + offset</c> (the inverse of what fixes them).</summary>
    private static List<SubtitleCue> Moved(List<SubtitleCue> cues, double scale, double offset) =>
        cues.Select(c => c with { Start = c.Start * scale + offset, End = c.End * scale + offset }).Where(c => c.Start >= 0).ToList();

    private const double Minutes30 = 1800;

    private static (SyncAnalyzer Analyzer, List<SubtitleCue> Cues, List<(double Start, double End)> Speech) Film(double seconds = Minutes30, int seed = 7)
    {
        var speech = Dialogue(seed, seconds);
        var detected = SpeechDetector.Detect(Loudness(speech, seconds));
        return (new SyncAnalyzer(detected), Cues(speech), speech);
    }

    // ---- Analysis ------------------------------------------------------------------------------

    [Fact]
    public void A_subtitle_that_follows_the_speech_is_in_sync()
    {
        var (analyzer, cues, _) = Film();

        var result = analyzer.Analyze(cues);

        Assert.Equal(SyncVerdict.InSync, result.Verdict);
        Assert.Null(result.Fix);
        Assert.InRange(result.StartError, -0.3, 0.3);
        Assert.True(result.CurrentCoverage > 0.8, $"coverage {result.CurrentCoverage}");
        Assert.NotEqual("low", result.Confidence);
    }

    [Theory]
    [InlineData(-3.4, "too early")]   // the subtitle is 3.4 s before the speech: delay it
    [InlineData(7.2, "too late")]     // the subtitle is 7.2 s after the speech: bring it forward
    [InlineData(-42.0, "too early")]
    public void A_constant_shift_is_found_and_proposed_as_a_fix(double subtitleError, string wording)
    {
        var (analyzer, cues, _) = Film();

        var result = analyzer.Analyze(Moved(cues, 1, subtitleError));

        Assert.Equal(SyncVerdict.Shift, result.Verdict);
        Assert.NotNull(result.Fix);
        Assert.Equal(1, result.Fix!.Scale);
        Assert.InRange(result.Fix.Offset, -subtitleError - 0.2, -subtitleError + 0.2);
        Assert.Contains(wording, result.Headline);
        // Applying the fix makes it follow the speech again.
        var fixedCues = Moved(Moved(cues, 1, subtitleError), result.Fix.Scale, result.Fix.Offset);
        Assert.Equal(SyncVerdict.InSync, analyzer.Analyze(fixedCues).Verdict);
        Assert.True(result.FixedCoverage > result.CurrentCoverage + 0.3);
    }

    [Fact]
    public void A_subtitle_timed_for_another_frame_rate_is_stretched_back()
    {
        var (analyzer, cues, _) = Film();
        // Timed on 23.976 fps frames, played on a 25 fps video: the subtitle runs 4.3 % too slowly.
        var scale = 23.976024 / 25;
        var wrong = Moved(cues, 1 / scale, 2.0); // times / 0.95904, then 2 s late

        var result = analyzer.Analyze(wrong);

        Assert.Equal(SyncVerdict.Rescale, result.Verdict);
        Assert.NotNull(result.Fix);
        Assert.Equal(scale, result.Fix!.Scale, 3);
        Assert.Equal("23.976 → 25 fps", result.Fix.Rate);
        Assert.Contains("23.976 → 25 fps", result.Headline);
        var fixedCues = Moved(wrong, result.Fix.Scale, result.Fix.Offset);
        Assert.Equal(SyncVerdict.InSync, analyzer.Analyze(fixedCues).Verdict);
    }

    [Fact]
    public void A_small_drift_is_caught_from_how_the_offset_changes_along_the_video()
    {
        var (analyzer, cues, _) = Film();
        // 24 fps subtitles on a 23.976 fps video drift by 1.8 s over half an hour.
        var wrong = Moved(cues, 23.976024 / 24, 0);

        var result = analyzer.Analyze(wrong);

        Assert.Equal(SyncVerdict.Rescale, result.Verdict);
        Assert.Equal(24 / 23.976024, result.Fix!.Scale, 4);
        Assert.True(result.Segments.Count(s => s.Valid) >= 3);
        // The needed shift grows along the video: that is what the segments show.
        var valid = result.Segments.Where(s => s.Valid).ToList();
        Assert.True(valid[^1].Shift - valid[0].Shift > 1.0);
    }

    [Fact]
    public void A_subtitle_made_for_another_cut_does_not_pass_for_a_shift()
    {
        var (analyzer, cues, _) = Film();
        // The second half is 40 s further along: an extended cut has something more in it.
        var otherCut = cues.Select(c => c.Start < 900 ? c : c with { Start = c.Start + 40, End = c.End + 40 }).ToList();

        var result = analyzer.Analyze(otherCut);

        Assert.Equal(SyncVerdict.DifferentCut, result.Verdict);
        Assert.Null(result.Fix);
        Assert.Contains(result.Segments, s => s.Outlier);
    }

    [Fact]
    public void A_subtitle_of_another_video_is_not_a_match()
    {
        var (analyzer, _, _) = Film();
        var stranger = Cues(Dialogue(seed: 99, Minutes30));

        var result = analyzer.Analyze(stranger);

        Assert.True(result.Verdict is SyncVerdict.Unclear or SyncVerdict.DifferentCut, result.Verdict.ToString());
        Assert.Null(result.Fix);
    }

    [Fact]
    public void Sound_descriptions_and_songs_are_not_compared_with_speech()
    {
        var (analyzer, cues, _) = Film();
        var noise = Enumerable.Range(0, 60)
            .Select(i => new SubtitleCue(20 + i * 29.3, 22 + i * 29.3, i % 2 == 0 ? "[door slams]" : "♪ la la la ♪"))
            .ToList();

        var result = analyzer.Analyze([.. cues, .. noise]);

        Assert.Equal(SyncVerdict.InSync, result.Verdict);
        Assert.Equal(cues.Count, result.SpeechCues);
    }

    [Fact]
    public void Almost_no_subtitles_cannot_be_judged()
    {
        var (analyzer, _, _) = Film();

        var result = analyzer.Analyze([new SubtitleCue(10, 12, "Hello"), new SubtitleCue(20, 22, "There")]);

        Assert.Equal(SyncVerdict.Unreadable, result.Verdict);
        Assert.Equal(0, result.RankScore);
    }

    [Fact]
    public void Audio_without_speech_cannot_be_judged()
    {
        var silent = new SyncAnalyzer(SpeechDetector.Detect(Loudness([], Minutes30)));

        var result = silent.Analyze(Cues(Dialogue(7, Minutes30)));

        Assert.Equal(SyncVerdict.Unclear, result.Verdict);
    }

    [Fact]
    public void Candidates_are_ranked_by_how_well_they_follow_the_speech_once_fixed()
    {
        var (analyzer, cues, _) = Film();
        var inSync = analyzer.Analyze(cues);
        var shifted = analyzer.Analyze(Moved(cues, 1, 4.0));
        var stranger = analyzer.Analyze(Cues(Dialogue(99, Minutes30)));

        Assert.True(inSync.RankScore > shifted.RankScore, "a subtitle that needs nothing beats one that needs a shift");
        Assert.True(shifted.RankScore > stranger.RankScore);
    }

    [Fact]
    public void The_best_candidate_of_each_language_is_picked_when_it_really_matches()
    {
        SyncAnalysis Made(SyncVerdict verdict, double rank) => new() { Verdict = verdict, RankScore = rank };
        var items = new List<(string Name, string? Language, SyncAnalysis Analysis)>
        {
            ("en-a", "en", Made(SyncVerdict.Shift, 0.30)),
            ("en-b", "en", Made(SyncVerdict.InSync, 0.35)),
            ("fr-a", "fr", Made(SyncVerdict.Unclear, 0.05)),
            ("x-a", null, Made(SyncVerdict.Rescale, 0.28)),
            ("x-b", null, Made(SyncVerdict.DifferentCut, 0.40)),
        };

        var best = SyncRanking.PickBest(items, i => i.Language, i => i.Analysis).Select(i => i.Name).Order().ToList();

        Assert.Equal(["en-b", "x-a"], best);
    }

    // ---- Speech detection ----------------------------------------------------------------------

    [Fact]
    public void Speech_is_told_from_the_background_it_sits_in()
    {
        var speech = Dialogue(3, 600);
        var detected = SpeechDetector.Detect(Loudness(speech, 600));

        double hit = 0, truth = 0, found = 0;
        for (var i = 0; i < detected.Length; i++)
        {
            var t = i * Frame;
            var isSpeech = speech.Any(s => t >= s.Start && t < s.End);
            truth += isSpeech ? 1 : 0;
            found += detected[i];
            hit += isSpeech ? detected[i] : 0;
        }
        Assert.True(hit / truth > 0.9, $"recall {hit / truth}");
        Assert.True(hit / found > 0.9, $"precision {hit / found}");
    }

    [Fact]
    public void A_steady_loud_sound_is_not_speech()
    {
        var random = new Random(5);
        var frames = Enumerable.Range(0, 5000).Select(_ => (float)(-30 + Gauss(random) * 0.5)).ToArray();

        var detected = SpeechDetector.Detect(frames);

        Assert.True(detected.Sum() < 50, "sustained noise has no syllable rhythm");
    }

    [Fact]
    public void Loudness_is_measured_per_twenty_milliseconds_whatever_the_chunking()
    {
        // 1 s of a full-scale-ish square wave, then 1 s of silence, fed in odd-sized chunks.
        var pcm = new byte[FrameEnergy.SampleRate * 2 * 2];
        for (var i = 0; i < FrameEnergy.SampleRate; i++)
        {
            var sample = (short)(i % 2 == 0 ? 16384 : -16384);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var energy = new FrameEnergy();
        for (var offset = 0; offset < pcm.Length; offset += 1001)
            energy.Add(pcm.AsSpan(offset, Math.Min(1001, pcm.Length - offset)));
        var frames = energy.ToArray();

        Assert.Equal(100, frames.Length);
        Assert.Equal(-6.0, frames[10], 1);   // amplitude 0.5 → −6 dB
        Assert.Equal(-100, frames[80], 1);
    }

    // ---- FFT -----------------------------------------------------------------------------------

    [Fact]
    public void Fft_agrees_with_the_plain_definition_and_inverts_itself()
    {
        var random = new Random(1);
        const int size = 64;
        var re = Enumerable.Range(0, size).Select(_ => random.NextDouble()).ToArray();
        var im = new double[size];
        var original = (double[])re.Clone();

        var fft = new Fft(size);
        fft.Transform(re, im, inverse: false);

        for (var k = 0; k < size; k += 7)
        {
            double sr = 0, si = 0;
            for (var t = 0; t < size; t++)
            {
                sr += original[t] * Math.Cos(-2 * Math.PI * k * t / size);
                si += original[t] * Math.Sin(-2 * Math.PI * k * t / size);
            }
            Assert.Equal(sr, re[k], 9);
            Assert.Equal(si, im[k], 9);
        }

        fft.Transform(re, im, inverse: true);
        for (var t = 0; t < size; t++)
            Assert.Equal(original[t], re[t], 9);
    }

    // ---- Subtitle files ------------------------------------------------------------------------

    private const string Srt = "1\r\n00:00:01,000 --> 00:00:03,500\r\nHello there\r\n\r\n2\r\n00:01:02,250 --> 00:01:04,000\r\n<i>General</i> Kenobi\r\nwith a second line\r\n\r\n3\r\n01:00:00,000 --> 01:00:01,000\r\n[door slams]\r\n";

    [Fact]
    public void Srt_cues_are_read_with_their_text_and_times()
    {
        var cues = SubtitleParser.Parse(Srt, ".srt");

        Assert.Equal(3, cues.Count);
        Assert.Equal((1.0, 3.5, "Hello there"), (cues[0].Start, cues[0].End, cues[0].Text));
        Assert.Equal(62.25, cues[1].Start);
        Assert.Equal("General Kenobi\nwith a second line", cues[1].Text);
        Assert.Equal(3600, cues[2].Start);
        Assert.Equal([true, true, false], cues.Select(c => c.IsSpeech).ToArray());
    }

    [Fact]
    public void WebVtt_cues_may_leave_out_the_hour_and_carry_settings()
    {
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:03.500 align:start position:10%\nFirst\n\n1:02:03.000 --> 1:02:04.000\nSecond\n";

        var cues = SubtitleParser.Parse(vtt, ".vtt");

        Assert.Equal([1.0, 3723.0], cues.Select(c => c.Start).ToArray());
        Assert.Equal([3.5, 3724.0], cues.Select(c => c.End).ToArray());
    }

    [Fact]
    public void Ass_dialogue_is_read_without_its_styling_or_typeset_signs()
    {
        const string ass = """
            [Script Info]
            Title: x

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:03.50,Default,,0,0,0,,{\i1}Hello{\i0}, my friend\NHow are you?
            Dialogue: 1,0:00:05.00,0:00:06.00,Sign,,0,0,0,,{\pos(10,10)\p1}m 0 0 l 10 0 10 10
            Comment: 0,0:00:07.00,0:00:08.00,Default,,0,0,0,,not a line
            Dialogue: 0,1:00:00.00,1:00:02.50,Default,,0,0,0,,Late
            """;

        var cues = SubtitleParser.Parse(ass, ".ass");

        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello, my friend\nHow are you?", cues[0].Text);
        Assert.Equal((3600.0, 3602.5), (cues[1].Start, cues[1].End));
    }

    [Fact]
    public void Bytes_are_decoded_as_utf8_with_a_latin1_fallback()
    {
        Assert.Equal("Café", SubtitleParser.Decode([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes("Café")]));
        Assert.Equal("Café", SubtitleParser.Decode(System.Text.Encoding.UTF8.GetBytes("Café")));
        Assert.Equal("Café", SubtitleParser.Decode([0x43, 0x61, 0x66, 0xE9]));
    }

    [Fact]
    public void Retiming_srt_moves_and_stretches_every_time_and_keeps_the_rest()
    {
        var moved = SubtitleRetimer.Retime(Srt, ".srt", scale: 1.0, offset: 2.5);

        Assert.Contains("00:00:03,500 --> 00:00:06,000", moved);
        Assert.Contains("00:01:04,750 --> 00:01:06,500", moved);
        Assert.Contains("<i>General</i> Kenobi\r\nwith a second line", moved);
        Assert.Contains("\r\n\r\n2\r\n", moved);

        var stretched = SubtitleRetimer.Retime(Srt, ".srt", scale: 1.5, offset: 0);
        Assert.Contains("00:01:33,375 --> 00:01:36,000", stretched);
    }

    [Fact]
    public void Retiming_never_goes_before_zero_and_follows_the_format()
    {
        Assert.Contains("00:00:00,000 --> 00:00:00,500", SubtitleRetimer.Retime(Srt, ".srt", 1, -3));
        Assert.Contains("00:00:04.000 --> 00:00:06.500", SubtitleRetimer.Retime("WEBVTT\n\n00:00:01.500 --> 00:00:04.000\nx\n", ".vtt", 1, 2.5)
            .Replace("00:00:04.000 --> 00:00:06.500", "00:00:04.000 --> 00:00:06.500"));

        const string ass = "[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:01.00,0:00:03.50,Default,Hello, you\n";
        var moved = SubtitleRetimer.Retime(ass, ".ass", 1, 10);
        Assert.Contains("Dialogue: 0,0:00:11.00,0:00:13.50,Default,Hello, you", moved);
    }

    [Fact]
    public void Retiming_then_parsing_gives_the_moved_cues()
    {
        var original = SubtitleParser.Parse(Srt, ".srt");

        var again = SubtitleParser.Parse(SubtitleRetimer.Retime(Srt, ".srt", 1.0427, 0.75), ".srt");

        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Start * 1.0427 + 0.75, again[i].Start, 2);
            Assert.Equal(original[i].End * 1.0427 + 0.75, again[i].End, 2);
        }
    }

    [Theory]
    [InlineData("Show.S01E01.en.srt", "en")]
    [InlineData("Show.S01E01.pt-BR.srt", "pt-br")]
    [InlineData("Show.S01E01.srt", null)]
    [InlineData("Show.S01E01.PROPER.srt", null)]
    public void The_language_is_guessed_from_the_file_name(string name, string? expected) =>
        Assert.Equal(expected, SubtitleNaming.GuessLanguage(name));

    // ---- Audio tracks --------------------------------------------------------------------------

    [Fact]
    public void Audio_tracks_are_listed_in_order_with_their_language_and_default_flag()
    {
        const string json = """
            { "streams": [
              { "index": 0, "codec_type": "video", "codec_name": "h264" },
              { "index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "tags": { "language": "fre" }, "disposition": { "default": 0 } },
              { "index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": { "language": "eng", "title": "Commentary" }, "disposition": { "default": 1 } },
              { "index": 3, "codec_type": "subtitle", "codec_name": "subrip" }
            ] }
            """;

        var tracks = AudioTrack.Parse(json);

        Assert.Equal(2, tracks.Count);
        Assert.Equal([0, 1], tracks.Select(t => t.Index).ToArray());
        Assert.Equal("fre · AC3 5.1", tracks[0].Label);
        Assert.Equal("eng · AAC stereo · Commentary", tracks[1].Label);
        Assert.Equal(1, AudioTrack.Preferred(tracks));
        Assert.Equal(0, AudioTrack.Preferred([]));
    }

    [Fact]
    public void Ffmpeg_is_asked_for_the_chosen_track_as_speech_band_pcm()
    {
        var args = AudioExtractor.Arguments(@"C:\v ideos\a.mkv", 1);

        Assert.Equal(@"C:\v ideos\a.mkv", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("0:a:1", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Contains("s16le", args);
        Assert.Equal("8000", args[args.ToList().IndexOf("-ar") + 1]);
    }
}
