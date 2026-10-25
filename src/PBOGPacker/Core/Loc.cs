using System.Collections;
using System.Globalization;
using System.Windows;
using PolyLab.Ui.Core;

namespace PolyLab.PBOGPacker.Core;

public static class Loc
{
    private static ResourceDictionary? _dict;
    private static Dictionary<string, string> _text = new();

    public static string Lang { get; private set; } = "ru";
    public static CultureInfo Culture { get; private set; } = new("ru-RU");
    public static event Action? Changed;

    public static void Set(string lang)
    {
        if (!UiLanguage.All.Contains(lang)) lang = UiLanguage.Default;
        var d = new ResourceDictionary { Source = new Uri($"pack://application:,,,/PBOG_Packer;component/Lang/{lang}.xaml") };
        var text = new Dictionary<string, string>();
        foreach (DictionaryEntry e in d)
            if (e.Key is string k && e.Value is string v) text[k] = v;
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dict != null) merged.Remove(_dict);
        merged.Add(d);
        _dict = d;
        _text = text;
        Lang = lang;
        Culture = UiLanguage.Culture(lang);
        Changed?.Invoke();
    }

    // The pipeline writes its log lines and errors on worker threads, so texts come from a copy made on the UI thread,
    // not from the application resources.
    public static string T(string key) => _text.TryGetValue(key, out var v) ? v : key;

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
