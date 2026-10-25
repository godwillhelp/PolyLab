using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using PolyLab.PBOGPacker.Core;

namespace PolyLab.PBOGPacker.ViewModels;

public sealed class WorkshopItemVM : ObservableObject
{
    private bool _queued;

    public WorkshopItemVM(WorkshopMod mod) => Mod = mod;

    public WorkshopMod Mod { get; }
    public string Name => Mod.Name;
    public string Path => Mod.Path;
    public string Sub => (Mod.Id != null ? "id " + Mod.Id + "  ·  " : "") + (Mod.Keys > 0 ? Loc.F("Wm.Keys", Mod.Keys) : Loc.T("Wm.NoKeys"));
    public bool IsQueued { get => _queued; set => Set(ref _queued, value); }
    public string FolderTip => Loc.F("Wm.FolderOpenTip", Path);
}

public sealed class QueueItemVM : ObservableObject
{
    private int _index;
    private bool _dragging;

    public QueueItemVM(string name, string text, string full, string? alt, string kind)
    {
        Name = name;
        Text = text;
        Full = full;
        Alt = alt;
        Kind = kind;
    }

    public string Name { get; }
    public string Text { get; }
    public string Full { get; }
    public string? Alt { get; }
    public string Kind { get; }
    public string KindText => Loc.T("Wm.Kind." + Kind);
    public string KindBrush => Kind switch { "ws" => "ok", "local" => "warn", _ => "err" };
    public int Index { get => _index; set { if (Set(ref _index, value)) Raise(nameof(Number)); } }
    public string Number => Index.ToString("00");
    public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }

    // A mod named in the script whose folder is gone has nothing to open.
    private bool? _exists;
    public bool Exists => _exists ??= Kind != "missing" && System.IO.Directory.Exists(Full);
    public string FolderTip => Loc.F(Exists ? "Wm.FolderOpenTip" : "Wm.FolderMissing", Full);
}

// The "connect mods" dialog of one server: workshop mods and the server's own @folders on the left,
// the -mod and -serverMod load orders on the right.
public sealed class ModsVM : ObservableObject
{
    private readonly IGraftBackend _backend;
    private readonly MainViewModel _root;
    private string _search = "";
    private string _workshopDir = "";
    private string _source = "ws";
    private string _target = "mod";
    private string? _error, _warning, _saveError;
    private bool _loading = true, _saving, _busy, _delayed;

    public ModsVM(IGraftBackend backend, MainViewModel root, ServerVM server, string bat)
    {
        _backend = backend;
        _root = root;
        Server = server;
        Bat = bat;
        AvailableView = CollectionViewSource.GetDefaultView(Available);
        AvailableView.Filter = Match;
        LocalView = CollectionViewSource.GetDefaultView(Local);
        LocalView.Filter = Match;
        Queue.CollectionChanged += (_, _) => Changed();
        ServerQueue.CollectionChanged += (_, _) => Changed();

        AddCmd = new RelayCommand(p => Add(p as WorkshopItemVM));
        RemoveCmd = new RelayCommand(p => Remove(p as QueueItemVM));
        SwapCmd = new RelayCommand(p => Swap(p as QueueItemVM));
        ClearCmd = new RelayCommand(_ => { foreach (var q in ActiveQueue.ToList()) Remove(q); }, _ => ActiveQueue.Count > 0 && !Saving);
        SaveCmd = new RelayCommand(_ => SaveAsync(), _ => CanSave);
        CloseCmd = new RelayCommand(_ => _root.WorkshopOpen = false, _ => !Saving);
        BrowseCmd = new RelayCommand(_ => BrowseAsync(), _ => !Saving);
        FolderCmd = new RelayCommand(_ => PickFolder(), _ => !Saving && !IsLoading);
        RefreshCmd = new RelayCommand(_ => RefreshAsync(), _ => !Saving && !IsLoading);
        OpenCmd = new RelayCommand(p => p switch
        {
            WorkshopItemVM w => _backend.OpenPathAsync(w.Path),
            QueueItemVM { Exists: true } q => _backend.OpenPathAsync(q.Full),
            _ => Task.CompletedTask,
        }, p => p is WorkshopItemVM or QueueItemVM { Exists: true });
    }

    private bool Match(object o) => o is WorkshopItemVM w && (_search.Length == 0 || w.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase));

    public ServerVM Server { get; }
    public string Bat { get; }
    public string Sub => Bat + "  ·  " + Server.Name;
    public ObservableCollection<WorkshopItemVM> Available { get; } = new();
    public ICollectionView AvailableView { get; }
    public ObservableCollection<WorkshopItemVM> Local { get; } = new();
    public ICollectionView LocalView { get; }
    public ObservableCollection<QueueItemVM> Queue { get; } = new();
    public ObservableCollection<QueueItemVM> ServerQueue { get; } = new();

    public ICommand AddCmd { get; }
    public ICommand RemoveCmd { get; }
    public ICommand SwapCmd { get; }
    public ICommand ClearCmd { get; }
    public ICommand SaveCmd { get; }
    public ICommand CloseCmd { get; }
    public ICommand BrowseCmd { get; }
    public ICommand FolderCmd { get; }
    public ICommand RefreshCmd { get; }
    public ICommand OpenCmd { get; }

    public event Action<QueueItemVM>? Added;

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            AvailableView.Refresh();
            LocalView.Refresh();
            RaiseSource();
        }
    }

    // Left: "ws" — the workshop folder, "local" — the @folders of the server.
    public string Source
    {
        get => _source;
        set { if (Set(ref _source, value == "local" ? "local" : "ws")) RaiseSource(); }
    }

    public ICollectionView SourceView => _source == "local" ? LocalView : AvailableView;
    public bool IsWorkshop => _source == "ws";

    // Right: "mod" — the -mod list, "server" — the -serverMod list; "+" on the left adds to it.
    public string Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value == "server" ? "server" : "mod")) return;
            Raise(nameof(ActiveQueue));
            Raise(nameof(IsServerTarget));
            Raise(nameof(QueueHint));
            Raise(nameof(EmptyText));
            Raise(nameof(SwapTip));
            Raise(nameof(SwapIcon));
            Raise(nameof(AddTip));
            Changed();
        }
    }

    public ObservableCollection<QueueItemVM> ActiveQueue => _target == "server" ? ServerQueue : Queue;
    public bool IsServerTarget => _target == "server";
    public string QueueHint => Loc.T(IsServerTarget ? "Wm.QueueHintSrv" : "Wm.QueueHint");
    public string EmptyText => Loc.T(IsServerTarget ? "Wm.EmptySrv" : "Wm.Empty");
    public string SwapTip => Loc.T(IsServerTarget ? "Wm.ToClient" : "Wm.ToServer");
    public string SwapIcon => IsServerTarget ? "mods" : "servers";
    public string AddTip => Loc.T(IsServerTarget ? "Wm.AddTipSrv" : "Wm.AddTip");

    public string WorkshopDir { get => _workshopDir; private set { if (Set(ref _workshopDir, value)) Raise(nameof(NoWorkshop)); } }
    public bool NoWorkshop => !IsLoading && IsWorkshop && Available.Count == 0;
    public bool NoLocal => !IsLoading && !IsWorkshop && Local.Count == 0;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Requery(); } } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string? Warning { get => _warning; private set { if (Set(ref _warning, value)) Raise(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public string? SaveError { get => _saveError; private set { if (Set(ref _saveError, value)) Raise(nameof(HasSaveError)); } }
    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);
    public bool ScriptBusy { get => _busy; private set { if (Set(ref _busy, value)) Requery(); } }
    public bool ServerUp => Server.IsRunning && !ScriptBusy;
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) { RaiseSource(); Requery(); } } }
    public bool Saving { get => _saving; private set { if (Set(ref _saving, value)) Requery(); } }
    public bool CanSave => !IsLoading && !Saving && !HasError && !ScriptBusy;

    private void Requery()
    {
        Raise(nameof(CanSave));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseSource()
    {
        Raise(nameof(SourceView));
        Raise(nameof(IsWorkshop));
        Raise(nameof(NoWorkshop));
        Raise(nameof(NoLocal));
        Raise(nameof(NoMatch));
        Raise(nameof(WorkshopTab));
        Raise(nameof(LocalTab));
    }

    public bool NoMatch => !IsLoading && (IsWorkshop ? Available.Count : Local.Count) > 0 && !SourceView.Cast<object>().Any();
    public string WorkshopTab => Loc.F("Wm.SrcWs", Available.Count);
    public string LocalTab => Loc.F("Wm.SrcLocal", Local.Count);
    public string ClientTab => Loc.F("Wm.TabClient", Queue.Count);
    public string ServerTab => Loc.F("Wm.TabServer", ServerQueue.Count);
    public bool QueueEmpty => !IsLoading && ActiveQueue.Count == 0;
    public string SaveText => Loc.F("Wm.Save", Bat);

    // What the start script will get, spelled the way Save will spell it; the chosen list comes first
    // so a long -mod does not push -serverMod out of sight.
    public string Preview => IsServerTarget
        ? Line("-serverMod", ServerQueue) + "\n" + Line("-mod", Queue)
        : Line("-mod", Queue) + "\n" + Line("-serverMod", ServerQueue);

    private string Line(string key, IEnumerable<QueueItemVM> list)
    {
        var items = list.ToList();
        if (items.Count == 0) return Loc.F("Wm.PreviewNone", key);
        return ServerMods.Token(key, string.Join(";", items.Select(q =>
            ServerMods.Written(Server.Path, ServerMods.Spell(new ModPick(q.Text, q.Alt), _delayed) ?? q.Text).Replace("%", "%%"))));
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var st = await _backend.GetServerModsAsync(Server.Id, Bat);
            Fill(st);
            Queue.Clear();
            foreach (var e in st.Current) Queue.Add(Item(e));
            ServerQueue.Clear();
            foreach (var e in st.CurrentServer ?? Array.Empty<ModEntry>()) ServerQueue.Add(Item(e));
            // A script whose only list is -serverMod opens on it.
            if (Queue.Count == 0 && ServerQueue.Count > 0) Target = "server";
            Error = st.Error;
            ScriptBusy = st.ScriptBusy;
            _delayed = st.Delayed;
            Raise(nameof(ServerUp));
        }
        catch (Exception e) { Error = e.Message; }
        finally { Done(); }
    }

    private void Fill(ModsSetup st)
    {
        WorkshopDir = st.WorkshopDir;
        Warning = st.Warning;
        Available.Clear();
        foreach (var m in st.Available) Available.Add(new WorkshopItemVM(m));
        Local.Clear();
        foreach (var m in st.Local ?? Array.Empty<WorkshopMod>()) Local.Add(new WorkshopItemVM(m));
    }

    private void Done()
    {
        IsLoading = false;
        Mark();
        AvailableView.Refresh();
        LocalView.Refresh();
        Changed();
        RaiseSource();
    }

    private QueueItemVM Item(ModEntry e)
    {
        var w = e.WorkshopPath != null ? Find(e.WorkshopPath) : null;
        var kind = !e.Exists ? "missing" : w != null && Available.Contains(w) ? "ws" : "local";
        return new QueueItemVM(e.Name, e.Text, w?.Path ?? e.Full, w?.Mod.Target, kind);
    }

    private WorkshopItemVM? Find(string full) => Available.Concat(Local).FirstOrDefault(a => Is(a, full));

    private static bool Is(WorkshopItemVM a, string full) =>
        Same(a.Mod.Path, full) || (a.Mod.Target != null && Same(a.Mod.Target, full));

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private bool Listed(string full, string? alt) => Queue.Concat(ServerQueue).Any(q =>
        Same(q.Full, full) || (alt != null && Same(q.Full, alt)) || (q.Alt != null && (Same(q.Alt, full) || (alt != null && Same(q.Alt, alt)))));

    // A mod goes to one list only: the tick on the left means "already in -mod or in -serverMod".
    private void Mark()
    {
        foreach (var w in Available.Concat(Local))
            w.IsQueued = Queue.Concat(ServerQueue).Any(q => Is(w, q.Full) || (q.Alt != null && Is(w, q.Alt)));
    }

    // Added in the order of the clicks: every new mod goes to the end of the chosen list.
    private void Add(WorkshopItemVM? w)
    {
        if (w == null || w.IsQueued || Saving) return;
        Push(new QueueItemVM(w.Name, w.Mod.Path, w.Mod.Path, w.Mod.Target, Available.Contains(w) ? "ws" : "local"));
    }

    private void Push(QueueItemVM q)
    {
        ActiveQueue.Add(q);
        Mark();
        Added?.Invoke(q);
    }

    private void Remove(QueueItemVM? q)
    {
        if (q == null || Saving) return;
        if (!Queue.Remove(q)) ServerQueue.Remove(q);
        Mark();
    }

    // Client <-> server: the mod goes to the end of the other list.
    private void Swap(QueueItemVM? q)
    {
        if (q == null || Saving) return;
        if (Queue.Remove(q)) ServerQueue.Add(q);
        else if (ServerQueue.Remove(q)) Queue.Add(q);
    }

    public void Move(QueueItemVM item, QueueItemVM target)
    {
        var list = ActiveQueue;
        int from = list.IndexOf(item), to = list.IndexOf(target);
        if (from < 0 || to < 0 || from == to || Saving) return;
        list.Move(from, to);
    }

    private void Changed()
    {
        for (var i = 0; i < Queue.Count; i++) Queue[i].Index = i + 1;
        for (var i = 0; i < ServerQueue.Count; i++) ServerQueue[i].Index = i + 1;
        Raise(nameof(Preview));
        Raise(nameof(ClientTab));
        Raise(nameof(ServerTab));
        Raise(nameof(QueueEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;
        Saving = true;
        SaveError = null;
        try
        {
            var saved = await _backend.SaveServerModsAsync(Server.Id, Bat,
                Queue.Select(q => new ModPick(q.Text, q.Alt)).ToList(),
                ServerQueue.Select(q => new ModPick(q.Text, q.Alt)).ToList());
            var sub = Loc.F("T.ModsSavedSub", saved.Mods, saved.KeysCopied, saved.ServerMods);
            if (saved.NoKeys.Count > 0) sub += " · " + Loc.F("T.ModsNoKeys", string.Join(", ", saved.NoKeys));
            _root.Toast("ok", Loc.F("T.ModsSaved", Bat), sub);
            _root.WorkshopOpen = false;
        }
        catch (Exception e) { SaveError = e.Message; }
        finally { Saving = false; }
    }

    // Any mod folder (a server-side mod kept outside the server, a local build) into the chosen list.
    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Wm.FolderTitle") };
        if (System.IO.Directory.Exists(Server.Path)) dlg.InitialDirectory = Server.Path;
        if (dlg.ShowDialog() != true) return;
        var full = dlg.FolderName.TrimEnd('\\', '/');
        if (Find(full) is { } w)
        {
            if (!w.IsQueued) Add(w);
            return;
        }
        if (Listed(full, null)) return;
        Push(new QueueItemVM(System.IO.Path.GetFileName(full).TrimStart('@'), full, full, null, "local"));
    }

    // Re-reads the folders on the left; the lists on the right stay as edited.
    private Task RefreshAsync() => ReloadLeftAsync();

    private async Task BrowseAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Wm.Source") };
        if (System.IO.Directory.Exists(WorkshopDir)) dlg.InitialDirectory = WorkshopDir;
        if (dlg.ShowDialog() != true) return;
        await _root.SetWorkshopDirAsync(dlg.FolderName);
        await ReloadLeftAsync();
    }

    private async Task ReloadLeftAsync()
    {
        IsLoading = true;
        try { Fill(await _backend.GetServerModsAsync(Server.Id, Bat)); }
        catch (Exception e) { Warning = e.Message; }
        finally { Done(); }
    }
}
