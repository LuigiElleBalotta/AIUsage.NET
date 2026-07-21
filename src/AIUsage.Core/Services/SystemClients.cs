using System.Runtime.InteropServices;
using System.Text;
using AIUsage.Core.Support;

namespace AIUsage.Core.Services;

public interface IEnvironmentReading
{
    string? Value(string name);
}

/// <summary>Reads process environment variables — the Windows counterpart of ProcessEnvironmentReader.
/// Unlike macOS, a Windows app launched from Explorer/Start Menu DOES inherit the user's persisted
/// environment variables (HKCU\Environment), so no login-shell capture step is needed here.</summary>
public sealed class ProcessEnvironmentReader : IEnvironmentReading
{
    public string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        return value?.NilIfEmpty();
    }
}

public interface ITextFileAccessing
{
    bool Exists(string path);
    string? ReadTextIfPresent(string path);
    string ReadText(string path);
    void WriteText(string path, string text);
    void Remove(string path);
}

/// <summary>
/// Local file accessor. Credential files are written atomically (temp file + rename) with an ACL that
/// restricts access to the current user only — the Windows analog of the macOS 0600 mode.
/// </summary>
public sealed class LocalTextFileAccessor : ITextFileAccessing
{
    public bool Exists(string path) => File.Exists(ExpandHome(path));

    public string ReadText(string path) => File.ReadAllText(ExpandHome(path), Encoding.UTF8);

    public string? ReadTextIfPresent(string path)
    {
        var expanded = ExpandHome(path);
        return File.Exists(expanded) ? File.ReadAllText(expanded, Encoding.UTF8) : null;
    }

    public void WriteText(string path, string text)
    {
        var expanded = ExpandHome(path);
        var dir = Path.GetDirectoryName(expanded);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir ?? ".", $".{Path.GetFileName(expanded)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text, Encoding.UTF8);
        try
        {
            RestrictToCurrentUser(temp);
        }
        catch
        {
            // best effort ACL hardening; never block a credential write on it
        }
        if (File.Exists(expanded)) File.Delete(expanded);
        File.Move(temp, expanded);
    }

    public void Remove(string path)
    {
        var expanded = ExpandHome(path);
        if (File.Exists(expanded)) File.Delete(expanded);
    }

    private static void RestrictToCurrentUser(string path)
    {
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        security.SetAccessRuleProtection(true, false);
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                currentUser,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        }
        fileInfo.SetAccessControl(security);
    }

    public static string ExpandHomeStatic(string path) => ExpandHome(path);

    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..].Replace('/', Path.DirectorySeparatorChar));
        }
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}

public static class PathHelpers
{
    public static string ExpandHome(string path) => LocalTextFileAccessor.ExpandHomeStatic(path);
}

/// <summary>
/// Windows Credential Manager accessor — the direct counterpart of the macOS `security` keychain CLI
/// wrapper. Uses CredRead/CredWrite/CredDelete via P/Invoke (advapi32) against the "generic" credential
/// type, keyed by a target name (equivalent to the Keychain "service").
/// </summary>
public interface IKeychainAccessing
{
    string? ReadGenericPassword(string service);
    void WriteGenericPassword(string service, string value);
    bool? GenericPasswordExists(string service);
    string? ReadGenericPassword(string service, string account);
}

public sealed class WindowsCredentialAccessor : IKeychainAccessing
{
    private const string TargetPrefix = "AIUsage:";

    public string? ReadGenericPassword(string service) => ReadInternal(TargetPrefix + service);

    public string? ReadGenericPassword(string service, string account) => ReadInternal($"{TargetPrefix}{service}:{account}");

    public void WriteGenericPassword(string service, string value) => WriteInternal(TargetPrefix + service, value);

    public bool? GenericPasswordExists(string service)
    {
        try
        {
            return ReadGenericPassword(service) is not null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadInternal(string target)
    {
        if (!CredRead(target, CredType.Generic, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            AppLog.Warn(LogTag.CredentialStore, $"credential read failed for target (win32 error {error})");
            return null;
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static void WriteInternal(string target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CredType.Generic,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersist.LocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref cred, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CredWrite failed (win32 error {error})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private const int ErrorNotFound = 1168;

    private enum CredType : uint
    {
        Generic = 1
    }

    private enum CredPersist : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CredType Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, CredType type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, CredType type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

public interface ISqliteAccessing
{
    string? QueryValue(string path, string sql);
    void Execute(string path, string sql);
}

/// <summary>SQLite access via Microsoft.Data.Sqlite (no external sqlite3 CLI needed on Windows, unlike macOS).</summary>
public sealed class SqliteDataAccessor : ISqliteAccessing
{
    public string? QueryValue(string path, string sql)
    {
        var expanded = PathHelpers.ExpandHome(path);
        if (!File.Exists(expanded)) return null;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={expanded};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        var text = result?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public void Execute(string path, string sql)
    {
        var expanded = PathHelpers.ExpandHome(path);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={expanded}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
