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

    // The running version, e.g. "1.2.0", stamped in at build time from the release tag; "dev" for
    // local builds. Read from the app (entry) assembly, where the host project sets it.
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Drop any "+<commit>" build metadata; the release number is what people need to see.
        return string.IsNullOrWhiteSpace(version) ? "dev" : version.Split('+')[0];
    }
}
