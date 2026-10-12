// DPAPI's contract on a machine without DPAPI.
//
//   blob = "WMDP" | version (1) | scope (1) | nonce (12) | tag (16) | ciphertext
//
// AES-GCM under the scope's key, with the scope and the caller's optional entropy as associated
// data: the wrong entropy, the wrong scope or a changed byte all fail authentication, and
// Unprotect reports them as Windows does, "The data is invalid."

using System.IO;

namespace System.Security.Cryptography
{
    public static partial class ProtectedData
    {
        private static ReadOnlySpan<byte> Magic => "WMDP"u8;
        private const byte Version = 1;
        private const int NonceSize = 12, TagSize = 16, KeySize = 32;
        private const int HeaderSize = 4 + 1 + 1 + NonceSize + TagSize;

        private static readonly object s_gate = new object();
        private static byte[] s_userKey, s_machineKey;

        public static byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)
        {
            ArgumentNullException.ThrowIfNull(userData);
            return Protect(userData.AsSpan(), scope, optionalEntropy);
        }

        public static byte[] Protect(ReadOnlySpan<byte> userData, DataProtectionScope scope, ReadOnlySpan<byte> optionalEntropy = default)
        {
            var result = new byte[HeaderSize + userData.Length];
            ProtectCore(userData, scope, optionalEntropy, result);
            return result;
        }

        public static int Protect(ReadOnlySpan<byte> userData, DataProtectionScope scope, Span<byte> destination, ReadOnlySpan<byte> optionalEntropy = default)
        {
            if (!TryProtect(userData, scope, destination, out int written, optionalEntropy))
                throw new ArgumentException("Destination is too short.", nameof(destination));
            return written;
        }

        public static bool TryProtect(ReadOnlySpan<byte> userData, DataProtectionScope scope, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> optionalEntropy = default)
        {
            int length = HeaderSize + userData.Length;
            if (destination.Length < length)
            {
                bytesWritten = 0;
                return false;
            }
            ProtectCore(userData, scope, optionalEntropy, destination.Slice(0, length));
            bytesWritten = length;
            return true;
        }

        public static byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope)
        {
            ArgumentNullException.ThrowIfNull(encryptedData);
            return Unprotect(encryptedData.AsSpan(), scope, optionalEntropy);
        }

        public static byte[] Unprotect(ReadOnlySpan<byte> encryptedData, DataProtectionScope scope, ReadOnlySpan<byte> optionalEntropy = default)
        {
            var result = new byte[PlainLength(encryptedData)];
            UnprotectCore(encryptedData, scope, optionalEntropy, result);
            return result;
        }

        public static int Unprotect(ReadOnlySpan<byte> encryptedData, DataProtectionScope scope, Span<byte> destination, ReadOnlySpan<byte> optionalEntropy = default)
        {
            if (!TryUnprotect(encryptedData, scope, destination, out int written, optionalEntropy))
                throw new ArgumentException("Destination is too short.", nameof(destination));
            return written;
        }

        public static bool TryUnprotect(ReadOnlySpan<byte> encryptedData, DataProtectionScope scope, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> optionalEntropy = default)
        {
            int length = PlainLength(encryptedData);
            if (destination.Length < length)
            {
                bytesWritten = 0;
                return false;
            }
            UnprotectCore(encryptedData, scope, optionalEntropy, destination.Slice(0, length));
            bytesWritten = length;
            return true;
        }

        private static void ProtectCore(ReadOnlySpan<byte> plain, DataProtectionScope scope, ReadOnlySpan<byte> entropy, Span<byte> output)
        {
            Magic.CopyTo(output);
            output[4] = Version;
            output[5] = (byte)scope;
            Span<byte> nonce = output.Slice(6, NonceSize);
            Span<byte> tag = output.Slice(6 + NonceSize, TagSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(Key(scope), TagSize);
            aes.Encrypt(nonce, plain, output.Slice(HeaderSize), tag, AssociatedData(scope, entropy));
        }

        private static void UnprotectCore(ReadOnlySpan<byte> blob, DataProtectionScope scope, ReadOnlySpan<byte> entropy, Span<byte> output)
        {
            if (blob[5] != (byte)scope) throw Invalid();
            try
            {
                using var aes = new AesGcm(Key(scope), TagSize);
                aes.Decrypt(blob.Slice(6, NonceSize), blob.Slice(HeaderSize), blob.Slice(6 + NonceSize, TagSize), output, AssociatedData(scope, entropy));
            }
            catch (AuthenticationTagMismatchException)
            {
                output.Clear();
                throw Invalid();
            }
        }

        private static int PlainLength(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < HeaderSize || !blob.StartsWith(Magic) || blob[4] != Version) throw Invalid();
            return blob.Length - HeaderSize;
        }

        private static byte[] AssociatedData(DataProtectionScope scope, ReadOnlySpan<byte> entropy)
        {
            var ad = new byte[1 + entropy.Length];
            ad[0] = (byte)scope;
            entropy.CopyTo(ad.AsSpan(1));
            return ad;
        }

        // ERROR_INVALID_DATA, as Windows' CryptUnprotectData reports a blob it cannot open.
        private static CryptographicException Invalid() => new CryptographicException("The data is invalid.");

        private static byte[] Key(DataProtectionScope scope)
        {
            if (scope != DataProtectionScope.CurrentUser && scope != DataProtectionScope.LocalMachine)
                throw new ArgumentOutOfRangeException(nameof(scope));
            lock (s_gate)
            {
                ref byte[] key = ref scope == DataProtectionScope.CurrentUser ? ref s_userKey : ref s_machineKey;
                return key ??= LoadOrCreateKey(scope == DataProtectionScope.CurrentUser ? "user.key" : "machine.key");
            }
        }

        private static byte[] LoadOrCreateKey(string name)
        {
            string dir = Environment.GetEnvironmentVariable("WPFWEBGPU_DPAPI_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "WpfWebGpu", "DataProtection");
            }
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string path = Path.Combine(dir, name);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    byte[] existing = File.ReadAllBytes(path);
                    if (existing.Length == KeySize) return existing;
                    throw new CryptographicException($"The data protection key {path} is damaged.");
                }
                catch (FileNotFoundException)
                {
                }

                byte[] key = RandomNumberGenerator.GetBytes(KeySize);
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var stream = new FileStream(path, options)) stream.Write(key);
                    return key;
                }
                catch (IOException) when (File.Exists(path) && attempt < 3)
                {
                    // Another process made it first; use theirs.
                }
            }
        }
    }
}
