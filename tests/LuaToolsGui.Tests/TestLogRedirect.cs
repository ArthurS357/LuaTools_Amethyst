using System.IO;
using System.Runtime.CompilerServices;
using LuaToolsGui.Services;

namespace LuaToolsGui.Tests;

/// <summary>
/// §5.1. <see cref="AppLog.FilePath"/> is fixed at <c>%AppData%\LuaToolsGui\plugin-backend.log</c>, so the suite
/// used to append to the developer's real log. A module initializer runs when the test assembly loads —
/// before any test or fixture can touch <see cref="AppLog"/> — and points it at a per-run temp directory.
/// </summary>
internal static class TestLogRedirect
{
    internal static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests-log", Guid.NewGuid().ToString("N"));

    /// <summary>Read the log while other tests may be appending to it (FileShare.ReadWrite, or the read races
    /// the writer into a sharing violation).</summary>
    internal static string ReadLog()
    {
        using var fs = new FileStream(AppLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    internal static string[] ReadLogLines() =>
        ReadLog().Split("\r\n").SelectMany(s => s.Split('\n')).ToArray();

    [ModuleInitializer]
    internal static void Init()
    {
        AppLog.FilePath = Path.Combine(Dir, "plugin-backend.log");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* best effort */ }
        };
    }
}
