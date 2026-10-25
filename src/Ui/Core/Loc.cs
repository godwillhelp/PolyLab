using System.Globalization;
using System.Windows;

namespace PolyLab.Ui.Core;

public static class Loc
{
    private static ResourceDictionary? _dict;

    public static string Lang { get; private set; } = "ru";
    public static CultureInfo Culture { get; private set; } = new("ru-RU");
    public static event Action? Changed;

    public static void Set(string lang)
    {
        if (lang != "ru" && lang != "uk") lang = "ru";
        var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lang/{lang}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dict != null) merged.Remove(_dict);
        merged.Add(d);
        _dict = d;
        Lang = lang;
        Culture = new CultureInfo(lang == "uk" ? "uk-UA" : "ru-RU");
        Changed?.Invoke();
    }

    public static string T(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    public static string Sec(long ms) => (ms / 1000.0).ToString("0.0", Culture) + " " + T("U.S");

    public static string Kb(long bytes) => (bytes / 1024.0).ToString("0.#", Culture) + " " + T("U.Kb");

    public static string Ago(DateTime? at)
    {
        if (at == null) return "";
        var d = DateTime.Now - at.Value;
        if (d.TotalMinutes < 1) return T("Ago.Now");
        if (d.TotalHours < 1) return F("Ago.M", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return F("Ago.H", (int)d.TotalHours);
        return F("Ago.D", (int)d.TotalDays);
    }

    public static string Clock(double ms)
    {
        var s = Math.Max(0, ms) / 1000.0;
        var m = (int)(s / 60);
        return $"{m:00}:{(s % 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    public static string LogTime(double ms)
    {
        var s = ms / 1000.0;
        var m = (int)(s / 60);
        return $"{m:00}:{(s % 60).ToString("00.000", CultureInfo.InvariantCulture)}";
    }
}
