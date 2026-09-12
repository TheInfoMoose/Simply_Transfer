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
                Shutdown(0);
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
                    
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(1000);
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            _ = vm.ApplyPackagedDestinationSetupAsync();
                        });
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
                var report = readiness.AuditSourceReadiness();
                if (!report.KeyPairExists)
                {
                    readiness.GenerateSourceKeyPairAsync().GetAwaiter().GetResult();
                }
                else
                {
                    readiness.RepairSourceKeyAcls(report.PrivateKeyPath);
                }
                _logger?.LogSuccess("[CLI] Source host bootstrap complete.");
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
                readiness.RepairDestinationAcls();
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
