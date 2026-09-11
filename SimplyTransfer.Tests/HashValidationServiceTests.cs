using System.IO;
using System.Text;
using System.Threading.Tasks;
using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class HashValidationServiceTests
    {
        private readonly HashValidationService _service = new();

        [Fact]
        public async Task ComputeStreamHashAsync_MatchesKnownSha256()
        {
            // Input: "Hello Simply Transfer"
            // SHA-256 ("Hello Simply Transfer") = 47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4
            string text = "Hello Simply Transfer";
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

            string hash = await _service.ComputeStreamHashAsync(ms);

            // Compute expected using .NET directly
            using var sha = System.Security.Cryptography.SHA256.Create();
            string expected = System.Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

            Assert.Equal(expected, hash);
            Assert.Equal(64, hash.Length);
        }

        [Fact]
        public void ValidateHashes_ReturnsTrueForMatchingHashes()
        {
            string hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            string hash2 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

            Assert.True(_service.ValidateHashes(hash1, hash2));
        }

        [Fact]
        public void ValidateHashes_ReturnsFalseForMismatchedHashes()
        {
            string hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            string hash2 = "1111111111111111111111111111111111111111111111111111111111111111";

            Assert.False(_service.ValidateHashes(hash1, hash2));
            Assert.False(_service.ValidateHashes(hash1, string.Empty));
        }

        [Fact]
        public void TryParseSha256_CertUtilOutput_ExtractsHash()
        {
            string certUtilOutput =
                "SHA256 hash of C:\\Backups\\QuickBooks.qbw:\r\n" +
                "47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4\r\n" +
                "CertUtil: -hashfile command completed successfully.";

            string? parsed = HashValidationService.TryParseSha256(certUtilOutput);

            Assert.Equal("47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4", parsed);
        }

        [Fact]
        public void TryParseSha256_PowerShellOutput_ExtractsHash()
        {
            string psOutput = "47807B1EF0306EEEB4F8FB2C42289C0715AA31FF3BE48CBCFD34208A0D4218A4\r\n";

            string? parsed = HashValidationService.TryParseSha256(psOutput);

            Assert.Equal("47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4", parsed);
        }

        [Fact]
        public void TryParseSha256_Sha256SumOutput_ExtractsHash()
        {
            string sha256sumOutput = "47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4  /var/backups/data.db\n";

            string? parsed = HashValidationService.TryParseSha256(sha256sumOutput);

            Assert.Equal("47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4", parsed);
        }

        [Fact]
        public void TryParseSha256_ShasumOutput_ExtractsHash()
        {
            string shasumOutput = "47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4  QuickBooks.qbw\n";

            string? parsed = HashValidationService.TryParseSha256(shasumOutput);

            Assert.Equal("47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4", parsed);
        }

        [Fact]
        public void TryParseSha256_OpenSslOutput_ExtractsHash()
        {
            string openSslOutput = "SHA256(/backups/file.tar)= 47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4\n";

            string? parsed = HashValidationService.TryParseSha256(openSslOutput);

            Assert.Equal("47807b1ef0306eeeb4f8fb2c42289c0715aa31ff3be48cbcfd34208a0d4218a4", parsed);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("'sha256sum' is not recognized as an internal or external command")]
        [InlineData("File not found")]
        [InlineData("Error 0x80070002")]
        public void TryParseSha256_InvalidOrErrorOutput_ReturnsNull(string? invalidOutput)
        {
            string? parsed = HashValidationService.TryParseSha256(invalidOutput);
            Assert.Null(parsed);
        }
    }
}
