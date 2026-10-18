using MailKit.Security;
using MimeKit;

namespace IISMonitor.Website.Site;

/// <summary>The "Email" section of appsettings.json: where contact messages go and how they are sent.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Who receives contact messages. Several addresses can be separated by commas.</summary>
    public string To { get; set; } = "";

    /// <summary>The sender address. Must be one the SMTP server lets this site send from.</summary>
    public string From { get; set; } = "";

    public string FromName { get; set; } = "IIS Monitor website";

    /// <summary>Put in front of every subject, so messages are easy to filter.</summary>
    public string SubjectPrefix { get; set; } = "[IIS Monitor]";

    /// <summary>How many messages one visitor (IP address) can send per hour.</summary>
    public int MaxMessagesPerHour { get; set; } = 5;

    /// <summary>
    /// When set, messages are written to this folder as .eml files instead of being sent: for
    /// development and testing. Relative paths are relative to the site's content root.
    /// </summary>
    public string PickupDirectory { get; set; } = "";

    public SmtpOptions Smtp { get; set; } = new();

    public bool UsesPickupDirectory => !string.IsNullOrWhiteSpace(PickupDirectory);

    /// <summary>Recipients, sender and a way to deliver are all set and valid.</summary>
    public bool IsConfigured =>
        InternetAddressList.TryParse(To, out var to) && to.Mailboxes.Any()
        && MailboxAddress.TryParse(From, out _)
        && (UsesPickupDirectory || !string.IsNullOrWhiteSpace(Smtp.Host));
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>None, Auto, SslOnConnect (usually port 465), StartTls (usually 587) or StartTlsWhenAvailable.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    /// <summary>Leave empty for a server that doesn't need signing in.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Don't commit this: use user secrets or the Email__Smtp__Password environment variable.</summary>
    public string Password { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 30;
}
