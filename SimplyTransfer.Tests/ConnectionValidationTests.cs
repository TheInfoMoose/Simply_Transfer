using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SimplyTransfer.Core.Models;
using SimplyTransfer.Core.Services;
using SimplyTransfer.UI.ViewModels;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class ConnectionValidationTests
    {
        [Fact]
        public void LoggingService_WritesLogFileWithTimestampAndSeverity()
        {
            var logger = new LoggingService();
            string testMessage = $"Test log entry {Guid.NewGuid()}";

            logger.LogInfo(testMessage);
            logger.LogWarn("Warning test entry");
            logger.LogError("Error test entry", new InvalidOperationException("Simulated exception"));

            Assert.True(File.Exists(logger.LogFilePath));

            string content = File.ReadAllText(logger.LogFilePath);
            Assert.Contains(testMessage, content);
            Assert.Contains("[INFO]", content);
            Assert.Contains("[WARN]", content);
            Assert.Contains("[ERROR]", content);
            Assert.Contains("Simulated exception", content);
        }

        [Fact]
        public void TransferItem_RaisesPropertyChangedEvents()
        {
            var item = new TransferItem
            {
                FileName = "test.txt",
                LocalFilePath = @"C:\test.txt",
                FileSizeBytes = 2048
            };

            var changedProperties = new List<string>();
            item.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName != null)
                {
                    changedProperties.Add(e.PropertyName);
                }
            };

            item.Status = TransferStatus.Transferring;
            item.ProgressPercentage = 45.5;
            item.BytesTransferred = 1024;
            item.TransferSpeedBps = 500000;
            item.LocalSha256 = "abc123def456";
            item.RemoteSha256 = "abc123def456";
            item.HashStatus = HashMatchStatus.Verified;
            item.StatusMessage = "Verified";
            item.FileSizeBytes = 4096;

            Assert.Contains(nameof(item.Status), changedProperties);
            Assert.Contains(nameof(item.ProgressPercentage), changedProperties);
            Assert.Contains(nameof(item.BytesTransferred), changedProperties);
            Assert.Contains(nameof(item.TransferSpeedBps), changedProperties);
            Assert.Contains(nameof(item.LocalSha256), changedProperties);
            Assert.Contains(nameof(item.RemoteSha256), changedProperties);
            Assert.Contains(nameof(item.HashStatus), changedProperties);
            Assert.Contains(nameof(item.StatusMessage), changedProperties);
            Assert.Contains(nameof(item.FormattedSize), changedProperties);
        }

        [Fact]
        public void FileBrowser_DummyNode_NeverReturnedInSelectedFiles()
        {
            var vm = new FileBrowserViewModel();
            var parent = new FileNode(@"C:\FakeFolder", "FakeFolder", isDirectory: true);
            parent.AddDummyChild();

            // When unexpanded folder is selected
            parent.IsSelected = true;

            vm.RootNodes.Clear();
            vm.RootNodes.Add(parent);

            var paths = vm.GetSelectedFilePaths();

            // Must NOT contain empty path or "Loading..."
            Assert.DoesNotContain(string.Empty, paths);
            Assert.DoesNotContain("Loading...", paths);
        }

        [Fact]
        public void FileBrowser_UnexpandedFolderWithFiles_GathersFilesSafely()
        {
            // Create a temporary folder with test files
            string tempDir = Path.Combine(Path.GetTempPath(), $"SimplyTransfer_Test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string file1 = Path.Combine(tempDir, "file1.txt");
            string file2 = Path.Combine(tempDir, "file2.txt");
            File.WriteAllText(file1, "Hello");
            File.WriteAllText(file2, "World");

            try
            {
                var vm = new FileBrowserViewModel();
                var folderNode = new FileNode(tempDir, "TestFolder", isDirectory: true);
                folderNode.AddDummyChild(); // simulates unexpanded tree node

                folderNode.IsSelected = true;
                vm.RootNodes.Clear();
                vm.RootNodes.Add(folderNode);

                var paths = vm.GetSelectedFilePaths();

                Assert.Contains(file1, paths);
                Assert.Contains(file2, paths);
                Assert.DoesNotContain(string.Empty, paths);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task ValidateConnection_ProfileValidationFailures()
        {
            var vpnService = new VpnOrchestrationService();
            var hashService = new HashValidationService();
            var secService = new SecurityService();
            var logger = new LoggingService();
            var orchestrator = new SyncOrchestratorService(vpnService, hashService, secService, logger);

            // 1. Missing host
            var profileNoHost = new SyncProfile { Host = "", Port = 22, Username = "user", PrivateKeyPath = "somekey" };
            var res1 = await orchestrator.ValidateConnectionAsync(profileNoHost);
            Assert.False(res1.Success);
            Assert.Contains("Host is required", res1.Message);

            // 2. Invalid port
            var profileBadPort = new SyncProfile { Host = "127.0.0.1", Port = 99999, Username = "user", PrivateKeyPath = "somekey" };
            var res2 = await orchestrator.ValidateConnectionAsync(profileBadPort);
            Assert.False(res2.Success);
            Assert.Contains("Invalid port", res2.Message);

            // 3. Missing username
            var profileNoUser = new SyncProfile { Host = "127.0.0.1", Port = 22, Username = "", PrivateKeyPath = "somekey" };
            var res3 = await orchestrator.ValidateConnectionAsync(profileNoUser);
            Assert.False(res3.Success);
            Assert.Contains("Username is required", res3.Message);

            // 4. Missing / non-existent private key
            var profileBadKey = new SyncProfile { Host = "127.0.0.1", Port = 22, Username = "user", PrivateKeyPath = @"C:\nonexistent_key_file.key" };
            var res4 = await orchestrator.ValidateConnectionAsync(profileBadKey);
            Assert.False(res4.Success);
            Assert.Contains("Private Key file not found", res4.Message);
        }

        [Fact]
        public async Task ValidateConnection_UnreachableHost_ReturnsFailureCleanly()
        {
            var vpnService = new VpnOrchestrationService();
            var hashService = new HashValidationService();
            var secService = new SecurityService();
            var logger = new LoggingService();
            var orchestrator = new SyncOrchestratorService(vpnService, hashService, secService, logger);

            // Temporary dummy key file
            string tempKey = Path.Combine(Path.GetTempPath(), $"dummy_key_{Guid.NewGuid():N}.key");
            File.WriteAllText(tempKey, "dummy key content");

            try
            {
                // Connect to a black-hole / unreachable IP with short timeout
                var profile = new SyncProfile
                {
                    Host = "192.0.2.1", // RFC 5737 TEST-NET-1 (non-routable)
                    Port = 2222,
                    Username = "testuser",
                    PrivateKeyPath = tempKey,
                    PreFlightTimeoutSeconds = 2,
                    UseVpn = false
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var result = await orchestrator.ValidateConnectionAsync(profile, cts.Token);

                Assert.False(result.Success);
                Assert.NotNull(result.Message);
            }
            finally
            {
                try { File.Delete(tempKey); } catch { }
            }
        }

        [Fact]
        public async Task ValidateConnection_PublicKeyFileSelected_ReturnsClearFailure()
        {
            var vpnService = new VpnOrchestrationService();
            var hashService = new HashValidationService();
            var secService = new SecurityService();
            var logger = new LoggingService();
            var orchestrator = new SyncOrchestratorService(vpnService, hashService, secService, logger);

            var profile = new SyncProfile
            {
                Host = "192.0.2.1",
                Port = 22,
                Username = "testuser",
                PrivateKeyPath = @"C:\Keys\sample.pub",
                UseVpn = false
            };

            var result = await orchestrator.ValidateConnectionAsync(profile);
            Assert.False(result.Success);
            Assert.Contains("Public Key", result.Message);
            Assert.Contains("PRIVATE", result.Details);
        }

        [Fact]
        public void SftpTransferService_ResolvePath_ExpandsTildeAndEnvVars()
        {
            string tildePath = @"~/.ssh/id_ed25519";
            string resolvedTilde = SftpTransferService.ResolvePath(tildePath);
            Assert.False(resolvedTilde.StartsWith("~"));
            Assert.Contains(".ssh", resolvedTilde);

            string envPath = @"%USERPROFILE%\.ssh\id_ed25519";
            string resolvedEnv = SftpTransferService.ResolvePath(envPath);
            Assert.False(resolvedEnv.StartsWith("%"));
            Assert.Contains(".ssh", resolvedEnv);
        }
    }
}
