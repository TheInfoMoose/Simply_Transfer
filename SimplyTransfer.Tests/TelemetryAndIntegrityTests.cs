using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SimplyTransfer.Core.Models;
using SimplyTransfer.Core.Services;
using SimplyTransfer.UI.ViewModels;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class TelemetryAndIntegrityTests
    {
        [Fact]
        public void TransferItem_SizeParity_CorrectlyCalculatesMatchStatus()
        {
            var item = new TransferItem
            {
                FileName = "QuickBooksCompany.qbw",
                LocalFilePath = @"C:\QB\QuickBooksCompany.qbw",
                FileSizeBytes = 104857600 // 100 MB
            };

            // Before remote size is known
            Assert.False(item.IsSizeMatch);
            Assert.Equal("Awaiting Parity Check", item.SizeParityText);
            Assert.Equal("Pending", item.FormattedRemoteSize);

            // Matching remote size
            item.RemoteFileSizeBytes = 104857600;
            Assert.True(item.IsSizeMatch);
            Assert.Contains("Parity", item.SizeParityText);
            Assert.Equal(item.FormattedSize, item.FormattedRemoteSize);

            // Mismatched remote size
            item.RemoteFileSizeBytes = 104857500;
            Assert.False(item.IsSizeMatch);
            Assert.Contains("Mismatch", item.SizeParityText);
        }

        [Fact]
        public void TransferTelemetryEventArgs_InitializesWithAccurateMetrics()
        {
            var args = new TransferTelemetryEventArgs
            {
                HandshakeState = ConnectionHandshakeState.StreamingPayload,
                CurrentFile = "InvoiceData.tlg",
                BytesTransferred = 50 * 1024 * 1024,
                TotalBytes = 100 * 1024 * 1024,
                TransferSpeedBps = 25 * 1024 * 1024,
                ItemIndex = 1,
                TotalItems = 2,
                LatencyMs = 15,
                StatusMessage = "Streaming payload at 25 MB/s"
            };

            Assert.Equal(ConnectionHandshakeState.StreamingPayload, args.HandshakeState);
            Assert.Equal(50 * 1024 * 1024, args.BytesTransferred);
            Assert.Equal(100 * 1024 * 1024, args.TotalBytes);
            Assert.Equal(15, args.LatencyMs);
            Assert.Equal("InvoiceData.tlg", args.CurrentFile);
            Assert.Equal(1, args.ItemIndex);
            Assert.Equal(2, args.TotalItems);
        }

        [Fact]
        public void MainViewModel_TechnicianRoleSwitching_UpdatesStateCorrectly()
        {
            var services = new ServiceCollection();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            services.AddSingleton<HostReadinessService>();
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<MainViewModel>();

            // Initial state: Source
            Assert.Equal("Source", vm.ActiveRuntimeRole);
            Assert.False(vm.IsDestinationMode);

            // Switch to Destination
            vm.SwitchRuntimeRole("Destination");
            Assert.Equal("Destination", vm.ActiveRuntimeRole);
            Assert.True(vm.IsDestinationMode);

            // Switch back to Source
            vm.SwitchRuntimeRole("Source");
            Assert.Equal("Source", vm.ActiveRuntimeRole);
            Assert.False(vm.IsDestinationMode);
        }

        [Fact]
        public void MainViewModel_ScriptRunnerConsole_CanClearOutput()
        {
            var services = new ServiceCollection();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            services.AddSingleton<HostReadinessService>();
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<MainViewModel>();

            vm.ScriptOutputLines.Add("Test output line 1");
            vm.ScriptOutputLines.Add("Test output line 2");
            Assert.Equal(2, vm.ScriptOutputLines.Count);

            vm.ClearScriptOutput();
            Assert.Empty(vm.ScriptOutputLines);
            Assert.Equal("Ready", vm.ScriptRunStatus);
        }


    }
}
