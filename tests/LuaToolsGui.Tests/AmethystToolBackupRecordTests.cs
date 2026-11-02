using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The AmethystTool install record's backup folder after that folder is deleted (Settings → Maintenance
/// removes exactly these). The Mode card used to keep showing "Backup: &lt;path&gt;" for a folder that was gone.
/// </summary>
public sealed class AmethystToolBackupRecordTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _steam;
    private readonly string _manifest;

    public AmethystToolBackupRecordTests()
    {
        _steam = Path.Combine(_tmp, "Steam");
        Directory.CreateDirectory(_steam);
        _manifest = Path.Combine(_tmp, "state", "installed.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private void WriteRecord(string? backupDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifest)!);
        File.WriteAllText(_manifest, JsonSerializer.Serialize(
            new { Tag = "v1.3.0", ZipSha = "abc", BackupDirectory = backupDirectory }));
    }

    private JsonElement Record() => JsonDocument.Parse(File.ReadAllText(_manifest)).RootElement;

    [Fact]
    public void An_existing_backup_folder_is_reported_and_kept()
    {
        string backup = Path.Combine(_steam, "AmethystTool-backup-20261020-141123");
        Directory.CreateDirectory(backup);
        WriteRecord(backup);

        AmethystToolService.LiveBackupDirectory(_manifest).Should().Be(backup);
        Record().GetProperty("BackupDirectory").GetString().Should().Be(backup);
    }

    [Fact]
    public void A_deleted_backup_folder_is_hidden_and_dropped_from_the_record()
    {
        // The folder was there at install time and has since been deleted by the backup cleanup.
        string backup = Path.Combine(_steam, "AmethystTool-backup-20261020-141123");
        WriteRecord(backup);

        AmethystToolService.LiveBackupDirectory(_manifest).Should().BeNull();

        var record = Record();
        record.GetProperty("BackupDirectory").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("Tag").GetString().Should().Be("v1.3.0", "only the stale folder is forgotten");
        record.GetProperty("ZipSha").GetString().Should().Be("abc");
    }

    [Fact]
    public void A_backup_on_a_missing_drive_is_hidden_but_not_forgotten()
    {
        // Steam root itself absent (library on an unplugged drive): the folder may still exist on that drive.
        string backup = Path.Combine(_tmp, "UnpluggedDrive", "Steam", "AmethystTool-backup-20261020-141123");
        WriteRecord(backup);

        AmethystToolService.LiveBackupDirectory(_manifest).Should().BeNull();
        Record().GetProperty("BackupDirectory").GetString().Should().Be(backup);
    }

    [Fact]
    public void No_record_or_no_backup_is_null()
    {
        AmethystToolService.LiveBackupDirectory(_manifest).Should().BeNull();

        WriteRecord(null);
        AmethystToolService.LiveBackupDirectory(_manifest).Should().BeNull();
    }
}
