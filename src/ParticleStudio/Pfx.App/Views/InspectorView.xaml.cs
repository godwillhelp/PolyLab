using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Pfx.App.Controls;
using Pfx.App.Core;
using Pfx.App.Inspector;

namespace Pfx.App.Views;

public partial class InspectorView : UserControl
{
    // Text a value box showed when it got focus: leaving the box commits only when the user typed something else.
    private readonly Dictionary<TextBox, string> _focusText = new();

    public InspectorView()
    {
        InitializeComponent();
    }

    public InspectorVm? Vm => DataContext as InspectorVm;

    // Context menu of every field (spec 1.1 rule 6), filled when it opens.
    public static Action<FieldVm, string>? CopyValue { get; set; }
    public static Func<string?>? PasteValue { get; set; }
    public static Action<FieldVm>? FindSame { get; set; }
    public static Action<FieldVm>? PushToGame { get; set; }
    public static Func<bool>? CanPushToGame { get; set; }

    private void OnBoxGot(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        _focusText[box] = box.Text;
        box.SelectAll();
    }

    private void OnBoxLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (_focusText.Remove(box, out var before) && before != box.Text)
            Commit(box);
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            _focusText[box] = box.Text;
            Commit(box);
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_focusText.TryGetValue(box, out var before))
                box.Text = before;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private static void Commit(TextBox box)
    {
        string text = box.Text;
        switch (box.DataContext)
        {
            case PairField p:
                (box.Tag as string == "rnd" ? p.Rnd : p.Base).Commit(text);
                break;
            case NumberField n:
                n.Commit(text);
                break;
            case VectorField v:
                v.Commit(int.Parse((string)box.Tag), text);
                break;
            case ColorField c:
                c.Commit(int.Parse((string)box.Tag), text);
                break;
            case PathField pf:
                pf.Commit(text);
                break;
            case TextField t:
                t.Commit(text);
                break;
        }
    }

    // Double-click on a label: caret into the first value box of the same row.
    private void OnScrubEdit(object? sender, EventArgs e)
    {
        DependencyObject? row = sender as DependencyObject;
        while (row != null && row is not Grid { ContextMenu: not null })
            row = VisualTreeHelper.GetParent(row);
        if (row != null && Descendants(row).OfType<TextBox>().FirstOrDefault() is { } box)
            box.Focus();
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ToggleField t)
            t.Toggle();
    }

    private void OnChoice(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ChoiceOption o)
        {
            o.IsOn = true;
            o.Raise();
        }
    }

    private void OnGraphOpen(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GraphField g)
            g.Open();
    }

    // Picks a file and writes it as a game path: relative to the data root it lies under, with forward slashes.
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PathField f || Vm == null)
            return;
        bool material = f.Key == "Material";
        var dialog = new OpenFileDialog
        {
            Filter = material ? Loc.T("Filter.Emat") : Loc.T("Filter.Texture"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        string? game = MainWindow.Current?.GamePathOf(dialog.FileName);
        if (game == null)
        {
            Vm.Say(Loc.F("Toast.OutsideRoots", dialog.FileName), "warn");
            return;
        }
        f.Commit(game);
    }

    private void OnRowMenu(object sender, ContextMenuEventArgs e)
    {
        var row = (FrameworkElement)sender;
        if (row.DataContext is not FieldVm f || row.ContextMenu is not { } menu)
            return;
        menu.Items.Clear();
        var vm = f.Owner;
        MenuItem Item(string key, Action run, bool enabled = true, string? tipKey = null, string? gesture = null)
        {
            var item = new MenuItem { Header = Loc.T(key), IsEnabled = enabled, InputGestureText = gesture ?? "" };
            item.ToolTip = Loc.T(tipKey ?? "Tip." + key);
            item.Click += (_, _) => run();
            return item;
        }
        bool anyOriginal = f.Targets.Any(t => t.Original != null);
        menu.Items.Add(Item("Ctx.ResetFile", () => ResetToFile(f), f.Modified && anyOriginal));
        menu.Items.Add(Item("Ctx.ResetGame", () => ResetToGame(f), f.Present && f.Schema != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ctx.Copy", () => CopyValue?.Invoke(f, Raw.Of(f.Targets[0].Block.Block, f.Key) ?? f.Targets[0].Block.Text(f.Key) ?? ""), f.Targets.Count > 0, gesture: "Ctrl+C"));
        string? clip = PasteValue?.Invoke();
        menu.Items.Add(Item("Ctx.Paste", () => Paste(f, clip!), clip != null && f is not ChildrenField, gesture: "Ctrl+V"));
        menu.Items.Add(Item("Ctx.Link", () => { }, false));
        menu.Items.Add(Item(f.FieldLocked ? "Ctx.Unlock" : "Ctx.Lock", () => vm.ToggleFieldLock(f.Key)));
        menu.Items.Add(Item("Ctx.Mode", () => { }, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ctx.ShowText", () => vm.RequestShowInText(f), f.Present));
        menu.Items.Add(Item("Ctx.FindSame", () => FindSame?.Invoke(f), f.Targets.Count > 0));
        menu.Items.Add(Item("Ctx.PushGame", () => PushToGame?.Invoke(f), CanPushToGame?.Invoke() == true && f.Targets.Count > 0));
    }

    private static void ResetToFile(FieldVm f)
    {
        f.Owner.Apply(f, b =>
        {
            var t = f.Targets.First(x => x.Block == b);
            if (t.Original == null)
                return null;
            var edits = new List<Pfx.Core.Editing.IEdit>();
            foreach (var key in f.Keys)
            {
                var line = t.Original.FindLine(key);
                var graph = t.Original.FindBlock(key);
                if (line != null)
                    edits.Add(Pfx.Core.Editing.EnfEdits.SetLine(b.Doc, b.Block, key, line.Values.Select(v => v.Text).ToList(), k => b.Schema.Find(b.Scope, k)?.Order));
                else if (graph != null)
                    edits.Add(Pfx.Core.Editing.EnfEdits.SetGraph(b.Doc, b.Block, key, graph.Items.OfType<Pfx.Core.Text.EnfLine>()
                        .SelectMany(l => l.Tokens).Select(x => x.Text).ToList(), k => b.Schema.Find(b.Scope, k)?.Order));
                else if (b.Reset(key) is { } reset)
                    edits.Add(reset);
            }
            return edits.Count == 0 ? null : new Pfx.Core.Editing.CompositeEdit(edits);
        }, () => Loc.F("Undo.ResetFile", f.Label), null);
    }

    private static void ResetToGame(FieldVm f) =>
        f.Owner.Apply(f, b =>
        {
            var edits = f.Keys.Select(k => b.Reset(k)).Where(x => x != null).Cast<Pfx.Core.Editing.IEdit>().ToList();
            return edits.Count == 0 ? null : new Pfx.Core.Editing.CompositeEdit(edits);
        }, () => Loc.F("Undo.ResetGame", f.Label), null);

    private static void Paste(FieldVm f, string text)
    {
        switch (f)
        {
            case NumberField n: n.Commit(text); break;
            case PairField p: p.Base.Commit(text); break;
            case PathField pf: pf.Commit(text.Trim('"')); break;
            default:
                f.Owner.Apply(f, b => Pfx.Core.Editing.EnfEdits.SetLine(b.Doc, b.Block, f.Key, text.Split(' ', StringSplitOptions.RemoveEmptyEntries), k => b.Schema.Find(b.Scope, k)?.Order),
                    () => Loc.F("Undo.Paste", f.Label), null);
                break;
        }
    }

    // Scrolls the row of a field into view (search jumps, "show in inspector").
    public void Reveal(FieldVm field)
    {
        foreach (var fe in Descendants(this).OfType<FrameworkElement>())
        {
            if (fe.DataContext == field && fe is Grid)
            {
                fe.BringIntoView();
                return;
            }
        }
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }
}
