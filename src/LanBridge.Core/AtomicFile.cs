using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LanBridge.Core;

internal static class AtomicFile
{
    public static void Publish(string temporary, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporary, destination, overwrite: true);
            return;
        }

        // MoveFileEx cannot replace an open destination, even with FILE_SHARE_DELETE.
        // ReplaceFile can expose a missing-name interval. Windows 10+ POSIX rename keeps
        // the directory entry continuously present while old readers retain the old file.
        const uint deleteAccess = 0x00010000;
        using var handle = CreateFile(ExtendedPath(temporary), deleteAccess,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastError("无法打开临时配置文件以原子替换。");

        byte[] name = Encoding.Unicode.GetBytes(ExtendedPath(destination));
        int lengthOffset = 2 * IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] information = new byte[nameOffset + name.Length + sizeof(char)];
        // FILE_RENAME_INFO: DWORD flags; aligned HANDLE RootDirectory; DWORD length; WCHAR name[].
        BinaryPrimitives.WriteUInt32LittleEndian(information, 0x1 | 0x2); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(lengthOffset), name.Length);
        name.CopyTo(information, nameOffset);
        // The content was flushed before this handle opened. Request DELETE only so
        // new readers may open the new snapshot even before the rename handle closes.
        if (!SetFileInformationByHandle(handle, 22, information, (uint)information.Length))
            ThrowLastError("无法原子替换规则配置；原配置未被截断。");
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static void ThrowLastError(string message)
    {
        int error = Marshal.GetLastPInvokeError();
        throw new IOException(message, new Win32Exception(error));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        [In] byte[] information, uint size);
}
