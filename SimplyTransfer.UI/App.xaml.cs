using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SimplyTransfer.Core.Services;
using SimplyTransfer.UI.ViewModels;

namespace SimplyTransfer.UI
{
    /// <summary>
    /// Interaction logic and dependency injection composition root for Simply Transfer.
    /// </summary>
    public partial class App : Application
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        private LoggingService? _logger;

        /// <summary>
        /// Gets the application-wide dependency injection service provider.
        /// </summary>
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        /// <summary>
        /// Configures service dependencies, registers unhandled exception handlers, and initializes the main application window on startup.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Startup event arguments.</param>
        private void OnStartup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length > 0)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            // Set up global unhandled exception monitoring before initializing services
            _logger = new LoggingService();
            _logger.LogInfo("Simply Transfer application starting up...");

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var services = new ServiceCollection();
            
            // Core Services
            services.AddSingleton(_logger);
            services.AddSingleton<ConfigurationService>();
            services.AddSingleton<SecurityService>();
            services.AddSingleton<VpnOrchestrationService>();
            services.AddTransient<VssSnapshotService>();
            services.AddTransient<HashValidationService>();
            services.AddSingleton<SyncOrchestratorService>();
            services.AddSingleton<HostReadinessService>();
            
            // ViewModels
            services.AddTransient<MainViewModel>();
            
            // Windows
            services.AddTransient<MainWindow>();

            ServiceProvider = services.BuildServiceProvider();

            // Handle CLI flags for automated headless setup or health auditing
            if (e.Args.Length > 0 && ProcessCommandLineArguments(e.Args))
            {
                Environment.Exit(Environment.ExitCode);
                return;
            }

            _logger.LogInfo("Service container built successfully. Presenting MainWindow.");
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

            string localProfilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profile.json");
            bool isPackagedDestination = System.IO.File.Exists(localProfilePath);
            var vm = mainWindow.DataContext as MainViewModel;

            if (isPackagedDestination)
            {
                var configService = ServiceProvider.GetRequiredService<ConfigurationService>();
                var imported = configService.ImportProfile(localProfilePath);

                if (imported != null)
                {
                    var profiles = configService.LoadProfiles();
                    if (!System.Linq.Enumerable.Any(profiles, p => p.Id == imported.Id))
                    {
                        profiles.Add(imported);
                        configService.SaveProfiles(profiles);
                    }
                    if (vm != null) 
                    {
                        vm.Profiles = new System.Collections.ObjectModel.ObservableCollection<SimplyTransfer.Core.Models.SyncProfile>(profiles);
                        vm.SelectedProfile = System.Linq.Enumerable.FirstOrDefault(vm.Profiles, p => p.Id == imported.Id) ?? imported;
                    }
                }

                if (vm != null)
                {
                    vm.SwitchRuntimeRole("Destination");
                    
                    Application.Current.Dispatcher.InvokeAsync(async () => 
                    {
                        try
                        {
                            await Task.Delay(1000);
                            await vm.ApplyPackagedDestinationSetupAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError("Packaged destination setup failed", ex);
                        }
                    });
                }
            }
            else if (e.Args.Length > 0)
            {
                int idx = Array.FindIndex(e.Args, a => a.Equals("--mode", StringComparison.OrdinalIgnoreCase) || a.Equals("-Mode", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx < e.Args.Length - 1 && e.Args[idx + 1].Equals("destination", StringComparison.OrdinalIgnoreCase))
                {
                    if (vm != null) vm.SwitchRuntimeRole("Destination");
                }
            }

            mainWindow.Show();
        }

        private bool ProcessCommandLineArguments(string[] args)
        {
            var readiness = ServiceProvider.GetRequiredService<HostReadinessService>();

            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase) || a.Equals("-?", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Simply Transfer - Secure SFTP Backup & Synchronization Utility");
                Console.WriteLine("Usage: SimplyTransfer.UI.exe [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  --mode <Source|Destination>      Launch GUI in Source or Destination mode");
                Console.WriteLine("  --health-check                   Audit and print host readiness report (headless)");
                Console.WriteLine("  --setup-source                   Bootstrap source client SSH key infrastructure (headless)");
                Console.WriteLine("  --repair-acls                    Repair strict NTFS permissions on private/authorized keys (headless)");
                Console.WriteLine("  --run-embedded-script <name>     Execute embedded PowerShell setup script in-process");
                Console.WriteLine("  --generate-manifest <dir> <mode> Generate pre-transfer manifest JSON (mode: Hash or SizeAndTimestamp)");
                Console.WriteLine("  --help, -h, -?                   Show this help message");
                return true;
            }

            if (args.Any(a => a.Equals("--health-check", StringComparison.OrdinalIgnoreCase) || a.Equals("-HealthCheck", StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogInfo("[CLI] Running host readiness audit...");
                var srcReport = readiness.AuditSourceReadiness();
                var dstReport = readiness.AuditDestinationReadiness();
                Console.WriteLine("=== SIMPLY TRANSFER HOST AUDIT ===");
                Console.WriteLine($"Source Key Pair: {(srcReport.KeyPairExists ? "Found (" + srcReport.PrivateKeyPath + ")" : "Missing")}");
                Console.WriteLine($"Source Key ACL: {srcReport.AclStatus} ({srcReport.AclDetails})");
                Console.WriteLine($"Destination sshd: {dstReport.SshdStatus}");
                Console.WriteLine($"Destination Admin Keys: {(dstReport.AdminAuthorizedKeysExists ? "Configured" : "Missing")} ({dstReport.AdminKeysAclStatus})");
                return true;
            }

            if (args.Any(a => a.Equals("--setup-source", StringComparison.OrdinalIgnoreCase) || a.Equals("-SetupSource", StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogInfo("[CLI] Executing source setup bootstrap...");
                var setupWindow = new SetupProgressWindow();
                
                Action<string> logAction = line => 
                {
                    Console.WriteLine(line);
                    setupWindow.AppendLog(line);
                };

                Task.Run(async () =>
                {
                    try
                    {
                        await readiness.InstallOpenSshCapabilitiesAsync(logAction);
                        await readiness.ConfigureSshServicesAsync(logAction);
                        await readiness.ConfigureFirewallPort22Async(logAction);
                        
                        var report = readiness.AuditSourceReadiness();
                        if (!report.KeyPairExists)
                        {
                            await readiness.GenerateSourceKeyPairAsync();
                        }
                        else
                        {
                            readiness.RepairSourceKeyAcls(report.PrivateKeyPath);
                        }
                        
                        _logger?.LogSuccess("[CLI] Source host bootstrap complete.");
                        setupWindow.Complete();
                        await Task.Delay(2000);
                    }
                    catch (Exception ex)
                    {
                        logAction($"ERROR: {ex.Message}");
                        Application.Current.Dispatcher.Invoke(() => setupWindow.ActivityProgressBar.IsIndeterminate = false);
                        await Task.Delay(5000);
                    }
                    finally
                    {
                        Application.Current.Dispatcher.Invoke(() => setupWindow.Close());
                    }
                });

                setupWindow.ShowDialog();
                return true;
            }

            if (args.Any(a => a.Equals("--setup-destination", StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogInfo("[CLI] Executing destination setup bootstrap...");
                
                string targetUser = Environment.UserName;
                string targetDir = @"C:\Backups";
                string pubKeyPath = string.Empty;
                
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].Equals("--user", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        targetUser = args[i + 1];
                    else if (args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        targetDir = args[i + 1];
                    else if (args[i].Equals("--pubkey", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        pubKeyPath = args[i + 1];
                }

                Action<string> logAction = line => Console.WriteLine(line);
                readiness.InstallOpenSshCapabilitiesAsync(logAction).GetAwaiter().GetResult();
                readiness.ConfigureSshServicesAsync(logAction).GetAwaiter().GetResult();
                readiness.ConfigureFirewallPort22Async(logAction).GetAwaiter().GetResult();

                if (!string.IsNullOrEmpty(pubKeyPath) && File.Exists(pubKeyPath))
                {
                    readiness.ProvisionDestinationUserAsync(targetUser, targetDir, pubKeyPath, logAction).GetAwaiter().GetResult();
                }
                else
                {
                    _logger?.LogWarn("[CLI] Missing or invalid public key path. Skipping user provisioning.");
                }

                _logger?.LogSuccess("[CLI] Destination host bootstrap complete.");
                return true;
            }

            if (args.Any(a => a.Equals("--reset-keys", StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogInfo("[CLI] Executing native key reset...");
                Action<string> logAction = line => Console.WriteLine(line);
                readiness.RemoveSecurityKeys(logAction);
                _logger?.LogSuccess("[CLI] Native key reset complete.");
                return true;
            }

            if (args.Any(a => a.Equals("--repair-acls", StringComparison.OrdinalIgnoreCase) || a.Equals("-RepairAcls", StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogInfo("[CLI] Repairing endpoint key ACLs and ownership...");
                var srcReport = readiness.AuditSourceReadiness();
                if (srcReport.KeyPairExists)
                {
                    readiness.RepairSourceKeyAcls(srcReport.PrivateKeyPath);
                }
                readiness.RepairDestinationAclsAsync().GetAwaiter().GetResult();
                _logger?.LogSuccess("[CLI] Key ACLs and ownership repaired.");
                return true;
            }

            if (args.Any(a => a.Equals("--run-embedded-script", StringComparison.OrdinalIgnoreCase)))
            {
                int idx = Array.FindIndex(args, a => a.Equals("--run-embedded-script", StringComparison.OrdinalIgnoreCase));
                string scriptName = idx < args.Length - 1 ? args[idx + 1] : "setup-prerequisites.ps1";
                string remainingArgs = string.Join(" ", args.Skip(idx + 2));
                _logger?.LogInfo($"[CLI] Running embedded script '{scriptName}' with args: {remainingArgs}");

                int exitCode = readiness.RunScriptInProcessAsync(
                    scriptName, 
                    remainingArgs, 
                    line => Console.WriteLine(line)).GetAwaiter().GetResult();

                Environment.ExitCode = exitCode;
                return true;
            }

            if (args.Any(a => a.Equals("--generate-manifest", StringComparison.OrdinalIgnoreCase)))
            {
                int idx = Array.FindIndex(args, a => a.Equals("--generate-manifest", StringComparison.OrdinalIgnoreCase));
                if (idx < args.Length - 2)
                {
                    string dir = args[idx + 1];
                    string mode = args[idx + 2];
                    if (System.IO.Directory.Exists(dir))
                    {
                        var files = System.IO.Directory.GetFiles(dir, "*.*", System.IO.SearchOption.AllDirectories);
                        var manifest = new System.Collections.Generic.List<SimplyTransfer.Core.Models.ManifestItem>();
                        var hashSvc = new SimplyTransfer.Core.Services.HashValidationService();
                        foreach (var file in files)
                        {
                            var fi = new System.IO.FileInfo(file);
                            var item = new SimplyTransfer.Core.Models.ManifestItem
                            {
                                FileName = fi.FullName.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/'),
                                Size = fi.Length,
                                LastWriteTimeUtc = fi.LastWriteTimeUtc
                            };
                            if (mode.Equals("Hash", StringComparison.OrdinalIgnoreCase))
                            {
                                item.Sha256 = hashSvc.ComputeLocalHashAsync(fi.FullName).GetAwaiter().GetResult();
                            }
                            manifest.Add(item);
                        }
                        string json = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        Console.WriteLine(json);
                    }
                    else
                    {
                        Console.WriteLine("[]"); // Return empty array if dir doesn't exist
                    }
                }
                return true;
            }

            return false;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger?.LogError("Unhandled UI Dispatcher Exception", e.Exception);

            MessageBox.Show(
                $"An unexpected application error occurred:\n\n{e.Exception.Message}\n\nDiagnostic log: {_logger?.LogFilePath}",
                "Simply Transfer - Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Mark as handled to prevent immediate process termination
            e.Handled = true;
        }

        private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            _logger?.LogError($"AppDomain Unhandled Exception (IsTerminating: {e.IsTerminating})", ex);

            if (ex != null)
            {
                MessageBox.Show(
                    $"A fatal application domain exception occurred:\n\n{ex.Message}\n\nDiagnostic log: {_logger?.LogFilePath}",
                    "Simply Transfer - Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            _logger?.LogError("Unobserved Task Exception", e.Exception);
            e.SetObserved();
        }
    }
}
