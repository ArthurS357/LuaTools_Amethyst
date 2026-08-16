using System.Reflection;

namespace LuaToolsGui.Services;

/// <summary>What kind of build is running.</summary>
public enum BuildKind
{
    /// <summary>This fork: the Amethyst marker is present.</summary>
    Amethyst,

    /// <summary>The marker is absent — an official/upstream build, or a rebuild that dropped it.</summary>
    Unrecognized,
}

/// <summary>
/// Answers "is the binary that is running actually LuaTools Amethyst?".
///
/// <para>
/// WHY: this fork and the official build share an executable name, an install path, a settings folder and
/// a <c>luatools://</c> handler. A user who runs an official installer — deliberately or by clicking the
/// wrong download — ends up with telemetry and the DonateKeys key upload back, in a program that still
/// looks and launches exactly like the one they chose. Nothing on screen would say so.
/// </para>
///
/// <para>
/// APPROACH: a positive marker, not a hunt for upstream artefacts. The check asks "does this assembly
/// declare itself Amethyst?" via <see cref="AssemblyProductAttribute"/>, which the csproj sets to
/// <c>LuaTools Amethyst</c>. Absence is what raises the warning.
/// </para>
///
/// <para>
/// Looking for upstream leftovers instead — a telemetry URL, a DonateKeys string — was considered and
/// rejected. Those checks invert the failure mode: they are silent when upstream renames or obfuscates
/// anything, and they fire on THIS fork the moment a source comment mentions the string they scan for,
/// which is exactly the false positive the brief rules out (the fork's own AppConfig documents both
/// removed features at length). A marker the fork controls cannot be wrong about the fork.
/// </para>
///
/// <para>
/// LIMITS, stated plainly: this is an accident detector, not a tamper check. It is not a signature, and
/// anyone repackaging an official build could set the same attribute. It answers "did I install the
/// wrong thing?", which is the realistic failure — not "is someone impersonating this fork?".
/// </para>
/// </summary>
public static class BuildIdentity
{
    /// <summary>Marker value the fork's csproj stamps into <c>AssemblyProductAttribute</c>.</summary>
    public const string AmethystProductMarker = "LuaTools Amethyst";

    /// <summary>Classify a product string. Pure, so the rule is testable without an assembly.</summary>
    /// <param name="productAttribute">The assembly's product value, or null when absent.</param>
    public static BuildKind Classify(string? productAttribute) =>
        productAttribute is not null
        && productAttribute.Contains(AmethystProductMarker, StringComparison.OrdinalIgnoreCase)
            ? BuildKind.Amethyst
            : BuildKind.Unrecognized;

    /// <summary>Classify the assembly that is actually executing.</summary>
    public static BuildKind Current() =>
        Classify(Assembly.GetExecutingAssembly()
                         .GetCustomAttribute<AssemblyProductAttribute>()?.Product);
}
