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
}
