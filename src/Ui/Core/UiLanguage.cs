using System.Globalization;

namespace PolyLab.Ui.Core;

// Interface languages of every Poly Lab program (linked into PBOG Packer, which does not reference PolyLab.Ui).
// Until a language is saved in a program's settings it follows the Windows display language: Ukrainian gets
// Ukrainian, Russian, Belarusian and Kazakh get Russian, any other language gets English.
public static class UiLanguage
{
    public static readonly string[] All = { "en", "ru", "uk" };

    public static string Default => For(CultureInfo.CurrentUICulture);

    public static string For(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "uk" => "uk",
        "ru" or "be" or "kk" => "ru",
        _ => "en",
    };

    public static string Next(string lang) => All[(Array.IndexOf(All, lang) + 1) % All.Length];

    public static CultureInfo Culture(string lang) => new(lang switch { "uk" => "uk-UA", "ru" => "ru-RU", _ => "en-GB" });
}
