using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// What currently holds the proxy-DLL slot next to <c>steam.exe</c>. Exactly one of these is true at any
/// moment, which is the whole point of the type: <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are single
/// files, so "a Mode is active" and "AmethystTool is active" cannot both be answered yes.
/// </summary>
public enum ActiveBackend
{
    /// <summary>Nothing selected — no Mode, no AmethystTool.</summary>
    None,

    /// <summary>One of the <see cref="UnlockerMode"/> cards on the Mode page.</summary>
    Mode,

    /// <summary>AmethystTool, which is not an <see cref="UnlockerMode"/> but competes for the same files.</summary>
    AmethystTool,
}

/// <summary>
/// Reads the single persisted string that says which backend owns the proxy-DLL slot.
///
/// <para>
/// <b>Why one string and not two settings.</b> The Mode cards and the AmethystTool card used to answer
/// "am I active?" from different places — the cards from <c>SelectedMode</c>, AmethystTool from whether
/// its four files happened to be present — so installing one after the other left both showing ACTIVE.
/// Two booleans can disagree; one slot cannot. AmethystTool therefore persists into the SAME
/// <c>SelectedMode</c> string, under a token no <see cref="UnlockerMode"/> member uses.
/// </para>
///
/// <para>
/// <b>Nothing about settings.json changed.</b> The field was already a free-form <c>string?</c> parsed
/// with <see cref="Enum.TryParse{T}(string, out T)"/>, and a value that is not an enum member already
/// resolved to "no Mode active". A settings file written by an older build keeps its meaning, and one
/// written by this build is read by an older build as "no Mode" rather than as the wrong Mode.
/// </para>
///
/// <para>Pure: no disk, no network, no clock. The services own the I/O.</para>
/// </summary>
public static class ActiveBackendPolicy
{
    /// <summary>
    /// The value <c>SelectedMode</c> carries while AmethystTool owns the slot. A persisted key, not a
    /// display name — renaming it silently demotes every existing AmethystTool install to "nothing
    /// selected". Deliberately not an <see cref="UnlockerMode"/> member: AmethystTool has no
    /// <see cref="ModeDefinition"/>, and every <c>Modes</c> lookup would throw on it.
    /// </summary>
    public const string AmethystToolToken = "AmethystTool";

    /// <summary>Which backend the persisted value names.</summary>
    public static ActiveBackend Resolve(string? selectedMode)
    {
        if (IsAmethystTool(selectedMode)) return ActiveBackend.AmethystTool;
        return ActiveMode(selectedMode) is not null ? ActiveBackend.Mode : ActiveBackend.None;
    }

    /// <summary>
    /// The active <see cref="UnlockerMode"/>, or null when none is — including when AmethystTool holds
    /// the slot, and when the value is a Mode this build no longer knows.
    /// </summary>
    public static UnlockerMode? ActiveMode(string? selectedMode)
    {
        // Checked before the enum parse so the token wins even if a Mode is ever named after it.
        if (IsAmethystTool(selectedMode)) return null;
        return Enum.TryParse(selectedMode, out UnlockerMode mode) && Enum.IsDefined(mode) ? mode : null;
    }

    /// <summary>Whether AmethystTool is the selected backend.</summary>
    public static bool IsAmethystTool(string? selectedMode) =>
        string.Equals(selectedMode, AmethystToolToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="candidate"/>'s files sitting in the Steam root can still be the bytes
    /// steam.exe loads, given who holds the slot.
    ///
    /// <para>
    /// <b>The one question behind both false positives.</b> <c>dwmapi.dll</c> and <c>xinput1_4.dll</c>
    /// are placed by every Mode AND by AmethystTool, so installing either over the other replaces exactly
    /// those two and leaves the loser's remaining names behind. Presence then says nothing about what is
    /// loaded: the AmethystTool card read "up to date" beside a Mode card holding the ACTIVE badge, and
    /// every Mode card read as installed beside AmethystTool, offering an Update that would hand the slot
    /// back. Only the slot can tell them apart, because only one backend ever holds it.
    /// </para>
    ///
    /// <para>
    /// <see cref="ActiveBackend.None"/> is true for everyone: nothing has claimed the slot, so the files
    /// in the root are the best evidence there is. Only a DIFFERENT backend actively owning it is a no.
    /// </para>
    ///
    /// <para>
    /// This answers "does the card say installed?" and nothing else. "Is there anything left to remove?"
    /// is a different question with a different answer — see <c>AmethystToolService.CanUninstall</c>,
    /// which must stay true precisely in the case this returns false.
    /// </para>
    /// </summary>
    public static bool StillOwnsItsFiles(ActiveBackend holder, ActiveBackend candidate) =>
        holder == ActiveBackend.None || holder == candidate;
}
