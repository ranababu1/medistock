using System.IO;

namespace MediStock.Application.Configuration;

/// <summary>
/// Where exported documents are written. One definition is shared by the receipt writer and the report
/// exporter so that "the folder the export went to" cannot mean two different places depending on which
/// screen produced the file.
/// </summary>
public static class OutputFolder
{
    /// <summary>
    /// Resolves a configured folder to a full path. A relative path is resolved against the user's
    /// documents folder, so a relative default does not depend on where the executable was started from.
    /// </summary>
    public static string Resolve(string configured, string fallback)
    {
        var folder = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

        return Path.GetFullPath(
            Path.IsPathRooted(folder)
                ? folder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), folder));
    }

    /// <summary>
    /// Replaces anything a file system would object to, so a stored document number can be used as a
    /// file name without being trusted.
    /// </summary>
    public static string SafeFileName(string? value, string fallback)
    {
        var safe = new string([.. (value ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '\\' or '/' or ':'
                ? '_'
                : character)]);

        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
}
