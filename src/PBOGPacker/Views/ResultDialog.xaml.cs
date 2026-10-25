using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PolyLab.PBOGPacker.ViewModels;

namespace PolyLab.PBOGPacker.Views;

public partial class ResultDialog : UserControl
{
    public ResultDialog()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Play(); };
    }

    private void Play()
    {
        var d = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        var sc = (ScaleTransform)Dlg.RenderTransform;
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });

        var ping = (ScaleTransform)((TransformGroup)Ping.RenderTransform).Children[0];
        var pd = new Duration(TimeSpan.FromMilliseconds(700));
        Ping.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, pd) { BeginTime = TimeSpan.FromMilliseconds(150) });
        ping.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.7, pd) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = ease });
        ping.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.7, pd) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = ease });
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (Vm != null) Vm.ResultOpen = false;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.ResultOpen = false;
    }
}
