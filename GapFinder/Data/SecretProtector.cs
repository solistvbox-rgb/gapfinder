using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GapFinder.Data
{
    /// <summary>
    /// Encrypts API keys for GapFinder.xml: AES-256-CBC + HMAC-SHA256 (netstandard2.0 has no AesGcm).
    /// The random key is stored separately from the XML in data/GapFinder/secret.key - someone who only sees the
    /// XML (e.g. a posted configuration) cannot read the API key. It does not protect against access to the whole
    /// server: the server needs the key in plain text to call Sonarr/Radarr.
    /// Format: "emby-encrypted:v1:" + Base64(IV[16] + ciphertext + HMAC[32] over IV+ciphertext).
    /// </summary>
    public static class SecretProtector
    {
        public const string Prefix = "emby-encrypted:v1:";
        private const int KeyFileLength = 64; // 32 bytes AES + 32 bytes HMAC
        private static readonly object Sync = new object();

        private static string KeyFilePath => Path.Combine(Plugin.Instance.DataFolder, "secret.key");

        public static bool IsProtected(string value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }
            var key = GetOrCreateKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key.Take(32).ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] cipher;
                using (var encryptor = aes.CreateEncryptor())
                {
                    var data = Encoding.UTF8.GetBytes(plain);
                    cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
                }
                var body = aes.IV.Concat(cipher).ToArray();
                using (var hmac = new HMACSHA256(key.Skip(32).ToArray()))
                {
                    return Prefix + Convert.ToBase64String(body.Concat(hmac.ComputeHash(body)).ToArray());
                }
            }
        }

        /// <summary>Plain text, or null if the value is missing or damaged or the key file is missing.</summary>
        public static string Unprotect(string value)
        {
            if (!IsProtected(value))
            {
                return null;
            }
            try
            {
                var key = ReadKey();
                if (key == null)
                {
                    return null;
                }
                var all = Convert.FromBase64String(value.Substring(Prefix.Length));
                if (all.Length < 16 + 16 + 32)
                {
                    return null;
                }
                var body = all.Take(all.Length - 32).ToArray();
                var mac = all.Skip(all.Length - 32).ToArray();
                using (var hmac = new HMACSHA256(key.Skip(32).ToArray()))
                {
                    if (!FixedTimeEquals(hmac.ComputeHash(body), mac))
                    {
                        return null;
                    }
                }
                using (var aes = Aes.Create())
                {
                    aes.Key = key.Take(32).ToArray();
                    aes.IV = body.Take(16).ToArray();
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var decryptor = aes.CreateDecryptor())
                    {
                        var plain = decryptor.TransformFinalBlock(body, 16, body.Length - 16);
                        return Encoding.UTF8.GetString(plain);
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] ReadKey()
        {
            lock (Sync)
            {
                var path = KeyFilePath;
                if (!File.Exists(path))
                {
                    return null;
                }
                var key = File.ReadAllBytes(path);
                return key.Length == KeyFileLength ? key : null;
            }
        }

        private static byte[] GetOrCreateKey()
        {
            lock (Sync)
            {
                var existing = ReadKey();
                if (existing != null)
                {
                    return existing;
                }
                if (Plugin.IsUninstalling)
                {
                    throw new InvalidOperationException("Gap Finder is being uninstalled.");
                }
                var key = new byte[KeyFileLength];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(key);
                }
                // The data folder is created only here (see Plugin.DataFolder).
                Directory.CreateDirectory(Plugin.Instance.DataFolder);
                File.WriteAllBytes(KeyFilePath, key);
                return key;
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
