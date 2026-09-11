using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using SimplyTransfer.Core.Models;
using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class HostReadinessServiceTests
    {
        [Fact]
        public void AuditSourceReadiness_NonExistentKey_ReportsMissingKeyAndCriticalStatus()
        {
            var readinessService = new HostReadinessService();
            string nonExistentKey = Path.Combine(Path.GetTempPath(), $"missing_key_{Guid.NewGuid():N}");

            var report = readinessService.AuditSourceReadiness(nonExistentKey);

            Assert.NotNull(report);
            Assert.False(report.KeyPairExists);
            Assert.Equal(SecurityHealthStatus.Critical, report.AclStatus);
            Assert.Contains("No SSH private key", report.AclDetails);
            Assert.True(report.Recommendations.Count > 0);
        }

        [Fact]
        public void AuditSourceReadiness_ExistingKeyAndPub_CalculatesFingerprint()
        {
            var readinessService = new HostReadinessService();
            string tempDir = Path.Combine(Path.GetTempPath(), $"ssh_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string keyPath = Path.Combine(tempDir, "id_ed25519");
            string pubPath = keyPath + ".pub";

            try
            {
                File.WriteAllText(keyPath, "-----BEGIN OPENSSH PRIVATE KEY-----\ntest\n-----END OPENSSH PRIVATE KEY-----");
                string testPubKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGt7yW6G/kCjK2eQ9cE1/jB7iW4uA0Y1hZlGg6p8K2mN SimplyTransfer-Test";
                File.WriteAllText(pubPath, testPubKey);

                var report = readinessService.AuditSourceReadiness(keyPath);

                Assert.NotNull(report);
                Assert.True(report.KeyPairExists);
                Assert.Equal(testPubKey, report.PublicKeyString);
                Assert.StartsWith("SHA256:", report.KeyFingerprintSha256);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AuditDestinationReadiness_ExecutesWithoutException()
        {
            var readinessService = new HostReadinessService();

            var report = readinessService.AuditDestinationReadiness(Environment.UserName, @"C:\Backups");

            Assert.NotNull(report);
            Assert.False(string.IsNullOrEmpty(report.AdminAuthorizedKeysPath));
            Assert.NotNull(report.Recommendations);
        }

        [Fact]
        public void RepairSourceKeyAcls_ExistingTempFile_AppliesStrictAclsWithoutError()
        {
            var readinessService = new HostReadinessService();
            string tempFile = Path.Combine(Path.GetTempPath(), $"test_key_repair_{Guid.NewGuid():N}");
            File.WriteAllText(tempFile, "fake private key content");

            try
            {
                readinessService.RepairSourceKeyAcls(tempFile);

                var fileInfo = new FileInfo(tempFile);
                var acl = fileInfo.GetAccessControl();
                Assert.NotNull(acl);
                // Inheritance should be protected (disabled)
                Assert.True(acl.AreAccessRulesProtected);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
