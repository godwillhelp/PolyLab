using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NDZ.PBOGPacker.ViewModels;

namespace NDZ.PBOGPacker.Views;

// Drag and drop for the load order: the dragged row moves live under the cursor, so there is no
// separate insertion marker to keep in sync.
public partial class WorkshopDialog : UserControl
{
    private Point _start;
    private QueueItemVM? _drag;
    private ModsVM? _hooked;

    public WorkshopDialog()
    {
        InitializeComponent();
        Dlg.DataContextChanged += (_, e) =>
        {
            if (_hooked != null) _hooked.Added -= OnAdded;
            _hooked = e.NewValue as ModsVM;
            if (_hooked != null) _hooked.Added += OnAdded;
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Dispatcher.BeginInvoke(() => SearchBox.Focus()); };
    }

    private ModsVM? Vm => Dlg.DataContext as ModsVM;

    private void OnAdded(QueueItemVM q) => Dispatcher.BeginInvoke(() => QueueList.ScrollIntoView(q));

    private static QueueItemVM? ItemOf(object source) => source switch
    {
        FrameworkElement fe => fe.DataContext as QueueItemVM,
        FrameworkContentElement fce => fce.DataContext as QueueItemVM,
        _ => null,
    };

    private static bool InButton(DependencyObject? d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    private void OnQueueDown(object sender, MouseButtonEventArgs e)
    {
        _drag = InButton(e.OriginalSource as DependencyObject) ? null : ItemOf(e.OriginalSource);
        _start = e.GetPosition(QueueList);
    }

    private void OnQueueMove(object sender, MouseEventArgs e)
    {
        if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(QueueList) - _start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _drag;
        _drag = null;
        item.IsDragging = true;
        try { DragDrop.DoDragDrop(QueueList, new DataObject(typeof(QueueItemVM), item), DragDropEffects.Move); }
        finally { item.IsDragging = false; }
    }

    private void OnQueueDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(QueueItemVM)) is not QueueItemVM item || Vm == null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (ItemOf(e.OriginalSource) is { } target && target != item) Vm.Move(item, target);
        AutoScroll(e.GetPosition(QueueList));
    }

    private void OnQueueDrop(object sender, DragEventArgs e) => e.Handled = true;

    // DragOver fires on every mouse move: one row at most every 120 ms keeps the list from running away.
    private DateTime _scrolled;

    private void AutoScroll(Point p)
    {
        if (FindScroll(QueueList) is not { } sv || (DateTime.Now - _scrolled).TotalMilliseconds < 120) return;
        if (p.Y < 20) sv.LineUp();
        else if (p.Y > QueueList.ActualHeight - 20) sv.LineDown();
        else return;
        _scrolled = DateTime.Now;
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroll(c) is { } found) return found;
        }
        return null;
    }
}
