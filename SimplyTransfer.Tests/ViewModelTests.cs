using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SimplyTransfer.Core.Services;
using SimplyTransfer.UI.ViewModels;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class ViewModelTests
    {
        [Fact]
        public void DependencyInjection_ResolvesAllServicesAndViewModels()
        {
            var services = new ServiceCollection();
            
            // Core Services
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            
            // ViewModels
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();

            var vm = provider.GetRequiredService<MainViewModel>();
            Assert.NotNull(vm);
            Assert.NotNull(vm.FileBrowser);
            Assert.NotNull(vm.Profiles);
            Assert.NotEmpty(vm.Profiles);
            Assert.NotNull(vm.SelectedProfile);
            Assert.Equal("Ready", vm.StatusMessage);
            Assert.True(vm.CanStartSync);
        }

        [Fact]
        public void MainViewModel_NewProfile_AddsProfileSuccessfully()
        {
            var services = new ServiceCollection();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<MainViewModel>();

            int initialCount = vm.Profiles.Count;
            vm.NewProfile();

            Assert.Equal(initialCount + 1, vm.Profiles.Count);
            Assert.NotNull(vm.SelectedProfile);
            Assert.Contains("New Profile", vm.SelectedProfile.Name);
            Assert.NotNull(vm.ValidateConnectionCommand);
            Assert.NotNull(vm.OpenLogFileCommand);
            Assert.NotNull(vm.StartSyncCommand);
            Assert.NotNull(vm.CopyPublicKeyCommand);
            Assert.NotNull(vm.ShowKeyInstructionsCommand);
        }

        [Fact]
        public void MainViewModel_AssociatedPublicKey_DiscoversMatchingPubFile()
        {
            var services = new ServiceCollection();
            services.AddSingleton<LoggingService>();
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            services.AddTransient<MainViewModel>();

            var provider = services.BuildServiceProvider();
            var vm = provider.GetRequiredService<MainViewModel>();

            string tempKey = Path.Combine(Path.GetTempPath(), $"test_key_{System.Guid.NewGuid():N}");
            string tempPub = tempKey + ".pub";
            string expectedPubKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITestPublicKey SimplyTransfer-Test";

            File.WriteAllText(tempKey, "private key content");
            File.WriteAllText(tempPub, expectedPubKey);

            try
            {
                vm.SelectedProfile!.PrivateKeyPath = tempKey;
                vm.RefreshAssociatedPublicKey();

                Assert.Equal(expectedPubKey, vm.AssociatedPublicKey);
            }
            finally
            {
                try { File.Delete(tempKey); } catch { }
                try { File.Delete(tempPub); } catch { }
            }
        }
    }
}
