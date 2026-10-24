using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Pfx.App.Recipes;
using Pfx.App.ViewModels;

namespace Pfx.App.Automation;

// P12c checks that go through the panels the way a user does: an own recipe saved from the selection with two exposed
// keys, added to another part, an exposed value edited and the node removed — all by the panel's own buttons and boxes;
// and a real mouse drag of a library emitter onto the layers list (system mouse input, so the library's drag start,
// WPF's DoDragDrop, the tab that opens under the drag and the list's drop handler all run).
public sealed partial class ShellChecks
{
    private static IEnumerable<T> Below<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
                yield return t;
            foreach (var d in Below<T>(child))
                yield return d;
        }
    }

    private static bool Inside(DependencyObject? d, DependencyObject root)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == root)
                return true;
        return false;
    }

    // A click as a screen reader makes it (the button's Click runs on the dispatcher).
    private static void Press(ButtonBase b) => ((IInvokeProvider)new ButtonAutomationPeer((Button)b).GetPattern(PatternInterface.Invoke)!).Invoke();

    private Button? PanelButton(FrameworkElement root, string key) =>
        Below<Button>(root).FirstOrDefault(b => b.Content as string == Loc.T(key) && b.IsVisible);

    private async Task OwnRecipeChecks()
    {
        var panel = _w.RecipesTab;
        _w.ShowPane("recipes");
        var source = _w.Docs[0];
        var target = _w.Docs.FirstOrDefault(d => d != source && _w.PartOf(d) != null) ?? _w.Docs.FirstOrDefault(d => d != source);
        if (target == null)
        {
            Problem("own recipe check needs two open parts");
            return;
        }
        _w.Select(source, new[] { source.Ptc.Emitters[0].Block }, false);
        await Settled();
        panel.UpdateLayout();
        if (PanelButton(panel, "Rcp.OwnSave") is not { } start)
        {
            Problem("own recipe: no save button on the recipes panel");
            return;
        }
        Press(start);
        await Settled();
        panel.UpdateLayout();
        var name = Below<TextBox>(panel).FirstOrDefault(t => t.Text == Loc.T("Rcp.OwnDefaultName"));
        // Two keys holding one number each, so the edit below can change a number.
        var keys = Below<CheckBox>(panel).Where(c => c.Content is string s && s.Contains(" = ")
                && double.TryParse(s[(s.LastIndexOf(" = ", StringComparison.Ordinal) + 3)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Take(2).ToList();
        if (name == null || keys.Count < 2 || PanelButton(panel, "Rcp.OwnSaveNow") is not { } save)
        {
            Problem($"own recipe: save form incomplete (name box {name != null}, number keys {keys.Count})");
            return;
        }
        const string ownName = "check_own";
        name.Text = ownName;
        foreach (var k in keys)
            ((IToggleProvider)new CheckBoxAutomationPeer(k).GetPattern(PatternInterface.Toggle)!).Toggle();
        Press(save);
        await Settled();
        var own = _w.OwnRecipes().FirstOrDefault(o => o.Recipe.Name == ownName);
        Expect(own.Recipe != null && own.Recipe.Exposed.Count == 2, $"own recipe \"{ownName}\" saved from the panel with {own.Recipe?.Exposed.Count} exposed keys");
        if (own.Recipe == null)
            return;

        _w.Select(target, Array.Empty<Pfx.Core.Text.EnfBlock>(), true);
        await Settled();
        var open = _w.Docs.ToList();
        string before = Bytes(open);
        int count = target.Ptc.Emitters.Count;
        panel.Show();
        panel.UpdateLayout();
        var row = Below<DockPanel>(panel).FirstOrDefault(p => p.Children.OfType<StackPanel>().Any(s => s.Children.OfType<TextBlock>().FirstOrDefault()?.Text == ownName));
        var add = row?.Children.OfType<Button>().FirstOrDefault(b => b.Content as string == Loc.T("Rcp.Add"));
        if (add == null)
        {
            Problem("own recipe: its row or its Add button is missing");
            return;
        }
        Press(add);
        await Settled();
        var node = _w.Project?.File.Recipes.LastOrDefault();
        Expect(node?.Kind == OwnRecipeRun.Kind && target.Ptc.Emitters.Count == count + own.Recipe.Emitters.Count,
            $"own recipe added to {target.Title} by its Add button: node {node?.Id}, {target.Ptc.Emitters.Count - count} emitters");
        if (node == null)
            return;

        var x = own.Recipe.Exposed[0];
        string madeName = node.Log.FirstOrDefault(o => o.Op == Pfx.Core.Model.RecipeOp.EmitterAdd)?.Emitter ?? x.Emitter;
        double now = double.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture);
        string edited = Pfx.App.Recipes.RecipeCatalog.F(now + 1);
        panel.SelectNode(node.Id);
        panel.UpdateLayout();
        var label = OwnRecipeRun.Label(own.Recipe, "p0");
        var box = Below<DockPanel>(panel).Where(p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text == label).Select(p => p.Children.OfType<TextBox>().FirstOrDefault()).FirstOrDefault(t => t != null);
        if (box == null)
        {
            Problem($"own recipe: no box for the exposed value {label}");
            return;
        }
        box.Text = edited;
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        await Settled();
        Expect(target.Ptc.Emitter(madeName)?.Text(x.Key) == edited, $"exposed value {label} edited in the node's box: {x.Key} of {madeName} = {target.Ptc.Emitter(madeName)?.Text(x.Key)} (wanted {edited})");

        panel.UpdateLayout();
        var remove = Below<Button>(panel).FirstOrDefault(b => b.ToolTip as string == Loc.T("Tip.Rcp.Remove"));
        if (remove == null)
        {
            Problem("own recipe: the node has no remove button");
            return;
        }
        Press(remove);
        await Settled();
        Expect(Bytes(open) == before, "removing the own recipe's node by its button brings every part and the project back byte for byte");

        panel.UpdateLayout();
        row = Below<DockPanel>(panel).FirstOrDefault(p => p.Children.OfType<StackPanel>().Any(s => s.Children.OfType<TextBlock>().FirstOrDefault()?.Text == ownName));
        row?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        panel.UpdateLayout();
        if (PanelButton(panel, "Rcp.OwnDelete") is { } del)
        {
            Press(del);
            await Settled();
        }
        Expect(_w.OwnRecipes().All(o => o.Recipe.Name != ownName), "the own recipe is deleted by its Delete button");
    }

    // ---- real mouse drag ----

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    // INPUT with the mouse member (type 0) as the 64-bit process sees it: the union starts at 8 (it holds a pointer),
    // MOUSEINPUT's dwExtraInfo at 32, 40 bytes in all. SendInput refuses any other size.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public IntPtr Extra;
    }

    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    private void SendButton(uint flag)
    {
        if (SendInput(1, new[] { new MouseInput { Flags = flag } }, Marshal.SizeOf<MouseInput>()) != 1)
            Problem($"library drag: SendInput refused the button event (error {Marshal.GetLastWin32Error()})");
    }

    private static void CursorTo(FrameworkElement e, double dx = 0)
    {
        var p = e.PointToScreen(new Point(e.ActualWidth / 2 + dx, e.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y);
    }

    private async Task LibraryDragCheck()
    {
        _w.Activate();
        await Settled();
        if (GetForegroundWindow() != new WindowInteropHelper(_w).Handle)
        {
            Problem("library drag: the window is not in front (another window took the foreground); the drag was not performed");
            return;
        }
        var lib = _w.LibraryTab;
        _w.ShowPane("library");
        await Settled();
        lib.UpdateLayout();
        var entry = lib.Entries.FirstOrDefault(x => Pfx.App.Library.LibraryIndex.Emitters(x.Path).Count > 0);
        var row = entry == null ? null : Below<DockPanel>(lib).FirstOrDefault(p => p.ToolTip is string t && t.StartsWith(entry.Path + "\n", StringComparison.Ordinal));
        var layersTab = Below<AvalonDock.Controls.LayoutAnchorableTabItem>(_w).FirstOrDefault(t => t.Model == _w.LayersPane);
        if (entry == null || row == null || layersTab == null)
        {
            Problem($"library drag: nothing to drag (entry {entry?.Name}, row {row != null}, layers tab {layersTab != null})");
            return;
        }
        GetCursorPos(out var home);
        row.BringIntoView();
        await Settled();
        // A real click opens the row (its emitters appear below it).
        CursorTo(row);
        SendButton(LeftDown);
        SendButton(LeftUp);
        string first = Pfx.App.Library.LibraryIndex.Emitters(entry.Path)[0].Name;
        TextBlock? line = null;
        for (int i = 0; i < 600 && line == null; i++)
        {
            await Frame();
            lib.UpdateLayout();
            line = Below<TextBlock>(lib).FirstOrDefault(t => t.Text == "⋮⋮ " + first && t.IsVisible);
        }
        if (line == null)
        {
            Problem($"library drag: the click did not open {entry.Name}");
            SetCursorPos(home.X, home.Y);
            return;
        }
        line.BringIntoView();
        await Settled();
        var doc = _w.ActiveDoc ?? _w.Docs[0];
        int count = doc.Ptc.Emitters.Count;
        var list = Below<ListBox>(_w.LayersPanel).First();
        var steps = new List<string>();
        int phase = 0;
        var done = new TaskCompletionSource();
        void Pressed(object s, MouseButtonEventArgs e)
        {
            if (phase != 0)
                return;
            phase = 1;
            steps.Add("pressed");
            CursorTo(line!, 12);
        }
        void Feedback(object s, GiveFeedbackEventArgs e)
        {
            if (phase != 1)
                return;
            phase = 2;
            steps.Add("drag started");
            CursorTo(layersTab);
        }
        void Over(object s, DragEventArgs e)
        {
            if (phase == 2 && _w.LayersPane.IsSelected && list.IsVisible)
            {
                list.UpdateLayout();
                var item = list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<EmitterRow>().FirstOrDefault(r => r.Doc == doc)) as FrameworkElement;
                if (item == null)
                    return;
                phase = 3;
                steps.Add("layers tab opened");
                CursorTo(item);
            }
            else if (phase == 3 && Inside(e.OriginalSource as DependencyObject, list))
            {
                phase = 4;
                steps.Add("over the list");
                SendButton(LeftUp);
            }
        }
        void Dropped(object s, DragEventArgs e)
        {
            steps.Add("dropped");
            done.TrySetResult();
        }
        var pressed = new MouseButtonEventHandler(Pressed);
        var feedback = new GiveFeedbackEventHandler(Feedback);
        var over = new DragEventHandler(Over);
        var dropped = new DragEventHandler(Dropped);
        _w.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, pressed, true);
        _w.AddHandler(DragDrop.GiveFeedbackEvent, feedback, true);
        _w.AddHandler(DragDrop.PreviewDragOverEvent, over, true);
        list.AddHandler(DragDrop.DropEvent, dropped, true);
        try
        {
            CursorTo(line);
            SendButton(LeftDown);
            // Frames keep coming inside the drag loop (it pumps the dispatcher); a step that never comes ends the drag.
            for (int i = 0; i < 900 && !done.Task.IsCompleted; i++)
                await Frame();
            if (!done.Task.IsCompleted)
                SendButton(LeftUp);
            await Settled();
        }
        finally
        {
            _w.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, pressed);
            _w.RemoveHandler(DragDrop.GiveFeedbackEvent, feedback);
            _w.RemoveHandler(DragDrop.PreviewDragOverEvent, over);
            list.RemoveHandler(DragDrop.DropEvent, dropped);
            SetCursorPos(home.X, home.Y);
        }
        Expect(done.Task.IsCompleted && doc.Ptc.Emitters.Count == count + 1,
            $"real drag of {first} from the library onto the layers list ({string.Join(" → ", steps)}): {doc.Title} has {doc.Ptc.Emitters.Count - count} new emitter");
        if (doc.Ptc.Emitters.Count == count + 1)
        {
            _w.UndoActive();
            await Settled();
            Expect(doc.Ptc.Emitters.Count == count, "undo removes the dragged emitter");
        }
    }
}
