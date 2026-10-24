using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pfx.App.Core;

namespace Pfx.App.Views;

public interface ISettingsHost
{
    AppSettings Settings { get; }
    void RootsChanged();
    void LanguageChanged(string lang);
    void ResetLayout();
    void ToolsChanged();
}

// Data roots, language and layout. Not a dialog: a panel like the others (spec 1.1 rule 8, no modal windows for edits).
public partial class SettingsView : UserControl
{
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
    }

    public ISettingsHost? Host { get; set; }

    public void Show(AppSettings s)
    {
        _loading = true;
        RootList.ItemsSource = null;
        RootList.ItemsSource = s.Roots;
        LangBox.Items.Clear();
        foreach (var lang in Loc.Languages)
            LangBox.Items.Add(new ComboBoxItem { Content = Loc.T("Lang." + lang), Tag = lang, ToolTip = Loc.T("Tip.Lang") });
        LangBox.SelectedItem = LangBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == Loc.Lang);
        FolderText.Text = Loc.F("Hint.SettingsFolder", AppSettings.Folder);
        RemoveRoot.IsEnabled = s.Roots.Count > 0;
        TexconvBox.Text = s.Texconv;
        PythonBox.Text = s.Python;
        UpscalerBox.Text = s.Upscaler;
        ModelBox.Text = s.UpscaleModel;
        XrayBox.Text = s.XrayScript;
        ImportBox.Text = s.ImportFolder;
        ConverterBox.Text = s.XrConverter;
        _loading = false;
    }

    private void OnToolKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            CommitTool((TextBox)sender);
    }

    private void OnToolLost(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitTool((TextBox)sender);

    private void CommitTool(TextBox box)
    {
        if (Host == null || _loading)
            return;
        var s = Host.Settings;
        string text = box.Text.Trim();
        string before = (string)box.Tag switch
        {
            "texconv" => s.Texconv, "python" => s.Python, "upscaler" => s.Upscaler, "xray" => s.XrayScript, "import" => s.ImportFolder,
            "converter" => s.XrConverter, _ => s.UpscaleModel,
        };
        if (text == before)
            return;
        switch ((string)box.Tag)
        {
            case "texconv": s.Texconv = text; break;
            case "python": s.Python = text; break;
            case "upscaler": s.Upscaler = text; break;
            case "xray": s.XrayScript = text; break;
            case "import": s.ImportFolder = text; break;
            case "converter": s.XrConverter = text; break;
            default: s.UpscaleModel = text; break;
        }
        Host.ToolsChanged();
    }

    // The model sits in esrgan_cpu.py's _esrgan\models folder in CG_AnomalyTools; any other place works as well.
    private void OnBrowseTool(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        string tag = (string)((FrameworkElement)sender).Tag;
        if (tag == "import")
        {
            var folder = new OpenFolderDialog { Title = Loc.T("Lbl.ImportFolder") };
            if (folder.ShowDialog(Window.GetWindow(this)) == true)
            {
                Host.Settings.ImportFolder = folder.FolderName;
                Host.ToolsChanged();
                Show(Host.Settings);
            }
            return;
        }
        var dialog = new OpenFileDialog
        {
            Filter = Loc.T(tag switch { "texconv" or "converter" => "Filter.Exe", "upscaler" or "xray" => "Filter.Py", _ => "Filter.Param" }),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var s = Host.Settings;
        switch (tag)
        {
            case "texconv": s.Texconv = dialog.FileName; break;
            case "upscaler":
                s.Upscaler = dialog.FileName;
                string model = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "_esrgan", "models", "realesrgan-x4plus.param");
                if (s.UpscaleModel.Length == 0 && File.Exists(model))
                    s.UpscaleModel = model;
                break;
            case "xray": s.XrayScript = dialog.FileName; break;
            case "converter": s.XrConverter = dialog.FileName; break;
            default: s.UpscaleModel = dialog.FileName; break;
        }
        Host.ToolsChanged();
        Show(s);
    }

    private void OnAddRoot(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        var dialog = new OpenFolderDialog { Title = Loc.T("Lbl.Roots") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        if (!Host.Settings.Roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            Host.Settings.Roots.Add(dialog.FolderName);
        Host.RootsChanged();
        Show(Host.Settings);
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (Host == null)
            return;
        var pick = RootList.SelectedItem as string ?? Host.Settings.Roots.LastOrDefault();
        if (pick == null)
            return;
        Host.Settings.Roots.Remove(pick);
        Host.RootsChanged();
        Show(Host.Settings);
    }

    private void OnLang(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangBox.SelectedItem is not ComboBoxItem { Tag: string lang } || lang == Loc.Lang)
            return;
        Host?.LanguageChanged(lang);
    }

    private void OnResetLayout(object sender, RoutedEventArgs e) => Host?.ResetLayout();
}
