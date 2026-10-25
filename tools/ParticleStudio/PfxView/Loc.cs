using System.Globalization;
using System.Windows;

namespace PfxView;

// The localisation pattern of PolyLab.Ui (Core/Loc.cs: Lang/<lang>.xaml merged into the application resources,
// controls bind with DynamicResource), with English added; PolyLab.Ui's copy accepts only ru and uk.
public static class Loc
{
    public static readonly string[] Languages = { "ru", "en", "uk" };
    private static ResourceDictionary? _dict;

    public static string Lang { get; private set; } = "ru";
    public static CultureInfo Culture { get; private set; } = new("ru-RU");
    public static event Action? Changed;

    public static void Set(string lang)
    {
        if (!Languages.Contains(lang))
            lang = "ru";
        var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lang/{lang}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dict != null)
            merged.Remove(_dict);
        merged.Add(d);
        _dict = d;
        Lang = lang;
        Culture = new CultureInfo(lang switch { "uk" => "uk-UA", "en" => "en-GB", _ => "ru-RU" });
        Changed?.Invoke();
    }

    public static string T(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);
}
