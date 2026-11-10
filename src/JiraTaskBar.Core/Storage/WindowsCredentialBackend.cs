using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JiraTaskBar.Core.Storage;

/// <summary>Minimal backend abstraction so credential migration can be tested without using a real user vault.</summary>
public interface ICredentialBackend
{
    byte[]? Read();
    void Write(byte[] credential);
    void Delete();
}

public sealed class CredentialBackendException : Exception
{
    public CredentialBackendException(string message) : base(message) { }
    public CredentialBackendException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Stores one application credential as a Windows Generic Credential.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialBackend : ICredentialBackend
{
    public const string TargetName = "JiraTaskBar/JiraCloud";
    private const int ErrorNotFound = 1168;
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    public byte[]? Read()
    {
        if (!CredRead(TargetName, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize == 0) return [];
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    public void Write(byte[] credential)
    {
        var blob = Marshal.AllocCoTaskMem(credential.Length);
        try
        {
            Marshal.Copy(credential, 0, blob, credential.Length);
            var native = new NativeCredential
            {
                Type = Generic,
                TargetName = TargetName,
                CredentialBlobSize = credential.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "JiraTaskBar",
            };
            if (!CredWrite(ref native, 0))
                throw Failure("write", Marshal.GetLastWin32Error());
        }
        finally
        {
            for (var index = 0; index < credential.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (CredDelete(TargetName, Generic, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw Failure("delete", error);
    }

    private static CredentialBackendException Failure(string operation, int error) =>
        new($"Windows Credential Manager could not {operation} the JiraTaskBar credential.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
