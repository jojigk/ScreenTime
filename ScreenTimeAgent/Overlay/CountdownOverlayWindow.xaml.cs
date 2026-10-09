using System.Windows;

namespace ScreenTimeAgent.Overlay;

/// <summary>
/// Advisory only: a small topmost corner banner that shows the countdown without blocking
/// input or stealing focus. The actual enforcement is the hard lock fired separately by
/// <see cref="OverlayController.Lock"/> once time runs out — this window never blocks the user.
/// </summary>
public partial class CountdownOverlayWindow : Window
{
    private const double BannerWidth = 300;
    private const double BannerHeight = 110;
    private const double CornerMargin = 16;

    public CountdownOverlayWindow()
    {
        InitializeComponent();

        Width = BannerWidth;
        Height = BannerHeight;
        Left = SystemParameters.WorkArea.Right - BannerWidth - CornerMargin;
        Top = SystemParameters.WorkArea.Bottom - BannerHeight - CornerMargin;
    }

    public void UpdateRemaining(int remainingSeconds)
    {
        TitleText.Text = "Screen time is almost up";
        CountdownText.Visibility = Visibility.Visible;
        CountdownText.Text = Math.Max(0, remainingSeconds).ToString();
        SubtitleText.Text = "seconds remaining";
    }

    /// <summary>Testing-mode state (LockEnabled=false): quota is exhausted but the session is not locked.</summary>
    public void ShowExpired()
    {
        TitleText.Text = "Time is over";
        CountdownText.Visibility = Visibility.Collapsed;
        SubtitleText.Text = string.Empty;
    }

    public void ShowOverlay()
    {
        Show();
    }

    public void HideOverlay()
    {
        Hide();
    }
}
