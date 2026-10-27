// DPAPI's contract on a machine without DPAPI: macOS and the browser.
//
// A blob is laid out as Windows lays out a DPAPI blob (DPAPI_BLOB: version 1, the provider
// df9d8cd0-1501-11d1-8c7a-00c04fc297eb, the master key's GUID, flags, description, AES-256, salt,
// SHA-512, HMAC key, data, signature), so code that recognises one by its first bytes -- the base64
// "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA" -- still does. The master key is this machine's, 32 random bytes
// per scope (KeyStore below), and from it and the blob's salt and the caller's entropy:
//
//   session = HMAC-SHA512(master, salt | entropy)       AES-256-CBC key = session[0..32], IV = session[32..48]
//   signing = HMAC-SHA512(master, hmacKey | entropy)    signature = HMAC-SHA512(signing, the blob up to it)
//
// The derivation is not Windows' (its master keys are sealed with the logon password, which there is
// none of here), and a blob from a Windows machine cannot be opened, as it cannot on another Windows
// account. AES is the managed one (AesCore): the browser's .NET refuses Aes and AesGcm alike.
//
// Unprotect reports as Windows does: another key's blob is "Key not valid for use in specified state."
// (NTE_BAD_KEY_STATE), the wrong entropy or a changed byte "The data is invalid." (ERROR_INVALID_DATA).
// The scope it is given does not matter, as it does not to CryptUnprotectData: the blob says whose it is.
//
// Blobs of the shim's first format ("WMDP", AES-GCM) are still opened where AES-GCM runs (macOS).

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using WpfWebGpu.Crypto;

namespace System.Security.Cryptography
{
    public static partial class ProtectedData
    {
        private static readonly Guid Provider = new Guid("df9d8cd0-1501-11d1-8c7a-00c04fc297eb");
        private const int CALG_AES_256 = 0x6610, CALG_SHA_512 = 0x800E;
        private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;
        private const int SaltSize = 32, HmacKeySize = 32, SignSize = 64, KeySize = 32;
        // everything but the data: the header up to dwDataLen (an empty description), and dwSignLen + pbSign
        private const int Overhead = 4 + 16 + 4 + 16 + 4 + 4 + 2 + 4 + 4 + 4 + SaltSize + 4 + 4 + 4 + 4 + HmacKeySize + 4 + 4 + SignSize;

        public static byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)
        {
            ArgumentNullException.ThrowIfNull(userData);
            return Protect(userData.AsSpan(), scope, optionalEntropy);
        }

        public static byte[] Protect(ReadOnlySpan<byte> userData, DataProtectionScope scope, ReadOnlySpan<byte> optionalEntropy = default)
        {
            var result = new byte[ProtectedLength(userData.Length)];
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
            int length = ProtectedLength(userData.Length);
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
            CheckScope(scope);
            return UnprotectCore(encryptedData, optionalEntropy);
        }

        public static int Unprotect(ReadOnlySpan<byte> encryptedData, DataProtectionScope scope, Span<byte> destination, ReadOnlySpan<byte> optionalEntropy = default)
        {
            if (!TryUnprotect(encryptedData, scope, destination, out int written, optionalEntropy))
                throw new ArgumentException("Destination is too short.", nameof(destination));
            return written;
        }

        public static bool TryUnprotect(ReadOnlySpan<byte> encryptedData, DataProtectionScope scope, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> optionalEntropy = default)
        {
            byte[] plain = Unprotect(encryptedData, scope, optionalEntropy);
            try
            {
                if (destination.Length < plain.Length)
                {
                    bytesWritten = 0;
                    return false;
                }
                plain.CopyTo(destination);
                bytesWritten = plain.Length;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        // PKCS #7 always adds at least one byte
        private static int ProtectedLength(int plain) => Overhead + (plain / 16 + 1) * 16;

        private static void CheckScope(DataProtectionScope scope)
        {
            if (scope != DataProtectionScope.CurrentUser && scope != DataProtectionScope.LocalMachine)
                throw new ArgumentOutOfRangeException(nameof(scope));
        }

        private static void ProtectCore(ReadOnlySpan<byte> plain, DataProtectionScope scope, ReadOnlySpan<byte> entropy, Span<byte> output)
        {
            CheckScope(scope);
            byte[] master = KeyStore.Key(scope);
            int at = 0;
            void U32(Span<byte> o, int v) { BinaryPrimitives.WriteInt32LittleEndian(o.Slice(at), v); at += 4; }
            U32(output, 1);
            Provider.TryWriteBytes(output.Slice(at)); at += 16;
            U32(output, 1);
            MasterKeyId(master).TryWriteBytes(output.Slice(at)); at += 16;
            U32(output, scope == DataProtectionScope.LocalMachine ? CRYPTPROTECT_LOCAL_MACHINE : 0);
            U32(output, 2);
            output[at++] = 0; output[at++] = 0;                     // the description: "" and its terminator
            U32(output, CALG_AES_256);
            U32(output, 256);
            U32(output, SaltSize);
            Span<byte> salt = output.Slice(at, SaltSize); at += SaltSize;
            RandomNumberGenerator.Fill(salt);
            U32(output, 0);                                         // no HMAC key
            U32(output, CALG_SHA_512);
            U32(output, 512);
            U32(output, HmacKeySize);
            Span<byte> hmacKey = output.Slice(at, HmacKeySize); at += HmacKeySize;
            RandomNumberGenerator.Fill(hmacKey);
            int dataLength = (plain.Length / 16 + 1) * 16;
            U32(output, dataLength);
            Encrypt(master, salt, entropy, plain, output.Slice(at, dataLength));
            at += dataLength;
            Span<byte> sign = output.Slice(at + 4, SignSize);
            Sign(master, hmacKey, entropy, output.Slice(0, at), sign);
            U32(output, SignSize);
        }

        private static byte[] UnprotectCore(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy)
        {
            if (blob.Length >= 4 && blob.StartsWith("WMDP"u8)) return UnprotectFirstFormat(blob, entropy);
            int at = 0;
            bool ok = true;
            int U32(ReadOnlySpan<byte> b)
            {
                if (at + 4 > b.Length) { ok = false; return 0; }
                int v = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(at));
                at += 4;
                return v;
            }
            ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> b, int n)
            {
                if (!ok || n < 0 || at + n > b.Length) { ok = false; return default; }
                ReadOnlySpan<byte> r = b.Slice(at, n);
                at += n;
                return r;
            }
            int version = U32(blob);
            ReadOnlySpan<byte> provider = Bytes(blob, 16);
            U32(blob);
            ReadOnlySpan<byte> masterId = Bytes(blob, 16);
            int flags = U32(blob);
            Bytes(blob, U32(blob));                                 // description
            int alg = U32(blob), algLength = U32(blob);
            ReadOnlySpan<byte> salt = Bytes(blob, U32(blob));
            Bytes(blob, U32(blob));                                 // HMAC key
            int hash = U32(blob), hashLength = U32(blob);
            ReadOnlySpan<byte> hmacKey = Bytes(blob, U32(blob));
            ReadOnlySpan<byte> data = Bytes(blob, U32(blob));
            int signed = at;
            ReadOnlySpan<byte> sign = Bytes(blob, U32(blob));
            if (!ok || version != 1 || at != blob.Length || new Guid(provider) != Provider) throw Invalid();

            byte[] master = KeyStore.Key((flags & CRYPTPROTECT_LOCAL_MACHINE) != 0 ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
            if (new Guid(masterId) != MasterKeyId(master)) throw BadKeyState();
            if (alg != CALG_AES_256 || algLength != 256 || hash != CALG_SHA_512 || hashLength != 512
                || salt.Length != SaltSize || hmacKey.Length != HmacKeySize || sign.Length != SignSize || data.Length == 0 || data.Length % 16 != 0)
                throw Invalid();
            Span<byte> expected = stackalloc byte[SignSize];
            Sign(master, hmacKey, entropy, blob.Slice(0, signed), expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, sign)) throw Invalid();
            return Decrypt(master, salt, entropy, data);
        }

        private static Guid MasterKeyId(byte[] master)
        {
            Span<byte> h = stackalloc byte[32];
            SHA256.HashData(master, h);
            return new Guid(h.Slice(0, 16));
        }

        private static byte[] Derive(byte[] master, ReadOnlySpan<byte> first, ReadOnlySpan<byte> entropy)
        {
            var input = new byte[first.Length + entropy.Length];
            first.CopyTo(input);
            entropy.CopyTo(input.AsSpan(first.Length));
            return HMACSHA512.HashData(master, input);
        }

        private static void Sign(byte[] master, ReadOnlySpan<byte> hmacKey, ReadOnlySpan<byte> entropy, ReadOnlySpan<byte> signed, Span<byte> sign)
        {
            byte[] key = Derive(master, hmacKey, entropy);
            HMACSHA512.HashData(key, signed, sign);
            CryptographicOperations.ZeroMemory(key);
        }

        private static void Encrypt(byte[] master, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> entropy, ReadOnlySpan<byte> plain, Span<byte> output)
        {
            byte[] session = Derive(master, salt, entropy);
            var aes = new AesCore(session.AsSpan(0, 32));
            Span<byte> chain = stackalloc byte[16], block = stackalloc byte[16];
            session.AsSpan(32, 16).CopyTo(chain);
            int pad = 16 - plain.Length % 16;
            for (int at = 0; at < output.Length; at += 16)
            {
                for (int i = 0; i < 16; i++)
                {
                    int k = at + i;
                    block[i] = (byte)((k < plain.Length ? plain[k] : (byte)pad) ^ chain[i]);
                }
                aes.Encrypt(block, output.Slice(at, 16));
                output.Slice(at, 16).CopyTo(chain);
            }
            CryptographicOperations.ZeroMemory(session);
        }

        private static byte[] Decrypt(byte[] master, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> entropy, ReadOnlySpan<byte> data)
        {
            byte[] session = Derive(master, salt, entropy);
            var aes = new AesCore(session.AsSpan(0, 32));
            var plain = new byte[data.Length];
            Span<byte> chain = stackalloc byte[16];
            session.AsSpan(32, 16).CopyTo(chain);
            for (int at = 0; at < data.Length; at += 16)
            {
                aes.Decrypt(data.Slice(at, 16), plain.AsSpan(at, 16));
                for (int i = 0; i < 16; i++) plain[at + i] ^= chain[i];
                data.Slice(at, 16).CopyTo(chain);
            }
            CryptographicOperations.ZeroMemory(session);
            int pad = plain[^1];
            if (pad < 1 || pad > 16) throw Invalid();
            for (int i = plain.Length - pad; i < plain.Length; i++) if (plain[i] != pad) throw Invalid();
            byte[] result = plain.AsSpan(0, plain.Length - pad).ToArray();
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }

        // "WMDP" | version 1 | scope | nonce (12) | tag (16) | ciphertext: AES-GCM under the scope's key,
        // with the scope and the entropy as associated data
        private static byte[] UnprotectFirstFormat(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy)
        {
            const int NonceSize = 12, TagSize = 16, HeaderSize = 4 + 1 + 1 + NonceSize + TagSize;
            if (blob.Length < HeaderSize || blob[4] != 1 || blob[5] > 1 || !AesGcm.IsSupported) throw Invalid();
            var scope = (DataProtectionScope)blob[5];
            var ad = new byte[1 + entropy.Length];
            ad[0] = blob[5];
            entropy.CopyTo(ad.AsSpan(1));
            var output = new byte[blob.Length - HeaderSize];
            try
            {
                using var aes = new AesGcm(KeyStore.Key(scope), TagSize);
                aes.Decrypt(blob.Slice(6, NonceSize), blob.Slice(HeaderSize), blob.Slice(6 + NonceSize, TagSize), output, ad);
                return output;
            }
            catch (AuthenticationTagMismatchException)
            {
                throw Invalid();
            }
        }

        private static CryptographicException Invalid() => new CryptographicException("The data is invalid.");

        private static CryptographicException BadKeyState() => new CryptographicException("Key not valid for use in specified state.");

        /// <summary>The master keys, one per scope, made the first time one is asked for:
        /// <list type="bullet">
        /// <item>in WPFWEBGPU_DPAPI_DIR when it is set -- the browser's (main.js keeps the keys in the
        /// origin's private file system and puts them there before the app starts) and tests';</item>
        /// <item>on macOS in the login keychain (or the keychain file WPFWEBGPU_DPAPI_KEYCHAIN names, for
        /// tests), as generic passwords of the service "WpfWebGpu Data Protection" written and read through
        /// /usr/bin/security, so that no build of an app or of wpfrun is ever asked to be allowed in; keys
        /// an earlier version kept in files are moved there;</item>
        /// <item>else, or when the keychain cannot be used, in a folder of the user's readable by the
        /// user only.</item>
        /// </list></summary>
        private static class KeyStore
        {
            private const string Service = "WpfWebGpu Data Protection";
            private static readonly object s_gate = new object();
            private static byte[] s_user, s_machine;

            public static byte[] Key(DataProtectionScope scope)
            {
                lock (s_gate)
                {
                    ref byte[] key = ref scope == DataProtectionScope.CurrentUser ? ref s_user : ref s_machine;
                    return key ??= Load(scope);
                }
            }

            private static byte[] Load(DataProtectionScope scope)
            {
                string file = scope == DataProtectionScope.CurrentUser ? "user.key" : "machine.key";
                string dir = Environment.GetEnvironmentVariable("WPFWEBGPU_DPAPI_DIR");
                if (!string.IsNullOrEmpty(dir)) return FromFile(dir, file);

                string support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "WpfWebGpu");
                if (OperatingSystem.IsMacOS())
                {
                    string account = scope.ToString();
                    // where wpfrun pointed the shim, and the shim's own folder before that
                    string[] earlier = { Path.Combine(support, "dpapi", file), Path.Combine(support, "DataProtection", file) };
                    byte[] key = FromKeychain(account, earlier);
                    if (key != null) return key;
                    Console.Error.WriteLine("WpfWebGpu: the login keychain cannot be used; data protection keys are kept in " + Path.Combine(support, "dpapi"));
                    return FromFile(Path.Combine(support, "dpapi"), file);
                }
                return FromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfWebGpu", "dpapi"), file);
            }

            private static byte[] FromKeychain(string account, string[] earlier)
            {
                string keychain = Environment.GetEnvironmentVariable("WPFWEBGPU_DPAPI_KEYCHAIN");
                string[] Find() => string.IsNullOrEmpty(keychain)
                    ? new[] { "find-generic-password", "-s", Service, "-a", account, "-w" }
                    : new[] { "find-generic-password", "-s", Service, "-a", account, "-w", keychain };
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var (code, output) = Security(null, Find());
                    if (code == 0)
                    {
                        try
                        {
                            byte[] found = Convert.FromBase64String(output.Trim());
                            if (found.Length == KeySize) return found;
                        }
                        catch (FormatException) { }
                        throw new CryptographicException($"The data protection key \"{Service}\" / {account} in the login keychain is damaged.");
                    }
                    if (code != 44) return null;                    // errSecItemNotFound is 44; anything else: no keychain

                    byte[] key = null;
                    string from = null;
                    foreach (string path in earlier)
                    {
                        if (!File.Exists(path)) continue;
                        byte[] old = File.ReadAllBytes(path);
                        if (old.Length == KeySize) { key = old; from = path; break; }
                    }
                    key ??= RandomNumberGenerator.GetBytes(KeySize);
                    // through security's standard input, so the key is never on a command line
                    string command = $"add-generic-password -s \"{Service}\" -a \"{account}\" -l \"{Service} ({account})\" -w \"{Convert.ToBase64String(key)}\""
                        + (string.IsNullOrEmpty(keychain) ? "" : $" \"{keychain}\"") + "\n";
                    (code, _) = Security(command, "-i");
                    if (code == 0)
                    {
                        if (from != null)
                        {
                            // moved: the file goes once the keychain hands the same key back
                            var (check, again) = Security(null, Find());
                            if (check == 0 && again.Trim() == Convert.ToBase64String(key)) File.Delete(from);
                        }
                        return key;
                    }
                    if (code != 45) return null;                    // 45: another process added it first; read theirs
                }
                return null;
            }

            private static (int Code, string Output) Security(string input, params string[] args)
            {
                try
                {
                    var psi = new ProcessStartInfo("/usr/bin/security")
                    {
                        RedirectStandardInput = input != null,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    foreach (string a in args) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi);
                    if (input != null)
                    {
                        p.StandardInput.Write(input);
                        p.StandardInput.Close();
                    }
                    var stderr = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    stderr.Wait();
                    // in interactive mode a failed command still exits 0 unless it says otherwise
                    if (input != null && p.ExitCode == 0 && stderr.Result.Contains("returned -25299")) return (45, output);
                    return (p.ExitCode, output);
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return (-1, "");
                }
            }

            private static byte[] FromFile(string dir, string name)
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    if (!OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
                        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
                        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        using (var stream = new FileStream(path, options)) stream.Write(key);
                        if (OperatingSystem.IsBrowser())
                            Console.Error.WriteLine($"WpfWebGpu: a data protection key was made in memory ({path}); what is protected with it cannot be read after the page reloads");
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
}
