using System.Windows;

namespace Sojourn.Launcher;

/// <summary>A field the launch needs filled: its rim turns coral until it's changed (no words).</summary>
static class Mark
{
    public static readonly DependencyProperty WrongProperty =
        DependencyProperty.RegisterAttached("Wrong", typeof(bool), typeof(Mark), new PropertyMetadata(false));
    public static bool GetWrong(DependencyObject o) => (bool)o.GetValue(WrongProperty);
    public static void SetWrong(DependencyObject o, bool v) => o.SetValue(WrongProperty, v);
}
