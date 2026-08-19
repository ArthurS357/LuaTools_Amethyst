using System.Reflection;

namespace LuaToolsGui;

/// <summary>
/// The running build's own version, read once from the assembly.
///
/// <para>
/// The csproj <c>&lt;Version&gt;</c> is the single source of truth (it drives all three assembly version
/// attributes), and this is the single place that reads it back. It used to be a private helper on
/// <c>MainViewModel</c>, which was fine while the nav footer was the only consumer — but the moment a
/// second caller needs it, a copied helper is how the footer and the User-Agent start disagreeing about
/// which build is running. That drift already happened once to the version itself: the csproj sat at
/// 1.1.3 through the whole 1.2.8 release.
/// </para>
/// </summary>
internal static class AppVersion
{
    /// <summary>e.g. <c>"1.5.2"</c>. The <c>+commit</c> suffix the SDK appends is trimmed.</summary>
    public static string Current { get; } = Read();

    /// <summary>What outbound HTTP identifies this app as, e.g. <c>"LuaToolsAmethyst/1.5.2"</c>.
    /// A request with no User-Agent is indistinguishable from a scraper at the far end; naming the fork
    /// and its version also means a server-side problem can be tied to a specific build.</summary>
    public static string UserAgent { get; } = $"LuaToolsAmethyst/{Current}";

    private static string Read()
    {
        var asm = typeof(AppVersion).Assembly;
        // InformationalVersion carries the csproj <Version> (may have a "+commit" suffix — trim it).
        string ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? asm.GetName().Version?.ToString()
                     ?? "?";
        int plus = ver.IndexOf('+');
        return plus >= 0 ? ver[..plus] : ver;
    }
}
