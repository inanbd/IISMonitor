using System.Windows;
using System.Windows.Controls;
using IISMonitor.Core.RequestLog;
using IISMonitor.Dashboard.ViewModels;

namespace IISMonitor.Dashboard;

/// <summary>Asks where IIS should refuse an IP address: the selected app pool's sites and applications, or every site.</summary>
public partial class BlockIpWindow : Window
{
    private const int ApplicationsShown = 10;

    private readonly IpBlockPrompt _prompt;

    public BlockIpWindow(IpBlockPrompt prompt)
    {
        InitializeComponent();
        _prompt = prompt;

        Address.Text = prompt.IpAddress;
        PrivateWarning.Visibility = IpAddressRules.IsPrivate(prompt.IpAddress) ? Visibility.Visible : Visibility.Collapsed;

        // A TextBlock, so an underscore in a pool name isn't taken as an access key.
        PoolScope.Content = new TextBlock
        {
            Text = string.IsNullOrEmpty(prompt.AppPool)
                ? "Only one app pool's sites and applications"
                : $"Only app pool {prompt.AppPool}'s sites and applications",
            TextWrapping = TextWrapping.Wrap,
        };

        var applications = prompt.Applications;
        PoolApplications.Text = applications.Count == 0
            ? "This app pool runs no sites or applications right now."
            : string.Join(", ", applications.Take(ApplicationsShown))
              + (applications.Count > ApplicationsShown ? $" and {applications.Count - ApplicationsShown} more" : "");

        var poolPossible = !string.IsNullOrEmpty(prompt.AppPool) && applications.Count > 0;
        PoolScope.IsEnabled = poolPossible;
        PoolScope.IsChecked = poolPossible;
        ServerScope.IsChecked = !poolPossible;
    }

    /// <summary>What to block, once the user clicked Block.</summary>
    public IpBlockRequest? Result { get; private set; }

    private void OnBlock(object sender, RoutedEventArgs e)
    {
        Result = new IpBlockRequest
        {
            IpAddress = _prompt.IpAddress,
            AppPool = PoolScope.IsChecked == true ? _prompt.AppPool : null,
        };
        DialogResult = true;
    }
}
