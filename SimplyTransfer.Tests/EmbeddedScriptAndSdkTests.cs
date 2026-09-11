using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class EmbeddedScriptAndSdkTests
    {
        [Fact]
        public void EmbeddedScripts_AreCompiledIntoAssemblyResources()
        {
            string? setupScript = HostReadinessService.GetEmbeddedScriptContent("setup-prerequisites.ps1");
            string? destScript = HostReadinessService.GetEmbeddedScriptContent("dest_prerequisites.ps1");
            string? destScriptHyphen = HostReadinessService.GetEmbeddedScriptContent("dest-prerequisites.ps1");

            Assert.False(string.IsNullOrWhiteSpace(setupScript), "setup-prerequisites.ps1 was not found in embedded assembly resources.");
            Assert.Contains("param", setupScript, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("OpenSSH", setupScript, StringComparison.OrdinalIgnoreCase);

            Assert.False(string.IsNullOrWhiteSpace(destScript), "dest_prerequisites.ps1 was not found in embedded assembly resources.");
            Assert.Contains("param", destScript, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("administrators_authorized_keys", destScript, StringComparison.OrdinalIgnoreCase);

            Assert.False(string.IsNullOrWhiteSpace(destScriptHyphen), "dest-prerequisites.ps1 was not found in embedded assembly resources.");
        }

        [Fact]
        public async Task SystemManagementAutomation_InProcessExecution_CapturesStreams()
        {
            var readinessService = new HostReadinessService();
            var outputLines = new List<string>();

            // Execute an in-process PowerShell snippet through the SDK runner
            string testSnippet = @"
Write-Output 'Pipeline item 1'
Write-Information 'Informational message' -InformationAction Continue
Write-Output 'Pipeline item 2'
";

            int exitCode = await readinessService.ExecuteScriptBlockAsync(
                testSnippet,
                arguments: null,
                outputHandler: line => outputLines.Add(line));

            // Should have executed in-process without error
            Assert.True(exitCode == 0, "Output:\n" + string.Join("\n", outputLines));
            Assert.Contains(outputLines, l => l.Contains("Pipeline item 1"));
            Assert.Contains(outputLines, l => l.Contains("Pipeline item 2"));
        }

        [Fact]
        public async Task SystemManagementAutomation_InProcessExecution_WithArguments_BindsParameters()
        {
            var readinessService = new HostReadinessService();
            var outputLines = new List<string>();

            // dest_prerequisites.ps1 with -ClientPublicKey, -SkipFirewall, -SkipServiceStart, -NoPause
            int exitCode = await readinessService.RunScriptAsync(
                "dest_prerequisites.ps1",
                "-ClientPublicKey \"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGt7yW6G/kCjK2eQ9cE1/jB7iW4uA0Y1hZlGg6p8K2mN SimplyTransfer-Test\" -SkipFirewall -SkipServiceStart -NoPause",
                elevate: false,
                outputHandler: line => outputLines.Add(line));

            // Should complete audit without error
            Assert.True(exitCode == 0, "Output:\n" + string.Join("\n", outputLines));
            Assert.True(outputLines.Count > 0, "No output was captured from the in-process PowerShell SDK pipeline.");
        }
    }
}
