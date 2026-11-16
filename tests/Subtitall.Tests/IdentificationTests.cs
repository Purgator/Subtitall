using System.Text;
using Subtitall.Core;
using Subtitall.Core.Providers;

namespace Subtitall.Tests;

/// <summary>Identifying files whose name alone does not say what they are.</summary>
public class IdentificationTests
{
    // Seen in the wild: a download bot Base64-encoded the whole release name.
    private const string EncodedOva =
        "W0FuaW1lT3V0XV9TaGluZ2VraV9ub19LeW9qaW5fT1ZBXzJ2Ml9bRFZEXVsxMGJpdF1bRDg5MzZCRTRdW1NhaXplbi1Tb2xkYWRvXVtSYXBpZEJvdF0=";

    [Fact]
    public void Decodes_a_base64_filename_and_reads_the_name_behind_it()
    {
        using var dir = new TempDir();
        var reading = MediaNameReader.Read(dir.CreateFile(EncodedOva + ".mkv"));

        Assert.Equal(NameSource.DecodedFileName, reading.Source);
        Assert.Equal("[AnimeOut]_Shingeki_no_Kyojin_OVA_2v2_[DVD][10bit][D8936BE4][Saizen-Soldado][RapidBot]", reading.Name);
        Assert.Equal("Shingeki no Kyojin", reading.Parsed.Title);
        Assert.Equal(0, reading.Parsed.Season);
        Assert.Equal(2, reading.Parsed.Episode);
        Assert.True(reading.Parsed.Special);
        Assert.Equal("AnimeOut", reading.Parsed.ReleaseGroup);
    }

    [Theory]
    [InlineData("U2hvdy5OYW1lLlMwMUUwMi43MjBw")] // "Show.Name.S01E02.720p", padding not needed
    [InlineData("U2hvdyBOYW1lIC0gMTIgWzEwODBwXQ")] // padding stripped
    public void Decodes_base64_names_with_or_without_padding(string stem)
    {
        var decoded = MediaNameReader.DecodeBase64Name(stem);
        Assert.NotNull(decoded);
        Assert.True(FilenameParser.Parse(decoded).IsEpisode);
    }

    [Theory]
    [InlineData("Interstellar2014Remastered")]
    [InlineData("Show_Name_Episode_12_1080p")]
    [InlineData("TheLordOfTheRingsTheReturnOfTheKing")]
    [InlineData("Show.Name.S01E02.720p")]
    [InlineData("Inception")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA")] // decodes to zero bytes, not to text
    public void Leaves_ordinary_names_alone(string stem) =>
        Assert.Null(MediaNameReader.DecodeBase64Name(stem));

    [Fact]
    public void Ordinary_filenames_are_read_as_they_are()
    {
        using var dir = new TempDir();
        var reading = MediaNameReader.Read(dir.CreateFile("Show.Name.S01E02.720p.mkv"));
        Assert.Equal(NameSource.FileName, reading.Source);
        Assert.Null(reading.Name);
        Assert.Equal("Show Name", reading.Parsed.Title);
    }

    [Fact]
    public void Falls_back_to_the_title_stored_in_the_mkv_when_the_filename_says_nothing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a8f3c1.mkv");
        File.WriteAllBytes(path, Matroska("[Group] Some Show - S02E05 [1080p]"));

        var reading = MediaNameReader.Read(path);
        Assert.Equal(NameSource.EmbeddedTitle, reading.Source);
        Assert.Equal("Some Show", reading.Parsed.Title);
        Assert.Equal(2, reading.Parsed.Season);
        Assert.Equal(5, reading.Parsed.Episode);
    }

    [Fact]
    public void A_usable_filename_wins_over_the_embedded_title()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Real.Show.S01E01.mkv");
        File.WriteAllBytes(path, Matroska("Other Show - S09E09"));
        Assert.Equal("Real Show", MediaNameReader.Read(path).Parsed.Title);
    }

    [Fact]
    public void An_embedded_title_that_identifies_nothing_is_ignored()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a8f3c1.mkv");
        File.WriteAllBytes(path, Matroska("Encoded with love"));
        var reading = MediaNameReader.Read(path);
        Assert.Equal(NameSource.FileName, reading.Source);
        Assert.False(reading.Parsed.IsEpisode);
    }

    [Fact]
    public void Reads_the_matroska_title_past_other_elements()
    {
        using var stream = new MemoryStream(Matroska("Show: The Title / S01E02"));
        Assert.Equal("Show: The Title / S01E02", MatroskaTitle.Read(stream));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x84 })] // header cut short
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 })] // an MP4
    public void Files_that_are_not_matroska_have_no_title(byte[] content)
    {
        using var stream = new MemoryStream(content);
        Assert.Null(MatroskaTitle.Read(stream));
    }

    [Fact]
    public void A_matroska_file_without_title_has_none()
    {
        using var stream = new MemoryStream(Matroska(null));
        Assert.Null(MatroskaTitle.Read(stream));
    }

    [Theory]
    [InlineData("[Group] Shingeki no Kyojin OVA 2v2 [DVD]", "Shingeki no Kyojin", 2)]
    [InlineData("Some.Show.OAV.03.720p.mkv", "Some Show", 3)]
    [InlineData("[Group] Some Show - OAD - 01 [BD 1080p].mkv", "Some Show", 1)]
    [InlineData("Some Show - Special 4.mkv", "Some Show", 4)]
    [InlineData("[Group] Some Show SP2 [1080p].mkv", "Some Show", 2)]
    public void Numbered_specials_are_read_as_season_zero(string file, string title, int number)
    {
        var parsed = FilenameParser.Parse(file);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(0, parsed.Season);
        Assert.Equal(number, parsed.Episode);
        Assert.True(parsed.Special);
        Assert.True(parsed.IsEpisode);
    }

    [Theory]
    [InlineData("Some.Comedy.Special.2019.1080p.WEB.mkv")]
    [InlineData("Show.Name.S01E02.Special.720p.mkv")]
    [InlineData("Jurassic.World.Dominion.2022.mkv")]
    public void Names_that_merely_contain_such_words_are_not_specials(string file) =>
        Assert.False(FilenameParser.Parse(file).Special);

    [Fact]
    public void A_special_read_from_an_ova_number_is_flagged_as_a_guess()
    {
        var guess = EpisodeGuesser.Guess([new GuessInput(FilenameParser.Parse("Show OVA 2.mkv"))], null)[0];
        Assert.NotNull(guess);
        Assert.Equal((0, 2), (guess.Season, guess.Episode));
        Assert.Equal(GuessConfidence.Low, guess.Confidence);
    }

    [Fact]
    public void Hash_lookup_reads_the_episode_most_matching_subtitles_agree_on()
    {
        const string json = """
            {"data":[
              {"attributes":{"moviehash_match":true,"feature_details":{"feature_type":"Episode","title":"Pilot","year":2008,"season_number":1,"episode_number":1,"parent_title":"Breaking Bad"}}},
              {"attributes":{"moviehash_match":true,"feature_details":{"feature_type":"Episode","title":"Pilot","season_number":1,"episode_number":1,"parent_title":"Breaking Bad"}}},
              {"attributes":{"moviehash_match":true,"feature_details":{"feature_type":"Episode","season_number":3,"episode_number":7,"parent_title":"Mislabelled Show"}}},
              {"attributes":{"moviehash_match":false,"feature_details":{"feature_type":"Movie","title":"Unrelated","year":1999}}}
            ]}
            """;
        Assert.Equal(new HashIdentity("Breaking Bad", null, 1, 1), OpenSubtitlesProvider.ParseIdentity(json));
    }

    [Fact]
    public void Hash_lookup_reads_movies_with_their_year()
    {
        const string json = """{"data":[{"attributes":{"moviehash_match":true,"feature_details":{"feature_type":"Movie","title":"Inception","movie_name":"2010 - Inception","year":2010}}}]}""";
        Assert.Equal(new HashIdentity("Inception", 2010, null, null), OpenSubtitlesProvider.ParseIdentity(json));
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"attributes":{"moviehash_match":false,"feature_details":{"title":"Unrelated","year":1999}}}]}""")]
    [InlineData("not json")]
    public void Hash_lookup_without_a_hash_match_identifies_nothing(string json) =>
        Assert.Null(OpenSubtitlesProvider.ParseIdentity(json));

    [Fact]
    public void Library_keeps_where_the_name_came_from_and_forgets_it_after_a_rename()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var item = library.AddPaths([dir.CreateFile(EncodedOva + ".mkv")])[0];
        Assert.Equal(NameSource.DecodedFileName, item.NameSource);
        Assert.False(item.IsUnidentified);

        library.Identify(item, new ParsedMedia { Title = "Attack on Titan", Season = 0, Episode = 2 }, NameSource.User, null);
        Assert.Equal(NameSource.User, item.NameSource);
        Assert.Equal("Attack on Titan", item.Effective.Title);

        var renamed = Path.Combine(dir.Path, "Attack on Titan - S00E02 - The Sudden Visitor.mkv");
        File.Move(item.Path, renamed);
        library.UpdatePath(item, renamed);
        Assert.Equal(NameSource.FileName, item.NameSource);
        Assert.Equal((0, 2), (item.Parsed.Season, item.Parsed.Episode));
    }

    [Fact]
    public void A_file_with_a_meaningless_name_is_unidentified()
    {
        using var dir = new TempDir();
        var item = new LibraryService().AddPaths([dir.CreateFile("a8f3c1.mkv")])[0];
        Assert.True(item.IsUnidentified);
    }

    [Fact]
    public void Specials_can_be_renamed_from_the_episode_list()
    {
        using var dir = new TempDir();
        var library = new LibraryService();
        var item = library.AddPaths([dir.CreateFile(EncodedOva + ".mkv")])[0];
        var episodes = new[]
        {
            new TvEpisode(1, 1, "To You, 2000 Years in the Future", null),
            new TvEpisode(0, 2, "The Sudden Visitor", null),
        };

        var proposal = new RenameService(library).BuildProposals(
            [item], "Attack on Titan", episodes, "{Show} - S{Season}E{Episode} - {Title}", ["Shingeki no Kyojin"])[0];
        Assert.Null(proposal.Problem);
        Assert.Equal("Attack on Titan - S00E02 - The Sudden Visitor.mkv", proposal.NewName);
    }

    /// <summary>A minimal Matroska file: EBML header, then a Segment holding a Void, an Info (with the title) and a Cluster.</summary>
    private static byte[] Matroska(string? title)
    {
        var info = new List<byte>();
        info.AddRange(Element([0x2A, 0xD7, 0xB1], [0x0F, 0x42, 0x40])); // TimestampScale
        if (title is not null)
            info.AddRange(Element([0x7B, 0xA9], Encoding.UTF8.GetBytes(title)));
        info.AddRange(Element([0x4D, 0x80], Encoding.UTF8.GetBytes("test muxer")));

        var segment = new List<byte>();
        segment.AddRange(Element([0xEC], new byte[300])); // Void, with a two-byte size
        segment.AddRange(Element([0x15, 0x49, 0xA9, 0x66], [.. info]));
        segment.AddRange(Element([0x1F, 0x43, 0xB6, 0x75], new byte[16]));

        var file = new List<byte>();
        file.AddRange(Element([0x1A, 0x45, 0xDF, 0xA3], Element([0x42, 0x82], Encoding.ASCII.GetBytes("matroska"))));
        file.AddRange([0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // Segment, unknown size
        file.AddRange(segment);
        return [.. file];
    }

    private static byte[] Element(byte[] id, byte[] payload)
    {
        byte[] size = payload.Length < 127
            ? [(byte)(0x80 | payload.Length)]
            : [(byte)(0x40 | (payload.Length >> 8)), (byte)payload.Length];
        return [.. id, .. size, .. payload];
    }
}
