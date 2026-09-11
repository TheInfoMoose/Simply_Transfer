using System;
using System.Text;
using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class SecurityServiceTests
    {
        private readonly SecurityService _service = new();

        [Fact]
        public void EncryptAndDecryptString_ReturnsOriginalString()
        {
            string original = "SuperSecretPassphrase123!";
            byte[] encrypted = _service.EncryptString(original);

            Assert.NotNull(encrypted);
            Assert.NotEmpty(encrypted);

            string decrypted = _service.DecryptString(encrypted);
            Assert.Equal(original, decrypted);
        }

        [Fact]
        public void EncryptAndDecryptBytes_ReturnsOriginalBytes()
        {
            byte[] original = Encoding.UTF8.GetBytes("Ed25519_Raw_Private_Key_Bytes_Data");
            byte[] encrypted = _service.EncryptBytes(original);

            Assert.NotNull(encrypted);
            Assert.NotEmpty(encrypted);

            byte[] decrypted = _service.DecryptBytes(encrypted);
            Assert.Equal(original, decrypted);
        }

        [Fact]
        public void EncryptString_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(_service.EncryptString(string.Empty));
            Assert.Empty(_service.EncryptString(null!));
        }

        [Fact]
        public void DecryptString_InvalidData_ReturnsEmptyGracefully()
        {
            byte[] corruptData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            string result = _service.DecryptString(corruptData);
            Assert.Equal(string.Empty, result);
        }
    }
}
