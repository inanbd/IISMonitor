using System.Windows;
using IISMonitor.Core.Blocking;
using IISMonitor.Core.Requests;
using IISMonitor.Dashboard.ViewModels;

namespace IISMonitor.Dashboard;

public partial class BlockIpWindow : Window
{
    private sealed record Choice<T>(T Value, string Label);

    private static readonly Choice<double>[] Durations =
    [
        new(1, "1 hour"), new(24, "24 hours"), new(24 * 7, "7 days"), new(24 * 30, "30 days"), new(0, "Until I unblock it"),
    ];

    private static readonly Choice<BlockMethod?>[] Methods =
    [
        new(null, "Automatic: firewall, or IIS if it came through a trusted proxy"),
        new(BlockMethod.Firewall, "Windows Firewall"),
        new(BlockMethod.Iis, "IIS IP restrictions (visitor behind a proxy)"),
    ];

    private BlockIpWindow(string? ip)
    {
        InitializeComponent();
        Address.Text = ip ?? "";
        Duration.ItemsSource = Durations;
        Duration.SelectedIndex = 1;
        Method.ItemsSource = Methods;
        Method.SelectedIndex = 0;
        Loaded += (_, _) => (string.IsNullOrEmpty(ip) ? Address : Note).Focus();
    }

    public BlockIpDraft? Result { get; private set; }

    public static BlockIpDraft? Ask(Window owner, string? ip)
    {
        var dialog = new BlockIpWindow(ip) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnBlock(object sender, RoutedEventArgs e)
    {
        if (!IpText.TryParse(Address.Text, out var address))
        {
            Error.Text = "Enter a full IPv4 or IPv6 address, for example 203.0.113.5.";
            Error.Visibility = Visibility.Visible;
            return;
        }

        Result = new BlockIpDraft(
            address.ToString(),
            ((Choice<double>)Duration.SelectedItem).Value,
            ((Choice<BlockMethod?>)Method.SelectedItem).Value,
            string.IsNullOrWhiteSpace(Note.Text) ? null : Note.Text.Trim());
        DialogResult = true;
    }
}
