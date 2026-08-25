using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SaviSchedular.Services.Security
{
    public static class EncryptionHelper
    {
        // 256-bit Key derived for internal encryption
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("SAVISCHEDULAR_SECURE_AES_KEY2026"); // 32 bytes
        private static readonly byte[] IV = Encoding.UTF8.GetBytes("SAVISCHEDULAR_IV1");                 // 16 bytes

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText) || plainText == "••••••••")
                return plainText;

            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = IV;

                    ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(plainText);
                            }
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch
            {
                return plainText; // Fallback
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText) || cipherText == "••••••••")
                return cipherText;

            try
            {
                byte[] buffer = Convert.FromBase64String(cipherText);
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = IV;

                    ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    using (MemoryStream ms = new MemoryStream(buffer))
                    {
                        using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        {
                            using (StreamReader sr = new StreamReader(cs))
                            {
                                return sr.ReadToEnd();
                            }
                        }
                    }
                }
            }
            catch
            {
                return cipherText; // Plaintext fallback if not encrypted
            }
        }
    }
}
