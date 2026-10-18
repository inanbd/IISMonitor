using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using IISMonitor.Website.Site;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MimeKit;

namespace IISMonitor.Website.Pages;

public sealed class ContactForm
{
    [Display(Name = "Your name")]
    [Required(ErrorMessage = "Please enter your name.")]
    [StringLength(100, ErrorMessage = "Please keep your name under 100 characters.")]
    public string Name { get; set; } = "";

    [Display(Name = "Email address")]
    [Required(ErrorMessage = "Please enter your email address, so I can reply.")]
    [StringLength(254, ErrorMessage = "That email address is too long.")]
    [EmailAddress(ErrorMessage = "That doesn't look like an email address.")]
    public string Email { get; set; } = "";

    [Display(Name = "Company")]
    [StringLength(100, ErrorMessage = "Please keep the company name under 100 characters.")]
    public string? Company { get; set; }

    [Display(Name = "Topic")]
    public string Topic { get; set; } = ContactModel.Topics[0];

    [Display(Name = "Message")]
    [Required(ErrorMessage = "Please write a message.")]
    [StringLength(5000, MinimumLength = 10, ErrorMessage = "Please write between 10 and 5,000 characters.")]
    public string Message { get; set; } = "";
}

public sealed class ContactModel(
    IContactMailer mailer,
    ContactThrottle throttle,
    IDataProtectionProvider protection,
    TimeProvider time,
    ILogger<ContactModel> logger) : PageModel
{
    public static readonly IReadOnlyList<string> Topics =
        ["A question", "Support or a problem", "Feature request", "Licensing or business", "Something else"];

    /// <summary>Faster than a person can fill in the form: almost certainly a bot.</summary>
    private static readonly TimeSpan MinimumFillTime = TimeSpan.FromSeconds(3);

    /// <summary>How long a rendered form stays valid.</summary>
    private static readonly TimeSpan FormLifetime = TimeSpan.FromDays(1);

    private readonly ITimeLimitedDataProtector _stamps = protection.CreateProtector("IISMonitor.Website.Contact.Stamp.v1").ToTimeLimitedDataProtector();

    [BindProperty]
    public ContactForm Form { get; set; } = new();

    /// <summary>A field people can't see. Bots that fill in every field give themselves away.</summary>
    [BindProperty]
    public string? Website { get; set; }

    /// <summary>When the form was rendered, signed so it can't be forged.</summary>
    [BindProperty]
    public string? Stamp { get; set; }

    [TempData]
    public bool Sent { get; set; }

    public bool CanSend => mailer.IsConfigured;

    public void OnGet() => Stamp = NewStamp();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!CanSend)
        {
            Stamp = NewStamp();
            return Page();
        }

        // Bots get the same "sent" page as people, so they learn nothing from the response.
        if (!string.IsNullOrEmpty(Website))
            return PretendSent("the hidden field was filled in");

        var age = StampAge();
        if (age is null)
        {
            ModelState.AddModelError(string.Empty, "This form was open for too long. Please send your message again.");
            Stamp = NewStamp();
            return Page();
        }

        if (age < MinimumFillTime)
            return PretendSent("the form was sent too quickly");

        Stamp = NewStamp();
        var topic = Topics.FirstOrDefault(t => t == Form.Topic) ?? Topics[^1];
        var name = SingleLine(Form.Name);
        var email = SingleLine(Form.Email);
        if (!MailboxAddress.TryParse(email, out var parsed) || parsed.Address != email || email.Contains(' '))
            ModelState.AddModelError($"{nameof(Form)}.{nameof(ContactForm.Email)}", "That doesn't look like an email address.");
        if (name.Length == 0)
            ModelState.AddModelError($"{nameof(Form)}.{nameof(ContactForm.Name)}", "Please enter your name.");
        if (!ModelState.IsValid)
            return Page();

        if (!throttle.TryAcquire(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
        {
            ModelState.AddModelError(string.Empty, "You've sent several messages in the last hour. Please try again later.");
            return Page();
        }

        var message = new ContactMessage(name, email, NullIfEmpty(SingleLine(Form.Company)), topic,
            Form.Message.ReplaceLineEndings("\n").Trim(), time.GetUtcNow());
        try
        {
            await mailer.SendAsync(message, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The message itself is personal data, so it is never logged.
            logger.LogError(e, "Couldn't send a contact message ({Topic}).", topic);
            ModelState.AddModelError(string.Empty, "Sorry, your message couldn't be sent right now. Please try again in a few minutes.");
            return Page();
        }

        logger.LogInformation("Contact message sent ({Topic}).", topic);
        Sent = true;
        return RedirectToPage();
    }

    private IActionResult PretendSent(string reason)
    {
        logger.LogInformation("Contact form submission ignored: {Reason}.", reason);
        Sent = true;
        return RedirectToPage();
    }

    private string NewStamp() =>
        _stamps.Protect(time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), FormLifetime);

    /// <summary>How long ago the form was rendered; null when the stamp is missing, forged or expired.</summary>
    private TimeSpan? StampAge()
    {
        try
        {
            var rendered = long.Parse(_stamps.Unprotect(Stamp ?? ""), CultureInfo.InvariantCulture);
            return time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(rendered);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Line breaks and other control characters don't belong in a name or an address.</summary>
    private static string SingleLine(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(c => !char.IsControl(c)).ToArray())))
            .Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
