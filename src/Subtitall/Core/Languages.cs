namespace Subtitall.Core;

/// <summary>Language catalog (OpenSubtitles-style lowercase codes) and per-provider code mapping.</summary>
public static class Languages
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("en", "English"), ("fr", "French"), ("es", "Spanish"), ("de", "German"),
        ("it", "Italian"), ("pt-pt", "Portuguese"), ("pt-br", "Portuguese (Brazil)"),
        ("nl", "Dutch"), ("pl", "Polish"), ("ru", "Russian"), ("uk", "Ukrainian"),
        ("ja", "Japanese"), ("ko", "Korean"), ("zh-cn", "Chinese (Simplified)"),
        ("zh-tw", "Chinese (Traditional)"), ("ar", "Arabic"), ("tr", "Turkish"),
        ("sv", "Swedish"), ("no", "Norwegian"), ("da", "Danish"), ("fi", "Finnish"),
        ("cs", "Czech"), ("sk", "Slovak"), ("el", "Greek"), ("he", "Hebrew"),
        ("hu", "Hungarian"), ("ro", "Romanian"), ("bg", "Bulgarian"), ("hr", "Croatian"),
        ("sr", "Serbian"), ("sl", "Slovenian"), ("id", "Indonesian"), ("ms", "Malay"),
        ("th", "Thai"), ("vi", "Vietnamese"), ("hi", "Hindi"), ("fa", "Persian"),
        ("et", "Estonian"), ("lv", "Latvian"), ("lt", "Lithuanian"),
    ];

    /// <summary>Maps an internal code to SubDL's uppercase scheme (EN, FR, BR_PT, ZH_BG…).</summary>
    public static string ToSubDl(string code) => code switch
    {
        "pt-br" => "BR_PT",
        "pt-pt" => "PT",
        "zh-cn" => "ZH",
        "zh-tw" => "ZH_BG",
        _ => code.ToUpperInvariant(),
    };

    /// <summary>Maps a SubDL code back to the internal one.</summary>
    public static string FromSubDl(string code) => code.ToUpperInvariant() switch
    {
        "BR_PT" => "pt-br",
        "PT" => "pt-pt",
        "ZH" => "zh-cn",
        "ZH_BG" => "zh-tw",
        _ => code.ToLowerInvariant(),
    };

    /// <summary>
    /// Maps an internal code to SubSource's lowercase language names
    /// ("farsi_persian", "brazilian_portuguese", "chinese_bg_code"…).
    /// </summary>
    public static string ToSubSource(string code) => code switch
    {
        "pt-br" => "brazilian_portuguese",
        "pt-pt" => "portuguese",
        "zh-cn" or "zh-tw" => "chinese_bg_code",
        "fa" => "farsi_persian",
        "el" => "greek",
        "he" => "hebrew",
        "ja" => "japanese",
        "ko" => "korean",
        "ar" => "arabic",
        _ => NameOf(code).ToLowerInvariant(),
    };

    private static string NameOf(string code) =>
        All.FirstOrDefault(l => l.Code == code).Name ?? code;
}
