using Subtitall.Core;
using Subtitall.Core.Reencode;

namespace Subtitall.Tests;

public class ReencodeTests
{
    // What ffprobe prints for an MKV with a cover image, one video, two audio and one subtitle track.
    private const string ProbeJson = """
        {
          "streams": [
            { "index": 0, "codec_name": "mjpeg", "codec_type": "video", "width": 600, "height": 900,
              "disposition": { "attached_pic": 1 } },
            { "index": 1, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080,
              "pix_fmt": "yuv420p", "avg_frame_rate": "24000/1001", "r_frame_rate": "24000/1001",
              "disposition": { "attached_pic": 0 } },
            { "index": 2, "codec_name": "ac3", "codec_type": "audio" },
            { "index": 3, "codec_name": "aac", "codec_type": "audio" },
            { "index": 4, "codec_name": "subrip", "codec_type": "subtitle" }
          ],
          "format": { "format_name": "matroska,webm", "duration": "2580.480000", "size": "2254857830", "bit_rate": "6990512" }
        }
        """;

    private static VideoInfo Video(string codec = "h264", int width = 1920, int height = 1080, long bitRate = 7_000_000,
        string pixelFormat = "yuv420p", bool hdr = false) =>
        new()
        {
            VideoCodec = codec, Width = width, Height = height, BitRate = bitRate, PixelFormat = pixelFormat, Hdr = hdr,
            DurationSeconds = 2580, SizeBytes = 2_254_857_830, Fps = 23.976,
        };

    private static readonly EncodeOptions Default = new(TargetCodec.Hevc, QualityLevel.Balanced, EncodeSpeed.Normal, 0);

    // ---- Probe ---------------------------------------------------------------------------------

    [Fact]
    public void Probe_reads_the_real_video_track_not_the_cover_image()
    {
        var info = VideoInfo.Parse(ProbeJson);

        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(1, info.VideoStreamIndex);
        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.Equal(23.976, info.Fps, 3);
        Assert.Equal(2580.48, info.DurationSeconds, 2);
        Assert.Equal(2_254_857_830, info.SizeBytes);
        Assert.Equal(6_990_512, info.BitRate);
        Assert.Equal((2, 1), (info.AudioTracks, info.SubtitleTracks));
        Assert.False(info.Hdr);
        Assert.True(info.HasVideo);
    }

    [Fact]
    public void Probe_flags_hdr_and_works_out_a_missing_bitrate()
    {
        var info = VideoInfo.Parse("""
            { "streams": [ { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 3840, "height": 2160,
                             "pix_fmt": "yuv420p10le", "color_transfer": "smpte2084", "avg_frame_rate": "0/0", "r_frame_rate": "25/1" } ],
              "format": { "duration": "100.0", "size": "125000000" } }
            """);

        Assert.True(info.Hdr);
        Assert.Equal(25, info.Fps);
        Assert.Equal(10_000_000, info.BitRate);
    }

    [Fact]
    public void Probe_of_an_audio_file_has_no_video()
    {
        var info = VideoInfo.Parse("""{ "streams": [ { "index": 0, "codec_name": "mp3", "codec_type": "audio" } ], "format": { "duration": "10" } }""");
        Assert.False(info.HasVideo);
        Assert.Equal(AdviceKind.NoVideo, EncodePlanner.Advise(info, Default).Kind);
        Assert.False(EncodePlanner.Advise(info, Default).Possible);
    }

    // ---- Advice --------------------------------------------------------------------------------

    [Theory]
    [InlineData("h264", 7_000_000, false, 0, AdviceKind.Recommended)]
    [InlineData("mpeg4", 3_000_000, false, 0, AdviceKind.Recommended)]
    [InlineData("hevc", 7_000_000, false, 0, AdviceKind.AlreadyEfficient)]
    [InlineData("av1", 2_000_000, false, 0, AdviceKind.AlreadyEfficient)]
    [InlineData("h264", 1_000_000, false, 0, AdviceKind.AlreadySmall)] // 1 Mb/s at 1080p: nothing left to squeeze
    [InlineData("h264", 20_000_000, true, 0, AdviceKind.Hdr)]
    [InlineData("hevc", 7_000_000, false, 720, AdviceKind.Recommended)] // shrinking the picture always saves
    [InlineData("hevc", 7_000_000, false, 1080, AdviceKind.AlreadyEfficient)] // already that size: no shrinking
    public void Advice_says_which_files_are_worth_it(string codec, long bitRate, bool hdr, int maxHeight, AdviceKind expected)
    {
        var advice = EncodePlanner.Advise(Video(codec, bitRate: bitRate, hdr: hdr), Default with { MaxHeight = maxHeight });

        Assert.Equal(expected, advice.Kind);
        Assert.Equal(expected == AdviceKind.Recommended, advice.Recommended);
        Assert.True(advice.Possible);
        Assert.Equal(expected == AdviceKind.Recommended, advice.Reason.Length == 0);
    }

    // ---- ffmpeg arguments ----------------------------------------------------------------------

    [Fact]
    public void Mkv_is_encoded_to_h265_with_every_other_track_copied()
    {
        var args = EncodePlanner.BuildEncodeArgs(Video(), Default, @"C:\v\Show.S01E01.mkv", @"C:\v\Show.S01E01.reencoded.mkv.part");
        var line = string.Join(' ', args);

        Assert.Contains("-i C:\\v\\Show.S01E01.mkv -map 0:V -map 0:a? -map 0:s? -map 0:t? -c copy", line);
        Assert.Contains("-c:v libx265 -preset medium -crf 22", line);
        Assert.Contains("-progress pipe:1", line);
        Assert.DoesNotContain("-vf", line);
        Assert.DoesNotContain("-pix_fmt", line);
        Assert.DoesNotContain("genpts", line);
        Assert.EndsWith("-f matroska C:\\v\\Show.S01E01.reencoded.mkv.part", line);
    }

    [Fact]
    public void Mp4_stays_mp4_and_is_tagged_for_apple_players()
    {
        var line = string.Join(' ', EncodePlanner.BuildEncodeArgs(Video(), Default, "/v/Holiday.mp4", "/v/Holiday.reencoded.mp4.part"));

        Assert.Contains("-movflags +faststart", line);
        Assert.Contains("-tag:v hvc1", line);
        Assert.DoesNotContain("0:t?", line); // MP4 cannot hold font attachments
        Assert.EndsWith("-f mp4 /v/Holiday.reencoded.mp4.part", line);
    }

    [Fact]
    public void Old_containers_become_mkv_with_rebuilt_timestamps()
    {
        var line = string.Join(' ', EncodePlanner.BuildEncodeArgs(Video("mpeg4"), Default, "/v/Old.Clip.avi", "/v/Old.Clip.reencoded.mkv.part"));

        Assert.Contains("-fflags +genpts -i /v/Old.Clip.avi", line);
        Assert.Contains("-f matroska", line);
    }

    [Theory]
    [InlineData(TargetCodec.Hevc, QualityLevel.High, EncodeSpeed.Slow, "-c:v libx265 -preset slow -crf 19")]
    [InlineData(TargetCodec.Hevc, QualityLevel.Compact, EncodeSpeed.Fast, "-c:v libx265 -preset fast -crf 25")]
    [InlineData(TargetCodec.H264, QualityLevel.Balanced, EncodeSpeed.Normal, "-c:v libx264 -preset medium -crf 21")]
    [InlineData(TargetCodec.Av1, QualityLevel.Balanced, EncodeSpeed.Normal, "-c:v libsvtav1 -preset 6 -crf 29 -pix_fmt yuv420p")]
    public void Each_choice_maps_to_an_encoder_setting(TargetCodec codec, QualityLevel quality, EncodeSpeed speed, string expected)
    {
        var line = string.Join(' ', EncodePlanner.VideoArgs(Video(), new EncodeOptions(codec, quality, speed, 0)));
        Assert.Contains(expected, line);
    }

    [Fact]
    public void A_lower_crf_is_always_the_better_quality()
    {
        foreach (var codec in Enum.GetValues<TargetCodec>())
        {
            Assert.True(EncodePlanner.Crf(codec, QualityLevel.High) < EncodePlanner.Crf(codec, QualityLevel.Balanced));
            Assert.True(EncodePlanner.Crf(codec, QualityLevel.Balanced) < EncodePlanner.Crf(codec, QualityLevel.Compact));
        }
    }

    [Fact]
    public void Resolution_is_only_ever_reduced()
    {
        var down = string.Join(' ', EncodePlanner.VideoArgs(Video(), Default with { MaxHeight = 720 }));
        Assert.Contains("-vf scale=-2:720:flags=lanczos", down);

        Assert.Null(EncodePlanner.TargetHeight(Video(height: 720), Default with { MaxHeight = 1080 }));
        Assert.Null(EncodePlanner.TargetHeight(Video(), Default));
        // A height the settings file should never hold is ignored.
        Assert.Equal(0, EncodeOptions.From(new ReencodeSettings { MaxHeight = 333 }).MaxHeight);
    }

    [Fact]
    public void Odd_pixel_formats_are_converted_and_deep_colour_is_kept()
    {
        Assert.Contains("-pix_fmt yuv420p", string.Join(' ', EncodePlanner.VideoArgs(Video(pixelFormat: "bgr24"), Default)));
        Assert.DoesNotContain("-pix_fmt", string.Join(' ', EncodePlanner.VideoArgs(Video(pixelFormat: "yuv420p10le"), Default)));
        Assert.Contains("-pix_fmt yuv420p10le",
            string.Join(' ', EncodePlanner.VideoArgs(Video(pixelFormat: "yuv422p10le"), Default with { Codec = TargetCodec.Av1 })));
    }

    [Fact]
    public void A_trial_excerpt_is_video_only()
    {
        var line = string.Join(' ', EncodePlanner.BuildSampleArgs(Video(), Default, "/v/a.mkv", 516.25, 8, "/tmp/balanced-0.mkv"));

        Assert.Contains("-ss 516.25 -t 8 -i /v/a.mkv -map 0:V:0 -an -sn -dn", line);
        Assert.Contains("-crf 22", line);
    }

    // ---- File names ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Show.S01E01.mkv", "Show.S01E01.reencoded.mkv", "Show.S01E01.mkv")]
    [InlineData("Holiday.mp4", "Holiday.reencoded.mp4", "Holiday.mp4")]
    [InlineData("Phone.Clip.m4v", "Phone.Clip.reencoded.mp4", "Phone.Clip.mp4")]
    [InlineData("Old.Clip.avi", "Old.Clip.reencoded.mkv", "Old.Clip.mkv")]
    [InlineData("Camera.MOV", "Camera.reencoded.mkv", "Camera.mkv")]
    public void The_new_file_waits_next_to_the_original_then_takes_its_name(string source, string pending, string final)
    {
        var folder = Path.Combine(Path.GetTempPath(), "videos");
        Assert.Equal(Path.Combine(folder, pending), EncodePlanner.PendingPath(Path.Combine(folder, source)));
        Assert.Equal(Path.Combine(folder, final), EncodePlanner.FinalPath(Path.Combine(folder, source)));
        Assert.True(EncodePlanner.IsPendingFile(Path.Combine(folder, pending)));
        Assert.False(EncodePlanner.IsPendingFile(Path.Combine(folder, source)));
    }

    [Fact]
    public void A_waiting_copy_is_not_added_to_the_list_as_a_video()
    {
        using var dir = new TempDir();
        dir.CreateFile("Show.S01E01.mkv");
        dir.CreateFile("Show.S01E01.reencoded.mkv");
        var library = new LibraryService();

        var added = library.AddPaths([dir.Path]);

        Assert.Equal("Show.S01E01.mkv", Assert.Single(added).FileName);
    }

    [Fact]
    public void Replacing_a_file_keeps_what_is_known_about_it()
    {
        using var dir = new TempDir();
        var original = dir.CreateFile("Old.Show.S02E03.avi", "original");
        var replacement = dir.CreateFile("Old.Show.S02E03.mkv", "new");
        var library = new LibraryService();
        var item = Assert.Single(library.AddPaths([original]));
        item.SeasonOverride = 2;

        library.Replace(item, replacement);

        Assert.Equal(replacement, item.Path);
        Assert.Equal(3, item.SizeBytes);
        Assert.Equal((2, 3), (item.Parsed.Season, item.Parsed.Episode));
        Assert.Equal(2, item.SeasonOverride);
        // The list still knows the file under its new path only.
        Assert.Empty(library.AddPaths([replacement]));
        Assert.Single(library.GetAll());
    }

    // ---- Trial maths ---------------------------------------------------------------------------

    [Fact]
    public void A_trial_takes_three_excerpts_of_a_film_and_one_of_a_short_clip()
    {
        var (starts, length) = EncodePlanner.SamplePositions(2580);
        Assert.Equal([516, 1290, 2064], starts);
        Assert.Equal(8, length);

        var (shortStarts, shortLength) = EncodePlanner.SamplePositions(9.5);
        Assert.Equal([0], shortStarts);
        Assert.Equal(9.5, shortLength);
    }

    [Fact]
    public void The_estimate_only_shrinks_the_video_part_of_the_file()
    {
        // 1000 MB of which 90 % is video; the excerpts came out at 40 % of the original video.
        Assert.Equal(460_000_000, EncodePlanner.EstimateSize(1_000_000_000, 0.9, 0.4));
        Assert.Equal(54, EncodePlanner.GainPercent(1_000_000_000, 460_000_000));
        Assert.Equal(-5, EncodePlanner.GainPercent(1_000_000_000, 1_050_000_000));
        Assert.Equal(0, EncodePlanner.GainPercent(0, 10));
    }

    [Theory]
    [InlineData(0.995, "Visually identical")]
    [InlineData(0.975, "Excellent")]
    [InlineData(0.955, "Very good")]
    [InlineData(0.93, "Good")]
    [InlineData(0.80, "Visible loss")]
    public void A_score_is_put_in_plain_words(double ssim, string expected) =>
        Assert.Equal(expected, EncodePlanner.QualityLabel(ssim));

    [Theory]
    [InlineData(2580, 2580.4, true)]
    [InlineData(2580, 2560, true)]   // within 1 %
    [InlineData(2580, 1300, false)]  // an encode that stopped halfway
    [InlineData(30, 27, false)]
    [InlineData(0, 12, true)]        // original length unknown: nothing to compare with
    public void A_shorter_new_file_never_replaces_the_original(double original, double encoded, bool ok) =>
        Assert.Equal(ok, EncodePlanner.DurationMatches(original, encoded));

    // ---- What ffmpeg prints --------------------------------------------------------------------

    [Fact]
    public void Progress_blocks_are_folded_into_one_report_each()
    {
        var reader = new FfmpegRunner.ProgressReader();
        var reports = new[]
        {
            "frame=1200", "fps=48.2", "bitrate=2100.5kbits/s", "out_time_us=50050000", "out_time_ms=50050000",
            "out_time=00:00:50.050000", "speed=2.01x", "progress=continue",
            "out_time_us=N/A", "speed=N/A", "progress=end",
        }.Select(reader.Read).OfType<EncodeProgress>().ToList();

        Assert.Equal(2, reports.Count);
        Assert.Equal(50.05, reports[0].Seconds, 2);
        Assert.Equal(2.01, reports[0].Speed, 2);
        Assert.Equal(50.05, reports[1].Seconds, 2); // "N/A" keeps the last known position
    }

    [Fact]
    public void The_similarity_score_is_read_from_the_filter_summary()
    {
        const string stderr = """
            Input #0, matroska,webm, from 'a.mkv':
            [Parsed_ssim_2 @ 000001] SSIM Y:0.991234 (20.571) U:0.994 (22.2) V:0.995 (23.0) All:0.992345 (21.160)
            """;
        Assert.Equal(0.992345, FfmpegRunner.ParseSsim(stderr));
        Assert.Null(FfmpegRunner.ParseSsim("Conversion failed!"));
    }

    [Theory]
    [InlineData("ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024 the FFmpeg developers", "7.1")]
    [InlineData("ffmpeg version n7.1.1-6-g48c0f071d4-20250405 Copyright (c) 2000-2025", "7.1.1")]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023", "6.1.1")]
    [InlineData("ffmpeg version N-118193-g5f38c82536-20250101 Copyright", "N-118193-g5f38c82536-20250101")]
    [InlineData("something else", "")]
    public void The_version_is_read_from_the_banner(string banner, string expected) =>
        Assert.Equal(expected, FfmpegRunner.ParseVersion(banner + "\nbuilt with gcc"));

    [Fact]
    public void Packet_sizes_are_summed_inside_the_excerpt_only()
    {
        // stream_index,pts_time,size — video is track 0; the first lines are before the excerpt.
        const string csv = "0,9.500,9000\n1,9.900,400\n0,10.000,5000\n1,10.010,300\n0,10.042,1000\n2,N/A,50\n0,17.990,2000\n0,18.000,7000\n";

        var (video, all) = FfmpegRunner.SumPackets(csv, 0, 10, 8);

        Assert.Equal(8000, video);
        Assert.Equal(8300, all);
    }

    // ---- Finding and downloading ffmpeg ---------------------------------------------------------

    private const string ReleaseJson = """
        { "tag_name": "latest", "assets": [
          { "name": "checksums.sha256", "size": 5296, "browser_download_url": "https://example.test/checksums.sha256" },
          { "name": "ffmpeg-master-latest-win64-gpl.zip", "size": 205, "digest": "sha256:aaaa", "browser_download_url": "https://example.test/master-win64.zip" },
          { "name": "ffmpeg-master-latest-win64-gpl-shared.zip", "size": 90, "browser_download_url": "https://example.test/master-win64-shared.zip" },
          { "name": "ffmpeg-n7.1-latest-win64-gpl-7.1.zip", "size": 197, "digest": "sha256:bbbb", "browser_download_url": "https://example.test/n7.1-win64.zip" },
          { "name": "ffmpeg-n8.0-latest-win64-gpl-8.0.zip", "size": 198, "digest": "sha256:cccc", "browser_download_url": "https://example.test/n8.0-win64.zip" },
          { "name": "ffmpeg-n8.0-latest-win64-lgpl-8.0.zip", "size": 150, "browser_download_url": "https://example.test/n8.0-win64-lgpl.zip" },
          { "name": "ffmpeg-n8.0-latest-win64-gpl-shared-8.0.zip", "size": 90, "browser_download_url": "https://example.test/n8.0-win64-shared.zip" },
          { "name": "ffmpeg-n8.0-latest-linux64-gpl-8.0.tar.xz", "size": 153, "digest": "sha256:dddd", "browser_download_url": "https://example.test/n8.0-linux64.tar.xz" },
          { "name": "ffmpeg-master-latest-linuxarm64-gpl.tar.xz", "size": 133, "browser_download_url": "https://example.test/master-linuxarm64.tar.xz" }
        ] }
        """;

    [Theory]
    [InlineData("win64", "ffmpeg-n8.0-latest-win64-gpl-8.0.zip", "cccc")] // newest stable, static, GPL (x264 + x265)
    [InlineData("linux64", "ffmpeg-n8.0-latest-linux64-gpl-8.0.tar.xz", "dddd")]
    [InlineData("linuxarm64", "ffmpeg-master-latest-linuxarm64-gpl.tar.xz", null)] // no stable build listed: development one
    public void The_newest_stable_build_of_the_platform_is_picked(string platform, string expectedName, string? expectedSha)
    {
        var asset = FfmpegDownload.PickAsset(FfmpegDownload.ParseRelease(ReleaseJson), platform);

        Assert.NotNull(asset);
        Assert.Equal(expectedName, asset.Name);
        Assert.Equal(expectedSha, asset.Sha256);
        Assert.StartsWith("https://example.test/", asset.Url);
    }

    [Fact]
    public void Without_a_listing_the_default_link_is_used()
    {
        Assert.Null(FfmpegDownload.PickAsset(FfmpegDownload.ParseRelease(ReleaseJson), "winarm64"));
        Assert.Null(FfmpegDownload.PickAsset(FfmpegDownload.ParseRelease("""{ "message": "API rate limit exceeded" }"""), "win64"));

        Assert.EndsWith("/ffmpeg-master-latest-win64-gpl.zip", FfmpegDownload.Fallback("win64").Url);
        Assert.EndsWith("/ffmpeg-master-latest-linux64-gpl.tar.xz", FfmpegDownload.Fallback("linux64").Url);
    }

    [Fact]
    public void Ffmpeg_is_looked_for_in_the_users_folder_then_the_apps_then_the_path()
    {
        using var dir = new TempDir();
        string Install(string folder)
        {
            dir.CreateFile(Path.Combine(folder, FfmpegLocator.ExeName("ffmpeg")));
            dir.CreateFile(Path.Combine(folder, FfmpegLocator.ExeName("ffprobe")));
            return Path.Combine(dir.Path, folder);
        }
        var app = Install("app");
        var onPath = Install("usr-bin");
        var custom = Install(Path.Combine("mine", "bin"));
        dir.CreateFile(Path.Combine("half", FfmpegLocator.ExeName("ffmpeg"))); // no ffprobe next to it: unusable
        string[] path = [Path.Combine(dir.Path, "half"), Path.Combine(dir.Path, "missing"), onPath];

        // The user's folder may be given with or without its "bin", or as the program itself.
        foreach (var given in new[] { custom, Path.Combine(dir.Path, "mine"), Path.Combine(custom, FfmpegLocator.ExeName("ffmpeg")) })
            Assert.Equal(FfmpegSource.Custom, FfmpegLocator.Find(given, app, path)!.Source);

        Assert.Equal(
            [FfmpegSource.Custom, FfmpegSource.App, FfmpegSource.Path],
            FfmpegLocator.FindAll(custom, app, path).Select(l => l.Source));
        Assert.Equal(FfmpegSource.App, FfmpegLocator.Find("", app, path)!.Source);
        Assert.Equal(Path.Combine(onPath, FfmpegLocator.ExeName("ffprobe")), FfmpegLocator.Find(null, Path.Combine(dir.Path, "none"), path)!.Ffprobe);
        Assert.Null(FfmpegLocator.Find(null, Path.Combine(dir.Path, "none"), [Path.Combine(dir.Path, "half")]));
    }

    [Fact]
    public void Only_the_package_managers_present_are_fallbacks_in_order_of_preference()
    {
        using var dir = new TempDir();
        var bin = Path.Combine(dir.Path, "bin");
        dir.CreateFile(Path.Combine("bin", "choco.exe"));
        dir.CreateFile(Path.Combine("bin", "winget.exe"));
        dir.CreateFile(Path.Combine("shims", "scoop.cmd")); // Scoop is a script, not a program

        var all = FfmpegPackageManagers.Available("windows", [Path.Combine(dir.Path, "missing"), bin, Path.Combine(dir.Path, "shims")]);
        Assert.Equal(["winget", "scoop", "choco"], all.Select(a => a.Installer.Name));
        Assert.Equal(Path.Combine(dir.Path, "shims", "scoop.cmd"), all[1].CommandPath);
        // Chocolatey needs administrator rights: the user is told a confirmation is coming.
        Assert.Equal([false, false, true], all.Select(a => a.Installer.Elevated));
        Assert.Contains("Gyan.FFmpeg", all[0].Installer.Arguments);
        Assert.Contains("--accept-package-agreements", all[0].Installer.Arguments);

        Assert.Equal(["winget", "choco"], FfmpegPackageManagers.Available("windows", [bin]).Select(a => a.Installer.Name));
        Assert.Empty(FfmpegPackageManagers.Available("windows", [dir.Path]));
        // A Windows program is not a command on Linux.
        Assert.Null(FfmpegPackageManagers.FindCommand("winget", [bin], windows: false));
    }

    [Fact]
    public void A_linux_package_manager_needs_the_desktop_password_prompt_too()
    {
        using var dir = new TempDir();
        dir.CreateFile("apt-get");
        dir.CreateFile("pacman");
        Assert.Empty(FfmpegPackageManagers.Available("linux", [dir.Path])); // no pkexec: nothing can ask for the password

        dir.CreateFile("pkexec");
        var available = FfmpegPackageManagers.Available("linux", [dir.Path]);

        Assert.Equal(["apt", "pacman"], available.Select(a => a.Installer.Name));
        Assert.Equal(Path.Combine(dir.Path, "pkexec"), available[0].CommandPath);
        Assert.Equal(["apt-get", "install", "-y", "ffmpeg"], available[0].Installer.Arguments);
        Assert.Equal(["brew"], FfmpegPackageManagers.For("mac").Select(i => i.Name));
    }

    [Fact]
    public void Ffmpeg_is_also_looked_for_where_package_managers_put_it()
    {
        using var dir = new TempDir();
        var local = Path.Combine(dir.Path, "Local");
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe", "ffmpeg-8.0-full_build", "bin");
        Directory.CreateDirectory(winget);
        Directory.CreateDirectory(Path.Combine(local, "Microsoft", "WinGet", "Packages", "Other.Tool_x", "tool-1.0"));

        var folders = FfmpegPackageManagers.KnownFolders("windows", Path.Combine(dir.Path, "Home"), local, Path.Combine(dir.Path, "ProgramData"));

        Assert.Equal(
        [
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
            winget,
            Path.Combine(dir.Path, "Home", "scoop", "apps", "ffmpeg", "current", "bin"),
            Path.Combine(dir.Path, "Home", "scoop", "shims"),
            Path.Combine(dir.Path, "ProgramData", "chocolatey", "bin"),
        ], folders);
        Assert.Contains("/opt/homebrew/bin", FfmpegPackageManagers.KnownFolders("mac", "/Users/me", "", ""));
        Assert.DoesNotContain("/opt/homebrew/bin", FfmpegPackageManagers.KnownFolders("linux", "/home/me", "", ""));
    }

    // ---- Settings ------------------------------------------------------------------------------

    [Fact]
    public void Reencode_settings_survive_a_save_and_default_to_balanced_h265()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.Path);
        Assert.Equal((TargetCodec.Hevc, QualityLevel.Balanced, EncodeSpeed.Normal, 0),
            (service.Current.Reencode.Codec, service.Current.Reencode.Quality, service.Current.Reencode.Speed, service.Current.Reencode.MaxHeight));

        service.Save(new AppSettings { Reencode = new ReencodeSettings { FfmpegPath = " \"C:\\Tools\\ffmpeg\\bin\" ", Quality = QualityLevel.High, MaxHeight = 720 } });
        var reloaded = new SettingsService(dir.Path).Current.Reencode;

        Assert.Equal(@"C:\Tools\ffmpeg\bin", reloaded.FfmpegPath);
        Assert.Equal((QualityLevel.High, 720), (reloaded.Quality, reloaded.MaxHeight));
    }

    [Fact]
    public void A_settings_file_from_before_the_feature_still_loads()
    {
        using var dir = new TempDir();
        dir.CreateFile("settings.json", """{ "Languages": ["fr"], "Reencode": null }""");

        var settings = new SettingsService(dir.Path).Current;

        Assert.Equal(["fr"], settings.Languages);
        Assert.Equal(QualityLevel.Balanced, settings.Reencode.Quality);
    }
}
