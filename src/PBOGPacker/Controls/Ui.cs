using System.Windows;

namespace PolyLab.PBOGPacker.Controls;

public static class Ui
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool value) => d.SetValue(ActiveProperty, value);
}
