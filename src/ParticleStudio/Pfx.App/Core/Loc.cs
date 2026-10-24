using System.Globalization;
using System.Windows;

namespace Pfx.App.Core;

// The NDZ localisation pattern of Suite.Ui (Core/Loc.cs: Lang/<lang>.xaml merged into the application resources,
// controls bind with DynamicResource) with English added; Suite.Ui's copy accepts only ru and uk (owner decision
// 2026-10-15: a local copy, Suite.Ui unchanged).
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
        var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NDZ_ParticleStudio;component/Lang/{lang}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dict != null)
            merged.Remove(_dict);
        merged.Add(d);
        _dict = d;
        Lang = lang;
        Culture = new CultureInfo(lang switch { "uk" => "uk-UA", "en" => "en-GB", _ => "ru-RU" });
        // AvalonDock's own button hints follow the UI culture (it ships ru, not uk: Ukrainian falls back to English).
        CultureInfo.CurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        Changed?.Invoke();
    }

    public static string T(string key) => TryT(key) ?? key;

    // Without a WPF application (unit tests) there are no dictionaries: keys come back as they are.
    public static string? TryT(string key) => Application.Current?.TryFindResource(key) as string;

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    public static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
