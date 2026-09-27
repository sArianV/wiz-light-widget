using System.Windows;

namespace WizLightWidget;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string text) =>
        Dispatcher.Invoke(() => StatusTextBlock.Text = text);

    public void SetProgress(double ratio)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressIndicator.IsIndeterminate = false;
            ProgressIndicator.Value = Math.Clamp(ratio, 0, 1) * 100;
        });
    }
}
