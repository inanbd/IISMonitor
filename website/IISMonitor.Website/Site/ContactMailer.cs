using System.Text;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IISMonitor.Website.Site;

/// <summary>A message from the contact form, already validated.</summary>
public sealed record ContactMessage(string Name, string Email, string? Company, string Topic, string Text, DateTimeOffset SentAt);

public interface IContactMailer
{
    bool IsConfigured { get; }

    Task SendAsync(ContactMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Emails contact messages to the site owner through SMTP (MailKit), or writes them to a pickup
/// folder when one is configured. The visitor goes in Reply-To, never From, so the message passes
/// the sending domain's SPF and DMARC checks and a reply still reaches the visitor.
/// </summary>
public sealed class ContactMailer(IOptionsMonitor<EmailOptions> options, IHostEnvironment environment, SiteContext site) : IContactMailer
{
    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task SendAsync(ContactMessage message, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Email isn't configured: set Email:To, Email:From and Email:Smtp:Host.");

        var mime = Compose(settings, message, site.Absolute("/contact"));
        if (settings.UsesPickupDirectory)
        {
            var folder = Path.Combine(environment.ContentRootPath, settings.PickupDirectory);
            Directory.CreateDirectory(folder);
            await mime.WriteToAsync(Path.Combine(folder, $"{message.SentAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml"), cancellationToken);
            return;
        }

        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(Math.Max(5, settings.Smtp.TimeoutSeconds)).TotalMilliseconds };
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, settings.Smtp.Security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Smtp.UserName))
            await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password, cancellationToken);
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    public static MimeMessage Compose(EmailOptions settings, ContactMessage message, string pageUrl)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, MailboxAddress.Parse(settings.From).Address));
        mime.To.AddRange(InternetAddressList.Parse(settings.To).Mailboxes);
        mime.ReplyTo.Add(new MailboxAddress(message.Name, message.Email));
        mime.Subject = $"{settings.SubjectPrefix} {message.Topic}: {message.Name}".Trim();
        mime.Date = message.SentAt;

        var body = new StringBuilder()
            .AppendLine($"Name:    {message.Name}")
            .AppendLine($"Email:   {message.Email}")
            .AppendLine($"Company: {(string.IsNullOrEmpty(message.Company) ? "-" : message.Company)}")
            .AppendLine($"Topic:   {message.Topic}")
            .AppendLine()
            .AppendLine(message.Text)
            .AppendLine()
            .AppendLine("--")
            .AppendLine($"Sent from {pageUrl} on {message.SentAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Reply to this email to answer {message.Name}.");
        mime.Body = new TextPart("plain") { Text = body.ToString() };
        return mime;
    }
}
