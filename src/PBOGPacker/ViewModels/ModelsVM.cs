using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using PolyLab.PBOGPacker.Core;
using PolyLab.PBOGPacker.Core.P3d;

namespace PolyLab.PBOGPacker.ViewModels;

public sealed record LodRowVM(string Name, int Vertices, int Faces, int Sections, int Selections, int Proxies);

public sealed class ModelItemVM : ObservableObject
{
    private OdolModel? _model;
    private string? _error;

    public ModelItemVM(string file, string root)
    {
        File = file;
        Name = Path.GetFileNameWithoutExtension(file);
        var dir = Path.GetDirectoryName(file) ?? "";
        Folder = Directory.Exists(root) ? Path.GetRelativePath(root, dir) : dir;
        if (Folder == ".") Folder = "";
        Size = Loc.Kb(new FileInfo(file).Length);
    }

    public string File { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Size { get; }
    public bool IsRead => _model != null || _error != null;

    public OdolModel? Model
    {
        get => _model;
        set { _model = value; RaiseAll(); }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; RaiseAll(); }
    }

    public bool HasModel => _model != null;
    public bool HasError => _error != null;
    public string Head => _model == null ? "" : Loc.F("Mdl.Head", _model.Version, Size, _model.Lods.Length);
    public string Skeleton => _model == null ? "" : _model.Skeleton.Length == 0 ? Loc.T("Mdl.NoSkeleton")
        : _model.Skeleton + "  ·  " + Loc.F("Mdl.Bones", _model.Bones.Length);
    public string Mass => _model == null ? "" : Loc.F("Mdl.MassVal", _model.Mass.ToString("0.###", Loc.Culture));
    public string Com => _model == null ? "" : string.Join("  ", new[] { _model.CenterOfMass.X, _model.CenterOfMass.Y, _model.CenterOfMass.Z }
        .Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

    public IReadOnlyList<LodRowVM> Lods => _model?.Lods.Select(l => new LodRowVM(Odol.LodName(l.Resolution), l.Positions.Length,
        l.Faces.Length, l.Sections.Length, l.Selections.Length, l.Proxies.Length)).ToList() ?? [];

    public IReadOnlyList<string> List(string which)
    {
        if (_model == null) return [];
        var lods = _model.Lods;
        IEnumerable<string> items = which switch
        {
            "mat" => lods.SelectMany(l => l.Materials).Select(m => m.Path),
            "sel" => lods.SelectMany(l => l.Selections).Select(s => s.Name),
            "bone" => _model.Bones.Select(b => b.Parent.Length > 0 ? b.Name + "  ←  " + b.Parent : b.Name),
            "anim" => _model.Animations.Select(a => a.Name + "  ·  " + a.Source),
            _ => lods.SelectMany(l => l.Textures.Concat(l.Materials.SelectMany(m => m.Stages.Select(s => s.Texture)))),
        };
        return items.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// The Models page: binarized models of a file or folder, their details, and conversion to MLOD / model.cfg / rvmat
// beside each model (<name>_mlod) or under another folder. Reading and writing run off the UI thread.
public sealed class ModelsVM : ObservableObject
{
    private readonly IGraftBackend _backend;
    private string _source = "", _outDir = "", _list = "tex", _status = "";
    private ModelItemVM? _selected;
    private bool _busy;
    private string? _lastOut;

    public ModelsVM(IGraftBackend backend)
    {
        _backend = backend;
        PickFileCmd = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "p3d|*.p3d" };
            return dlg.ShowDialog() == true ? OpenAsync(dlg.FileName) : Task.CompletedTask;
        }, _ => !_busy);
        PickFolderCmd = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            return dlg.ShowDialog() == true ? OpenAsync(dlg.FolderName) : Task.CompletedTask;
        }, _ => !_busy);
        OpenSourceCmd = new RelayCommand(_ => OpenAsync(_source.Trim().Trim('"')), _ => !_busy && _source.Trim().Length > 0);
        PickOutCmd = new RelayCommand(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (Directory.Exists(_outDir)) dlg.InitialDirectory = _outDir;
            if (dlg.ShowDialog() == true) OutDir = dlg.FolderName;
        }, _ => !_busy);
        ResetOutCmd = new RelayCommand(_ => OutDir = "", _ => _outDir.Length > 0 && !_busy);
        ExportCmd = new RelayCommand(p => ExportAsync(Parts(p as string)), _ => !_busy && _selected is { HasModel: true });
        ExportAllCmd = new RelayCommand(_ => ExportAllAsync(), _ => !_busy && Items.Count > 0);
        OpenOutCmd = new RelayCommand(_ => _lastOut != null ? _backend.OpenPathAsync(_lastOut) : Task.CompletedTask, _ => _lastOut != null);
    }

    public ObservableCollection<ModelItemVM> Items { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    public ICommand PickFileCmd { get; }
    public ICommand PickFolderCmd { get; }
    public ICommand OpenSourceCmd { get; }
    public ICommand PickOutCmd { get; }
    public ICommand ResetOutCmd { get; }
    public ICommand ExportCmd { get; }
    public ICommand ExportAllCmd { get; }
    public ICommand OpenOutCmd { get; }

    public string Source { get => _source; set => Set(ref _source, value); }
    public string OutDir
    {
        get => _outDir;
        set { if (Set(ref _outDir, value)) Raise(nameof(OutText)); }
    }
    public string OutText => _outDir.Length > 0 ? _outDir : Loc.T("Mdl.OutBeside");
    // Commands re-check what they may do only on input; work that ends on its own has to ask.
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasItems => Items.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasOut => _lastOut != null;

    public string ListKind
    {
        get => _list;
        set { if (Set(ref _list, value)) Raise(nameof(CurrentList)); }
    }
    public IReadOnlyList<string> CurrentList => _selected?.List(_list) ?? [];

    public ModelItemVM? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(CurrentList));
            if (value is { IsRead: false }) _ = ReadAsync(value);
        }
    }

    public void Refresh()
    {
        RaiseAll();
        _selected?.RaiseAll();
    }

    public async Task OpenAsync(string path)
    {
        Source = path;
        Items.Clear();
        Notes.Clear();
        Selected = null;
        IsBusy = true;
        Status = Loc.T("Mdl.Scanning");
        try
        {
            var files = await Task.Run(() => Find(path));
            foreach (var f in files) Items.Add(new ModelItemVM(f, path));
            Status = files.Count == 0 ? Loc.F("Mdl.NoneFound", path) : Loc.F("Mdl.Count", files.Count);
            Selected = Items.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasItems));
            Raise(nameof(HasNotes));
        }
    }

    private static List<string> Find(string path)
    {
        var files = System.IO.File.Exists(path) ? [path]
            : Directory.Exists(path) ? Directory.GetFiles(path, "*.p3d", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        return files.Where(IsOdol).ToList();
    }

    private static bool IsOdol(string file)
    {
        try
        {
            var head = new byte[4];
            using var fs = System.IO.File.OpenRead(file);
            return fs.Read(head, 0, 4) == 4 && Odol.IsOdol(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task ReadAsync(ModelItemVM item)
    {
        try { item.Model = await Task.Run(() => Odol.Read(item.File)); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { item.Error = e.Message; }
        if (item == _selected) Raise(nameof(CurrentList));
        CommandManager.InvalidateRequerySuggested();
    }

    private static ModelExport.Parts Parts(string? p) => p switch
    {
        "cfg" => ModelExport.Parts.Cfg,
        "rvmat" => ModelExport.Parts.Rvmats,
        _ => ModelExport.Parts.All,
    };

    private string? OutFor(ModelItemVM item) => _outDir.Length > 0 ? ModelExport.OutUnder(_outDir, item.File) : null;

    private async Task ExportAsync(ModelExport.Parts parts)
    {
        if (_selected is not { } item) return;
        IsBusy = true;
        Notes.Clear();
        try
        {
            var r = await Task.Run(() => ModelExport.Export(item.File, OutFor(item), parts));
            foreach (var n in r.Notes) Notes.Add(n);
            _lastOut = OutFor(item) ?? ModelExport.DefaultOut(item.File);
            Status = Loc.F("Mdl.Written", r.Written.Count, _lastOut);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("Mdl.Failed", e.Message);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasNotes));
            Raise(nameof(HasOut));
        }
    }

    private async Task ExportAllAsync()
    {
        IsBusy = true;
        Notes.Clear();
        var items = Items.ToList();
        int done = 0, broken = 0;
        try
        {
            foreach (var item in items)
            {
                Status = Loc.F("Mdl.Progress", done + broken + 1, items.Count);
                try
                {
                    var r = await Task.Run(() => ModelExport.Export(item.File, OutFor(item), ModelExport.Parts.All));
                    foreach (var n in r.Notes) Notes.Add(item.Name + ": " + n);
                    done++;
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Notes.Add(item.Name + ": " + e.Message);
                    broken++;
                }
            }
            _lastOut = _outDir.Length > 0 ? _outDir : items.Count == 1 ? ModelExport.DefaultOut(items[0].File) : Directory.Exists(_source) ? _source : null;
            Status = Loc.F("Mdl.AllDone", done, items.Count, broken);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasNotes));
            Raise(nameof(HasOut));
        }
    }
}
