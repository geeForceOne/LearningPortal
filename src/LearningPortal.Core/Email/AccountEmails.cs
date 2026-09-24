using System.Net;

namespace LearningPortal.Core.Email;

// The account emails, as plain text plus a simple HTML version. Inline styles only, since
// mail clients drop stylesheets.
public static class AccountEmails
{
    public static EmailMessage Invite(string userName, string link, TimeSpan validFor) => Build(
        $"Your {AppInfo.Name} account",
        $"An account has been created for you on {AppInfo.Name}, where you can turn your own study material into practice exams.",
        $"Your username is {userName}. Open the link below to choose your password and sign in.",
        "Choose your password",
        link,
        $"The link works once and expires in {Describe(validFor)}. If it has expired, ask your administrator for a new one.");

    public static EmailMessage PasswordReset(string userName, string link, TimeSpan validFor) => Build(
        $"Reset your {AppInfo.Name} password",
        $"Someone asked to reset the password for the {AppInfo.Name} account {userName}.",
        "Open the link below to choose a new password.",
        "Reset password",
        link,
        $"The link works once and expires in {Describe(validFor)}. If you didn't ask for this, ignore this email; your password stays the same.");

    public static EmailMessage Test(string userName) => new(
        $"{AppInfo.Name} test email",
        $"Hello {userName},\n\nEmail from {AppInfo.Name} works. Invites and password reset links will arrive like this one.\n",
        Html($"Email from {AppInfo.Name} works.",
            $"<p style=\"margin:0 0 16px\">Hello {Enc(userName)},</p>" +
            "<p style=\"margin:0\">Invites and password reset links will arrive like this one.</p>"));

    private static EmailMessage Build(string subject, string intro, string action, string button, string link, string footer)
    {
        var text = $"{intro}\n\n{action}\n\n{link}\n\n{footer}\n";
        var body =
            $"<p style=\"margin:0 0 16px\">{Enc(intro)}</p>" +
            $"<p style=\"margin:0 0 24px\">{Enc(action)}</p>" +
            $"<p style=\"margin:0 0 24px\"><a href=\"{Enc(link)}\" style=\"display:inline-block;background:#2f5d50;color:#ffffff;" +
            $"text-decoration:none;padding:12px 20px;border-radius:6px;font-weight:600\">{Enc(button)}</a></p>" +
            $"<p style=\"margin:0 0 16px;font-size:13px;color:#666666\">If the button doesn't work, copy this address into your browser:<br>" +
            $"<span style=\"word-break:break-all\">{Enc(link)}</span></p>" +
            $"<p style=\"margin:0;font-size:13px;color:#666666\">{Enc(footer)}</p>";
        return new EmailMessage(subject, text, Html(subject, body));
    }

    private static string Html(string title, string body) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Enc(title) + "</title></head>" +
        "<body style=\"margin:0;padding:24px;background:#f4f2ee;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#222222;font-size:15px;line-height:1.5\">" +
        "<div style=\"max-width:520px;margin:0 auto;background:#ffffff;border-radius:8px;padding:28px\">" +
        $"<p style=\"margin:0 0 20px;font-weight:700;font-size:17px\">{Enc(AppInfo.Name)}</p>" +
        body +
        "</div></body></html>";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string Describe(TimeSpan span) =>
        span.TotalDays >= 1 ? Plural((int)Math.Round(span.TotalDays), "day")
        : span.TotalHours >= 1 ? Plural((int)Math.Round(span.TotalHours), "hour")
        : Plural((int)Math.Round(span.TotalMinutes), "minute");

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
