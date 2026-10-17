using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IISMonitor.Core.Models;
using IISMonitor.Core.Requests;

namespace IISMonitor.Dashboard;

public partial class IisLoggingWindow : Window
{
    private readonly List<SiteChoice> _sites;

    private IisLoggingWindow(IReadOnlyList<SiteMetrics> sites)
    {
        InitializeComponent();
        _sites = sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new SiteChoice(s)).ToList();
        SiteGrid.ItemsSource = _sites;
    }

    public IisLoggingRequest? Result { get; private set; }

    public static IisLoggingRequest? Ask(Window owner, IReadOnlyList<SiteMetrics> sites)
    {
        var dialog = new IisLoggingWindow(sites) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnAll(object sender, RoutedEventArgs e) => _sites.ForEach(s => s.Headers = true);

    private void OnNone(object sender, RoutedEventArgs e) => _sites.ForEach(s => s.Headers = false);

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Result = new IisLoggingRequest
        {
            RequestFields = RequestFields.IsChecked == true,
            HeaderFieldSites = _sites.Where(s => s.Headers).Select(s => s.Id).ToList(),
            // Only take away what IIS Monitor added on a site that had it.
            RemoveHeaderFieldSites = _sites.Where(s => !s.Headers && s.HadHeaders).Select(s => s.Id).ToList(),
        };
        DialogResult = true;
    }

    private sealed class SiteChoice(SiteMetrics site) : INotifyPropertyChanged
    {
        private bool _headers = site.HeaderFieldsEnabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        public long Id { get; } = site.Id;
        public string Name { get; } = site.Name;
        public bool HadHeaders { get; } = site.HeaderFieldsEnabled;

        public string Now =>
            !site.EtwLoggingEnabled ? "not sent to ETW"
            : !site.RequestFieldsEnabled ? "response times only"
            : !site.HeaderFieldsEnabled ? "request details"
            : site.ForwardedForUsable ? "request details and headers" : "headers, but X-Forwarded-For isn't last";

        public bool Headers
        {
            get => _headers;
            set
            {
                if (_headers == value)
                    return;
                _headers = value;
                Raise();
            }
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
