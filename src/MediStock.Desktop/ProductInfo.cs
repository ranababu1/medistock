using System.Reflection;
using System.Runtime.InteropServices;

namespace MediStock.Desktop;

/// <summary>
/// What this build is, in one place. The version comes from <c>&lt;Version&gt;</c> in
/// <c>Directory.Build.props</c>, which is the only place a version is ever written down — see
/// <c>docs/release-process.md</c>. The sign-in window and the Support tab both read it from here so
/// they can never disagree or drift from the artefact that was shipped.
/// </summary>
public static class ProductInfo
{
    /// <summary>The name a pharmacist would recognise.</summary>
    public const string Name = "MediStock";

    /// <summary>Major.minor.patch, or "unknown" if the assembly carries no version — which only happens under a designer.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>The version as an operator should see it, ready to read down a telephone.</summary>
    public static string VersionText => $"Version {Version}";

    /// <summary>Everything a support call needs about the build, in one line.</summary>
    public static string Summary => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{Name} {Version} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {Environment.MachineName}");
}
