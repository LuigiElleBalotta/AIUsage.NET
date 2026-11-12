using System.Runtime.InteropServices;
using System.Text;
using AIUsage.Core.Services;

namespace AIUsage.Core.Tests.Providers;

public sealed class WindowsCredentialAccessorTests
{
    [Fact]
    public void ServiceAccountReadUsesExistingGoKeyringTarget()
    {
        var service = "AIUsage.Tests." + Guid.NewGuid().ToString("N");
        const string account = "fixture-account";
        using var credential = new NativeCredentialFixture($"{service}:{account}", "fixture-value");
        Assert.Equal("fixture-value", new WindowsCredentialAccessor().ReadGenericPassword(service, account));
    }

    [Fact]
    public void ServiceAccountReadPrefersNativeTargetOverLegacyTarget()
    {
        var service = "AIUsage.Tests." + Guid.NewGuid().ToString("N");
        using var native = new NativeCredentialFixture($"{service}:account", "native-value");
        using var legacy = new NativeCredentialFixture($"AIUsage:{service}:account", "legacy-value");
        Assert.Equal("native-value", new WindowsCredentialAccessor().ReadGenericPassword(service, "account"));
    }

    [Fact]
    public void ServiceAccountReadFallsBackToLegacyTarget()
    {
        var service = "AIUsage.Tests." + Guid.NewGuid().ToString("N");
        using var legacy = new NativeCredentialFixture($"AIUsage:{service}:account", "legacy-value");
        Assert.Equal("legacy-value", new WindowsCredentialAccessor().ReadGenericPassword(service, "account"));
    }

    [Fact]
    public void MissingServiceAccountReturnsNull()
    {
        var service = "AIUsage.Tests." + Guid.NewGuid().ToString("N");
        Assert.Null(new WindowsCredentialAccessor().ReadGenericPassword(service, "account"));
    }

    [Fact]
    public void SingleServiceReadKeepsPrivateNamespace()
    {
        var service = "AIUsage.Tests." + Guid.NewGuid().ToString("N");
        using var native = new NativeCredentialFixture(service, "external-value");
        using var owned = new NativeCredentialFixture($"AIUsage:{service}", "private-value");
        Assert.Equal("private-value", new WindowsCredentialAccessor().ReadGenericPassword(service));
    }

    // Unique disposable entries test the native boundary without touching user credentials.
    private sealed class NativeCredentialFixture : IDisposable
    {
        private readonly string _target;
        public NativeCredentialFixture(string target, string value)
        {
            _target = target;
            var bytes = Encoding.UTF8.GetBytes(value);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = 1, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob, Persist = 1, UserName = "aiusage-test"
                };
                if (!CredWrite(ref credential, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(blob); }
        }
        public void Dispose()
        {
            if (!CredDelete(_target, 1, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, uint type, uint flags);
    }
}
