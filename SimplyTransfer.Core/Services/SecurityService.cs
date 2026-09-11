using System;
using System.Security.Cryptography;
using System.Text;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Provides cryptographic data protection services using the Windows Data Protection API (DPAPI).
    /// Protects sensitive keys and passphrases at rest scoped to the current Windows user.
    /// </summary>
    public class SecurityService
    {
        // DPAPI Entropy for additional security layer
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SimplyTransfer_DPAPI_Entropy_v2");

        /// <summary>
        /// Encrypts a plain-text string using Windows DPAPI under the current user's security context.
        /// </summary>
        /// <param name="plainText">The plain text string to encrypt.</param>
        /// <returns>The encrypted byte array, or an empty byte array if the input is null/empty.</returns>
        public byte[] EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return Array.Empty<byte>();

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            return EncryptBytes(plainBytes);
        }

        /// <summary>
        /// Decrypts a DPAPI-encrypted byte array back into a plain-text string.
        /// </summary>
        /// <param name="encryptedData">The encrypted byte array.</param>
        /// <returns>The decrypted UTF-8 string, or an empty string if decryption fails or data is empty.</returns>
        public string DecryptString(byte[]? encryptedData)
        {
            if (encryptedData == null || encryptedData.Length == 0)
                return string.Empty;

            byte[] decryptedBytes = DecryptBytes(encryptedData);
            if (decryptedBytes.Length == 0)
                return string.Empty;

            return Encoding.UTF8.GetString(decryptedBytes);
        }

        /// <summary>
        /// Encrypts an array of bytes using Windows DPAPI (DataProtectionScope.CurrentUser).
        /// </summary>
        /// <param name="plainBytes">The raw bytes to encrypt.</param>
        /// <returns>The protected encrypted byte array.</returns>
        public byte[] EncryptBytes(byte[] plainBytes)
        {
            if (plainBytes == null || plainBytes.Length == 0)
                return Array.Empty<byte>();

            try
            {
                return ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception)
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Decrypts a byte array previously protected with Windows DPAPI under CurrentUser scope.
        /// </summary>
        /// <param name="encryptedBytes">The protected byte array to decrypt.</param>
        /// <returns>The unprotected original byte array, or an empty array if decryption fails.</returns>
        public byte[] DecryptBytes(byte[]? encryptedBytes)
        {
            if (encryptedBytes == null || encryptedBytes.Length == 0)
                return Array.Empty<byte>();

            try
            {
                return ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Happens if decrypted under a different user or machine context
                return Array.Empty<byte>();
            }
            catch (Exception)
            {
                return Array.Empty<byte>();
            }
        }
    }
}
