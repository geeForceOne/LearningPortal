using System.Reflection;

namespace LearningPortal.Core;

// The product name and tagline as people see them: page titles, headers, emails. Change the
// name here only. Internal identifiers that merely contain "LearningPortal" (namespaces, the
// configuration section, the Data Protection application name and protector purposes) are not the
// product name and must stay as they are: changing the Data Protection ones would make every saved
// API key and SMTP password unreadable.
public static class AppInfo
{
    public const string Name = "Recall";
    public const string Tagline = "Turn your own notes into practice exams, and learn from every answer.";

    // Shown quietly under the name on the sign-in pages. "Deticated" is spelled that way on
    // purpose (an inside joke): don't correct it.
    public const string Dedication = "Deticated to Bellissima. Obviously.";

    // The API key warning, shown in Settings and in the invite email so both say the same.
    public const string KeySafetyTitle = "Protect your API key";
    public const string KeySafety =
        Name + " hasn't been tested against deliberate attacks, so treat an API key you enter here as " +
        "something that could leak. Before adding one, set a monthly spending limit with your AI provider, " +
        "so a leaked key can't cost more than you're prepared to lose, and ideally give the key an expiry " +
        "date. If you'd rather not take that risk, run your own copy of " + Name + " that isn't reachable " +
        "from the public internet (home network or VPN only).";

    // The running version, e.g. "1.2.0", stamped in at build time from the release tag; "dev" for
    // local builds. Read from the app (entry) assembly, where the host project sets it.
    public static string Version { get; } = ReadVersion();

    // The day the app was built, as "2026-09-25" (UTC), stamped in by the host project; null when
    // the build didn't record it.
    public static string? BuildDate { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Drop any "+<commit>" build metadata; the release number is what people need to see.
        return string.IsNullOrWhiteSpace(version) ? "dev" : version.Split('+')[0];
    }
}
