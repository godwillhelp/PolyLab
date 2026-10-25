using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PolyLab.Launcher.Core;
using PolyLab.Ui.Core;

namespace PolyLab.Launcher.Tests;

// The launcher's and PBOG Packer's dictionaries (Particle Studio has its own check in Pfx.Tests) and the language a
// first start picks from the Windows display language.
public class LangTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static Dictionary<string, string> Dict(string app, string lang) =>
        Regex.Matches(File.ReadAllText(Path.Combine(Repo(), "src", app, "Lang", lang + ".xaml")), "x:Key=\"([^\"]+)\">([^<]*)<")
            .ToDictionary(m => m.Groups[1].Value, m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));

    [Theory]
    [InlineData("Launcher")]
    [InlineData("PBOGPacker")]
    public void The_three_languages_have_the_same_keys_and_placeholders(string app)
    {
        var d = UiLanguage.All.ToDictionary(l => l, l => Dict(app, l));
        var keys = d["ru"].Keys.Order().ToList();
        Assert.True(keys.Count > 50);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        foreach (var lang in UiLanguage.All)
        {
            Assert.Equal(keys, d[lang].Keys.Order().ToList());
            foreach (var k in keys)
                Assert.True(Holes(d["ru"][k]) == Holes(d[lang][k]), lang + " " + k);
        }
        foreach (var (k, v) in d["en"])
            Assert.False(Regex.IsMatch(v, @"\p{IsCyrillic}"), "en " + k);
    }

    [Theory]
    [InlineData("uk-UA", "uk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru-UA", "ru")]
    [InlineData("be-BY", "ru")]
    [InlineData("kk-KZ", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("pl-PL", "en")]
    public void A_first_start_follows_the_display_language(string culture, string lang)
    {
        Assert.Equal(lang, UiLanguage.For(new CultureInfo(culture)));
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            Assert.Equal(lang, new Settings().Lang);
        }
        finally { CultureInfo.CurrentUICulture = was; }
    }

    [Fact]
    public void A_saved_language_wins_over_the_display_language()
    {
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("uk", System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"Lang\":\"uk\"}")!.Lang);
        }
        finally { CultureInfo.CurrentUICulture = was; }
    }

    [Fact]
    public void The_switch_cycles_through_all_three()
    {
        Assert.Equal("ru", UiLanguage.Next("en"));
        Assert.Equal("uk", UiLanguage.Next("ru"));
        Assert.Equal("en", UiLanguage.Next("uk"));
    }
}
