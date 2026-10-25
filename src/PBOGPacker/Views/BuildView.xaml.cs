using System.Windows.Controls;
using PolyLab.PBOGPacker.ViewModels;

namespace PolyLab.PBOGPacker.Views;

public partial class BuildView : UserControl
{
    public BuildView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel o) o.LogAppended -= OnLog;
            if (e.NewValue is MainViewModel n) n.LogAppended += OnLog;
        };
    }

    private void OnLog()
    {
        if (DataContext is not MainViewModel vm || !vm.AutoScroll || LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
