using System.Windows;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;

namespace EliteSIP.App.PanelLine;

public partial class UpdateOffer : Window
{
    public UpdateOffer(Version installed, Version available, string notes, bool dark)
    {
        InitializeComponent();
        DataContext = new UpdateOfferContent(installed, available, notes);
        CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Template = null; }
        else
        {
            WindowStyle = WindowStyle.None;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
            });
        }
        SourceInitialized += (_, _) => WindowCaption.Apply(this, dark);
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>Заметки отображаются как текст из уже проверенного манифеста, без HTML.</summary>
public sealed class UpdateOfferContent
{
    public UpdateOfferContent(Version installed, Version available, string notes)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(notes);
        VersionTransition = $"EliteSIP {installed.ToString(3)} → {available.ToString(3)}";
        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim().TrimStart('-', '*', '•').Trim()).Where(line => line.Length > 0).ToArray();
        Changes = lines.Length > 0 ? lines : [Strings.Get("UpdateNoNotes")];
    }

    public string VersionTransition { get; }
    public IReadOnlyList<string> Changes { get; }
}
