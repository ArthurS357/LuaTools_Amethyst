using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LuaToolsGui.Services;

/// <summary>
/// Creates and removes NTFS directory junctions without a shell.
///
/// <para>
/// Three places used to shell out to <c>cmd.exe /c mklink /j "&lt;path&gt;"</c> and
/// <c>cmd.exe /c rmdir "&lt;path&gt;"</c> with the path interpolated into the command string. That path is
/// derived from <c>SteamPathOverride</c> in settings.json — a file any process running as the user can
/// write — so a quote in it closed the literal and appended a second command that cmd.exe would run. The
/// previous mitigation rejected paths containing a quote, which worked but left the injection-prone shape
/// in place. There is no shell here at all, so the whole class of bug is gone rather than filtered.
/// </para>
///
/// <para>
/// <b>Why a junction and not <see cref="Directory.CreateSymbolicLink"/>.</b> They are not interchangeable
/// for this use. A directory symbolic link on Windows requires SeCreateSymbolicLinkPrivilege — that is,
/// administrator rights or Developer Mode — and this app deliberately runs <c>asInvoker</c> and never
/// elevates (see app.manifest). A junction needs no privilege at all. Substituting a symlink would turn a
/// feature that works for every user into one that fails for most of them.
/// </para>
///
/// <para>
/// The .NET BCL exposes no junction-creation API, which is why the shell was used originally. It is
/// reachable directly: create an empty directory, then attach a mount-point reparse point to it with
/// <c>FSCTL_SET_REPARSE_POINT</c>. Removal needs no interop — <see cref="Directory.Delete(string, bool)"/>
/// on .NET Core 3.0+ calls <c>RemoveDirectory</c>, which severs a reparse point instead of recursing into
/// its target. (The <c>rmdir</c> shell-out was working around a .NET Framework-era quirk that no longer
/// applies; <see cref="Remove"/> is covered by a test asserting the target's contents survive.)
/// </para>
/// </summary>
internal static class DirectoryJunction
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;  // required to open a DIRECTORY handle
    private const uint FileFlagOpenReparsePoint = 0x00200000; // operate on the link, never its target

    /// <summary>Byte offset of PathBuffer inside REPARSE_DATA_BUFFER: an 8-byte header (tag, data length,
    /// reserved) followed by the mount-point block's four 16-bit offset/length fields.</summary>
    private const int PathBufferOffset = 16;

    /// <summary>Size of the four offset/length fields that precede PathBuffer, counted by ReparseDataLength.</summary>
    private const int MountPointHeaderSize = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// True when something exists at <paramref name="path"/> and it is a reparse point (junction or
    /// symlink) — not merely "something is there". A plain leftover file or directory returns false, which
    /// is what lets a caller tell a real junction apart from cruft that has to be cleared first.
    /// </summary>
    public static bool Exists(string path)
    {
        try
        {
            return (Directory.Exists(path) || File.Exists(path))
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Point <paramref name="path"/> at <paramref name="target"/> as a junction. The target does not have
    /// to exist — a deliberately dangling junction is a legitimate marker, and is what the CDP marker is.
    /// Returns false on any failure (invalid path characters, permissions, a non-NTFS volume) without
    /// throwing, and leaves nothing behind when it fails.
    /// </summary>
    public static bool Create(string path, string target)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(target)) return false;

        bool createdDirectory = false;
        try
        {
            // A junction is an EMPTY DIRECTORY carrying a reparse point, so the directory comes first.
            // A path Windows will not accept — one containing a quote, for instance — fails right here,
            // which is exactly the input the old shell form would have executed.
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                createdDirectory = true;
            }

            using SafeFileHandle handle = CreateFile(
                path, GenericWrite, dwShareMode: 0, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

            if (handle.IsInvalid) return CleanUp(path, createdDirectory);

            byte[] buffer = BuildMountPointBuffer(target);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                return CleanUp(path, createdDirectory);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return CleanUp(path, createdDirectory);
        }
    }

    /// <summary>
    /// Remove the junction at <paramref name="path"/>, severing the link and leaving whatever it pointed at
    /// untouched. Returns false if nothing is there or the link could not be removed.
    /// </summary>
    public static bool Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        try
        {
            // recursive:false is load-bearing, not a default: RemoveDirectory detaches a reparse point, so
            // the target keeps its contents. Passing true would enumerate THROUGH the junction and delete
            // the target's files instead.
            Directory.Delete(path, recursive: false);
            return !Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Delete the empty directory this call created, so a failure leaves no half-made marker.</summary>
    private static bool CleanUp(string path, bool createdDirectory)
    {
        if (createdDirectory)
            try { Directory.Delete(path, recursive: false); } catch (IOException) { /* best effort */ }
        return false;
    }

    /// <summary>
    /// Lay out a REPARSE_DATA_BUFFER for a mount point. The substitute name is the NT-namespace form
    /// (<c>\??\C:\…</c>) the object manager resolves; the print name is the plain path tools display. Both
    /// are counted in BYTES excluding their terminating null, while the buffer still carries those nulls.
    /// </summary>
    private static byte[] BuildMountPointBuffer(string target)
    {
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));

        int pathBytes = substitute.Length + 2 + print.Length + 2;
        int dataLength = MountPointHeaderSize + pathBytes;
        byte[] buffer = new byte[PathBufferOffset + pathBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        // bytes 6..8 are Reserved and stay zero
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);

        substitute.CopyTo(buffer, PathBufferOffset);
        print.CopyTo(buffer, PathBufferOffset + substitute.Length + 2);
        return buffer;
    }
}
