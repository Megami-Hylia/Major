using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Major.Streaming;

internal sealed class CredentialStore
{
    private readonly string path;
    private readonly object sync = new();
    private readonly StoredIdentity identity;
    public X509Certificate2 Certificate { get; }

    public CredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(Path.GetFullPath(directory), "receiver-identity.dpapi");
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Archivio credenziali fuori limite.");
            identity = JsonSerializer.Deserialize<StoredIdentity>(Dpapi.Unprotect(File.ReadAllBytes(path))) ?? throw new InvalidDataException("Identità ricevitore non leggibile.");
            Certificate = new X509Certificate2(Convert.FromBase64String(identity.Pfx), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            if (!Certificate.HasPrivateKey) throw new InvalidDataException("Manca la chiave privata del ricevitore.");
        }
        else
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Major Receiver", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var pfx = generated.Export(X509ContentType.Pfx, "");
            // Schannel requires a user key container; omit PersistKeySet so disposal removes the import.
            Certificate = new X509Certificate2(pfx, "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            identity = new StoredIdentity { Pfx = Convert.ToBase64String(pfx) };
            Save();
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    public string AddToken(string device)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (sync)
        {
            if (identity.Tokens.Count >= 16) identity.Tokens.RemoveAt(0);
            identity.Tokens.Add(new StoredToken { Hash = HashToken(token), Device = device, Created = DateTimeOffset.UtcNow });
            Save();
        }
        return token;
    }
    public bool ContainsToken(string? token)
    {
        if (token is null || token.Length != 43 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        var hash = Convert.FromHexString(HashToken(token));
        lock (sync)
        {
            var found = false;
            foreach (var stored in identity.Tokens)
                found |= CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(stored.Hash));
            return found;
        }
    }
    public void RevokeAll() { lock (sync) { identity.Tokens.Clear(); Save(); } }
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));
    private void Save()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(identity);
        try
        {
            var encrypted = Dpapi.Protect(plaintext);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private sealed class StoredIdentity
    {
        public string Pfx { get; set; } = "";
        public List<StoredToken> Tokens { get; set; } = [];
    }
    private sealed class StoredToken
    {
        public string Hash { get; set; } = "";
        public string Device { get; set; } = "";
        public DateTimeOffset Created { get; set; }
    }

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
        public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
        private static byte[] Transform(byte[] bytes, bool encrypt)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("La protezione delle credenziali richiede Windows DPAPI.");
            var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                var success = encrypt ? CryptProtectData(ref input, "Major receiver", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Protezione credenziali Windows non riuscita.");
                var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}
