using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pfx.App.ViewModels;

namespace Pfx.App.Views;

public interface ILayersHost
{
    void LayersSelected(IReadOnlyList<LayerRow> rows);
    void RenameEmitter(EmitterRow row, string name);
    string? RenameProblem(EmitterRow row, string name);
    void AddEmitter();
    void CloneEmitter();
    void DeleteEmitters();
    void MoveEmitter(EmitterRow row, EmitterRow target);
    void DropLibraryEmitter(LibraryDrag drag, LayerRow? target);
}

public partial class LayersView : UserControl
{
    private Point _pressAt;
    private EmitterRow? _pressed;
    private bool _syncing;

    public LayersView()
    {
        InitializeComponent();
    }

    public ILayersHost? Host { get; set; }

    public void SetSelection(IEnumerable<LayerRow> rows)
    {
        _syncing = true;
        List.SelectedItems.Clear();
        foreach (var r in rows)
            List.SelectedItems.Add(r);
        _syncing = false;
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        Host?.LayersSelected(List.SelectedItems.Cast<LayerRow>().ToList());
    }

    private static T? Up<T>(DependencyObject? d) where T : class
    {
        while (d != null && d is not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(List);
        _pressed = Up<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as EmitterRow;
        if (Up<ToggleButton>(e.OriginalSource as DependencyObject) != null || Up<TextBox>(e.OriginalSource as DependencyObject) != null)
            _pressed = null;
    }

    // Drag a row onto another to reorder (the file's emitter order is the draw order inside the effect).
    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var p = e.GetPosition(List);
        if (Math.Abs(p.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var row = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop(List, new DataObject(typeof(EmitterRow), row), DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(LibraryDrag)))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        var target = Up<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as EmitterRow;
        e.Effects = e.Data.GetData(typeof(EmitterRow)) is EmitterRow src && target != null && target.Doc == src.Doc && target != src ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LibraryDrag)) is LibraryDrag drag)
        {
            Host?.DropLibraryEmitter(drag, Up<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as LayerRow);
            return;
        }
        var target = Up<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as EmitterRow;
        if (e.Data.GetData(typeof(EmitterRow)) is EmitterRow src && target != null && target.Doc == src.Doc && target != src)
            Host?.MoveEmitter(src, target);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Up<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is EmitterRow row && Up<ToggleButton>(e.OriginalSource as DependencyObject) == null)
            row.Renaming = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;
        if (e.Key == Key.F2 && List.SelectedItems.Count == 1 && List.SelectedItem is EmitterRow row)
        {
            row.Renaming = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Host?.DeleteEmitters();
            e.Handled = true;
        }
    }

    private void OnRenameShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnRenameText(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: EmitterRow row } box && row.Renaming && Host != null)
            row.RenameProblem = Host.RenameProblem(row, box.Text);
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: EmitterRow row } box)
            return;
        if (e.Key == Key.Enter)
        {
            Finish(row, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.Renaming = false;
            e.Handled = true;
        }
    }

    private void OnRenameLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: EmitterRow { Renaming: true } row } box)
            Finish(row, box.Text);
    }

    private void Finish(EmitterRow row, string name)
    {
        if (name == row.Name)
        {
            row.Renaming = false;
            return;
        }
        string? problem = Host?.RenameProblem(row, name);
        if (problem != null)
        {
            row.RenameProblem = problem;
            return;
        }
        row.Renaming = false;
        Host?.RenameEmitter(row, name);
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Host?.AddEmitter();
    private void OnClone(object sender, RoutedEventArgs e) => Host?.CloneEmitter();
    private void OnDelete(object sender, RoutedEventArgs e) => Host?.DeleteEmitters();
}
