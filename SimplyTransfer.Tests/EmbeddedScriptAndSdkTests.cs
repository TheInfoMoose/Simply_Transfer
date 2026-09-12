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

            Assert.False(string.IsNullOrWhiteSpace(setupScript), "setup-prerequisites.ps1 was not found in embedded assembly resources.");
            Assert.Contains("param", setupScript, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("OpenSSH", setupScript, StringComparison.OrdinalIgnoreCase);
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


    }
}
