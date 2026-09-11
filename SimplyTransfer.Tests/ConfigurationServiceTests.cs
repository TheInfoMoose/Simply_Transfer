using System;
using System.IO;
using SimplyTransfer.Core.Models;
using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class ConfigurationServiceTests
    {
        private readonly ConfigurationService _service = new();

        [Fact]
        public void ExportAndImportProfile_PreservesProperties()
        {
            var profile = new SyncProfile
            {
                Name = "Export Test Profile",
                Host = "sftp.testserver.org",
                Port = 2222,
                Username = "testadmin",
                PrivateKeyPath = @"C:\keys\id_ed25519",
                KeyType = "Ed25519",
                UseVpn = true,
                VpnType = "WireGuard",
                VpnConnectionName = "wg-backup",
                DestinationDirectory = "/remote/backups/",
                UseVssForLockedFiles = true,
                AutoDetectQuickBooks = true
            };

            string tempFile = Path.Combine(Path.GetTempPath(), $"test_profile_{Guid.NewGuid():N}.json");
            try
            {
                _service.ExportProfile(profile, tempFile, includeEncryptedSecrets: false);
                Assert.True(File.Exists(tempFile));

                var imported = _service.ImportProfile(tempFile);
                Assert.NotNull(imported);
                Assert.Equal(profile.Name, imported.Name);
                Assert.Equal(profile.Host, imported.Host);
                Assert.Equal(profile.Port, imported.Port);
                Assert.Equal(profile.Username, imported.Username);
                Assert.Equal(profile.DestinationDirectory, imported.DestinationDirectory);
                Assert.Equal(profile.UseVpn, imported.UseVpn);
                Assert.Equal(profile.VpnType, imported.VpnType);
                Assert.Equal(profile.VpnConnectionName, imported.VpnConnectionName);
                Assert.Null(imported.EncryptedPrivateKey); // Verify secrets excluded for portability
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}
