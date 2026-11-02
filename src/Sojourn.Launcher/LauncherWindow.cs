using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Sojourn.Launcher;

/// <summary>The window around the view: no system frame (the view draws its own keys), dragged by its top, rounded by
/// Windows 11. Started from a Claude Code session it shows without taking focus.</summary>
sealed class LauncherWindow : Window
{
    public LauncherWindow(LauncherView view)
    {
        Title = "Sojourn";
        Language = Words.Language;   // (the view follows a change itself: LauncherView.Language.cs)
        Content = view;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xF1));
        ShowActivated = !Places.Background;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        WindowChrome.SetIsHitTestVisibleInChrome(view.MinKey, true);
        WindowChrome.SetIsHitTestVisibleInChrome(view.CloseKey, true);
        view.CloseRequested += Close;
        view.MinimiseRequested += () => WindowState = WindowState.Minimized;
        SourceInitialized += (_, _) =>
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
        };
        Loaded += (_, _) => view.FocusFirst();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
