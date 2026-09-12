using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SimplyTransfer.Core.Models;
using SimplyTransfer.Core.Services;

namespace SimplyTransfer.UI.ViewModels
{
    /// <summary>
    /// Primary ViewModel driving the Simply Transfer presentation layer.
    /// Manages profiles, VPN connectivity state, file selection, transfer queue, and operation audit logging.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly ConfigurationService _configService;
        private readonly VpnOrchestrationService _vpnService;
        private readonly SyncOrchestratorService _orchestratorService;
        private readonly SecurityService _securityService;
        private readonly HostReadinessService _readinessService;
        private readonly LoggingService _logger;
        private CancellationTokenSource? _syncCts;
        private CancellationTokenSource? _scriptCts;

        [ObservableProperty]
        private ObservableCollection<SyncProfile> _profiles = new();

        [ObservableProperty]
        private SyncProfile? _selectedProfile;

        [ObservableProperty]
        private FileBrowserViewModel _fileBrowser = new();

        [ObservableProperty]
        private ObservableCollection<TransferItem> _transferQueue = new();

        [ObservableProperty]
        private ObservableCollection<SyncLogEventArgs> _activityLogs = new();

        #region Dual-Runtime & Technician Telemetry Properties

        [ObservableProperty]
        private string _activeRuntimeRole = "Source"; // "Source" or "Destination"

        [ObservableProperty]
        private bool _isDestinationMode;

        // Key Health & Security Audit
        [ObservableProperty]
        private SourceReadinessReport? _sourceHealthReport;

        [ObservableProperty]
        private DestinationReadinessReport? _destinationHealthReport;

        [ObservableProperty]
        private SecurityHealthStatus _overallSecurityHealth = SecurityHealthStatus.Unknown;

        [ObservableProperty]
        private string _keyHealthSummary = "Auditing endpoint security...";

        [ObservableProperty]
        private string _keyAclBadgeText = "Auditing...";

        private bool _hasAutoRunPrerequisites = false;

        [ObservableProperty]
        private string _keyOwnerText = "Unknown";

        [ObservableProperty]
        private string _keyFingerprint = string.Empty;

        [ObservableProperty]
        private bool _isAclRepairNeeded;

        [ObservableProperty]
        private bool _isAuditingHealth;

        // Connection Handshake & Telemetry
        [ObservableProperty]
        private ConnectionHandshakeState _currentHandshakeState = ConnectionHandshakeState.Idle;

        [ObservableProperty]
        private string _handshakeStateDisplay = "Idle";

        [ObservableProperty]
        private string _telemetryThroughputText = "0.00 MB/s";

        [ObservableProperty]
        private string _telemetryBytesTransferredText = "0 B / 0 B";

        [ObservableProperty]
        private string _telemetryActiveFile = "Idle";

        [ObservableProperty]
        private string _telemetryStatusText = "Ready for transfer";

        [ObservableProperty]
        private long _telemetryLatencyMs;

        // GUI Script Runner
        [ObservableProperty]
        private bool _isScriptRunning;

        [ObservableProperty]
        private ObservableCollection<string> _scriptOutputLines = new();

        [ObservableProperty]
        private string _scriptRunStatus = "Ready";

        [ObservableProperty]
        private bool _elevateScriptExecution = true;

        [ObservableProperty]
        private string _destinationScriptUser = Environment.UserName;

        [ObservableProperty]
        private string _destinationScriptDirectory = @"C:\Backups";

        #endregion

        [ObservableProperty]
        private bool _isVpnConnected;

        [ObservableProperty]
        private bool _isVpnBusy;

        [ObservableProperty]
        private string _vpnStatusText = "Disconnected";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartSync))]
        private bool _isSyncing;

        /// <summary>Gets whether a sync operation can be initiated.</summary>
        public bool CanStartSync => !IsSyncing;

        [ObservableProperty]
        private double _overallProgress;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        // Unsaved changes or edit state for private key / passphrase
        [ObservableProperty]
        private string _plainPassphrase = string.Empty;

        /// <summary>
        /// Gets or sets the OpenSSH public key matching the current profile's private key.
        /// </summary>
        [ObservableProperty]
        private string _associatedPublicKey = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainViewModel"/> class.
        /// Injects required core services and hooks into sync orchestrator event streams.
        /// </summary>
        public MainViewModel(
            ConfigurationService configService, 
            VpnOrchestrationService vpnService,
            SyncOrchestratorService orchestratorService,
            SecurityService securityService,
            HostReadinessService? readinessService = null,
            LoggingService? logger = null)
        {
            _configService = configService;
            _vpnService = vpnService;
            _orchestratorService = orchestratorService;
            _securityService = securityService;
            _readinessService = readinessService ?? new HostReadinessService(logger);
            _logger = logger ?? new LoggingService();

            _orchestratorService.LogMessage += OnLogMessageReceived;
            _orchestratorService.ItemProgressUpdated += OnItemProgressUpdated;
            _orchestratorService.TelemetryUpdated += OnTelemetryUpdated;

            LoadProfiles();
            _ = CheckVpnStatusAsync();
            _ = RefreshHealthAuditAsync();
        }

        private void LoadProfiles()
        {
            var loaded = _configService.LoadProfiles();
            Profiles = new ObservableCollection<SyncProfile>(loaded);
            
            if (Profiles.Count > 0)
            {
                SelectedProfile = Profiles[0];
            }
        }

        partial void OnSelectedProfileChanged(SyncProfile? value)
        {
            if (value != null)
            {
                _ = CheckVpnStatusAsync();
                
                // If passphrase is encrypted, we don't display plain text unless decrypted
                if (value.EncryptedPassphrase != null && value.EncryptedPassphrase.Length > 0)
                {
                    PlainPassphrase = _securityService.DecryptString(value.EncryptedPassphrase);
                }
                else
                {
                    PlainPassphrase = string.Empty;
                }

                RefreshAssociatedPublicKey();
            }
            else
            {
                AssociatedPublicKey = string.Empty;
            }
        }

        /// <summary>
        /// Inspects the configured private key path and loads the matching public key (.pub).
        /// </summary>
        public void RefreshAssociatedPublicKey()
        {
            if (SelectedProfile == null || string.IsNullOrWhiteSpace(SelectedProfile.PrivateKeyPath))
            {
                AssociatedPublicKey = string.Empty;
                return;
            }

            string resolvedKeyPath = SftpTransferService.ResolvePath(SelectedProfile.PrivateKeyPath);

            // Check if .pub exists alongside the private key
            string pubPath = resolvedKeyPath + ".pub";
            if (File.Exists(pubPath))
            {
                try
                {
                    AssociatedPublicKey = File.ReadAllText(pubPath).Trim();
                    return;
                }
                catch { }
            }

            // Also check standard ~/.ssh location if file name matches
            string userSshPub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", Path.GetFileName(resolvedKeyPath) + ".pub");
            if (File.Exists(userSshPub))
            {
                try
                {
                    AssociatedPublicKey = File.ReadAllText(userSshPub).Trim();
                    return;
                }
                catch { }
            }

            // Check default id_ed25519.pub in ~/.ssh
            string defaultEd25519Pub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519.pub");
            if (File.Exists(defaultEd25519Pub))
            {
                try
                {
                    AssociatedPublicKey = File.ReadAllText(defaultEd25519Pub).Trim();
                    return;
                }
                catch { }
            }

            AssociatedPublicKey = string.Empty;
        }

        private void OnLogMessageReceived(object? sender, SyncLogEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ActivityLogs.Insert(0, e);
                StatusMessage = e.Message;
            });
        }

        private void OnItemProgressUpdated(object? sender, TransferItem item)
        {
            // TransferItem implements INotifyPropertyChanged. Individual property bindings
            // update in the ListView automatically without mutating the collection.
        }

        private void OnTelemetryUpdated(object? sender, TransferTelemetryEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CurrentHandshakeState = e.HandshakeState;
                HandshakeStateDisplay = e.HandshakeState.ToString();
                TelemetryThroughputText = $"{e.TransferSpeedBps / (1024.0 * 1024.0):F2} MB/s";
                TelemetryBytesTransferredText = e.TotalBytes > 0
                    ? $"{TransferItem.FormatSize(e.BytesTransferred)} / {TransferItem.FormatSize(e.TotalBytes)}"
                    : TransferItem.FormatSize(e.BytesTransferred);
                TelemetryActiveFile = string.IsNullOrEmpty(e.CurrentFile) ? "Idle" : e.CurrentFile;
                TelemetryStatusText = e.StatusMessage;
                TelemetryLatencyMs = e.LatencyMs;
            });
        }

        #region VPN Commands

        /// <summary>
        /// Probes and refreshes the current VPN connection status for the selected profile.
        /// </summary>
        [RelayCommand]
        public async Task CheckVpnStatusAsync()
        {
            if (SelectedProfile == null || !SelectedProfile.UseVpn)
            {
                IsVpnConnected = false;
                VpnStatusText = "VPN Disabled";
                return;
            }

            try
            {
                IsVpnConnected = await _vpnService.IsVpnConnectedAsync(SelectedProfile.VpnType, SelectedProfile.VpnConnectionName);
                VpnStatusText = IsVpnConnected ? "Connected" : "Disconnected";
            }
            catch
            {
                IsVpnConnected = false;
                VpnStatusText = "Unknown";
            }
        }

        /// <summary>
        /// Connects or disconnects the configured VPN tunnel based on current connection state.
        /// </summary>
        [RelayCommand]
        public async Task ToggleVpnAsync()
        {
            if (SelectedProfile == null) return;
            if (IsVpnConnected)
            {
                await DisconnectVpnAsync();
            }
            else
            {
                await ConnectVpnAsync();
            }
        }

        [RelayCommand]
        private async Task ConnectVpnAsync()
        {
            if (SelectedProfile == null) return;

            try
            {
                IsVpnBusy = true;
                VpnStatusText = "Connecting...";
                StatusMessage = $"Connecting to {SelectedProfile.VpnType} ({SelectedProfile.VpnConnectionName})...";

                bool success = await _vpnService.ConnectVpnAsync(
                    SelectedProfile.VpnType, 
                    SelectedProfile.VpnConnectionName, 
                    SelectedProfile.VpnConfigPath);

                IsVpnConnected = success;
                VpnStatusText = success ? "Connected" : "Connection Failed";
                StatusMessage = success ? "VPN Tunnel Established" : "VPN Tunnel Failed";
            }
            catch (Exception ex)
            {
                IsVpnConnected = false;
                VpnStatusText = "Error";
                StatusMessage = $"VPN Error: {ex.Message}";
            }
            finally
            {
                IsVpnBusy = false;
            }
        }

        [RelayCommand]
        private async Task DisconnectVpnAsync()
        {
            if (SelectedProfile == null) return;

            try
            {
                IsVpnBusy = true;
                VpnStatusText = "Disconnecting...";
                StatusMessage = $"Disconnecting {SelectedProfile.VpnConnectionName}...";

                await _vpnService.DisconnectVpnAsync(SelectedProfile.VpnType, SelectedProfile.VpnConnectionName);
                IsVpnConnected = false;
                VpnStatusText = "Disconnected";
                StatusMessage = "VPN Disconnected";
            }
            catch (Exception ex)
            {
                StatusMessage = $"VPN Disconnect Error: {ex.Message}";
            }
            finally
            {
                IsVpnBusy = false;
            }
        }

        #endregion

        #region Sync Commands

        /// <summary>
        /// Probes and validates SFTP server connectivity, SSH credentials, and remote destination directory on demand.
        /// </summary>
        [RelayCommand]
        public async Task ValidateConnectionAsync()
        {
            if (SelectedProfile == null)
            {
                MessageBox.Show("Please select or create a sync profile first.", "Simply Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                StatusMessage = $"Validating connection to {SelectedProfile.Host}:{SelectedProfile.Port}...";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, SelectedProfile.PreFlightTimeoutSeconds + 5)));

                var result = await _orchestratorService.ValidateConnectionAsync(SelectedProfile, cts.Token);

                if (result.Success)
                {
                    StatusMessage = $"Connection verified ({result.LatencyMs}ms).";
                    MessageBox.Show(
                        $"Connection Validation Succeeded!\n\nTarget: {SelectedProfile.Host}:{SelectedProfile.Port}\nLatency: {result.LatencyMs} ms\nDetails: {result.Details}",
                        "Connection Verified",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = "Connection validation failed.";

                    if (result.Message.Contains("publickey", StringComparison.OrdinalIgnoreCase) ||
                        result.Details.Contains("publickey", StringComparison.OrdinalIgnoreCase))
                    {
                        var answer = MessageBox.Show(
                            $"Connection Validation Failed:\n\n{result.Message}\n\n{result.Details}\n\nWould you like to open the SSH Key Assignment Guide now to view ready-to-use setup commands for this destination?",
                            "SSH Authentication Failed (publickey)",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (answer == MessageBoxResult.Yes)
                        {
                            ShowKeyInstructions();
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Connection Validation Failed:\n\n{result.Message}\n\n{result.Details}\n\nPlease check network reachability, firewall rules, port configuration, and SSH credentials.",
                            "Connection Validation Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connection probe error: {ex.Message}";
                _logger.LogError("Error during on-demand connection validation", ex);
                MessageBox.Show(
                    $"An error occurred while probing the connection:\n\n{ex.Message}",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Validates pre-conditions, performs pre-flight connection validation, gathers selected files,
        /// and initiates the synchronization pipeline with comprehensive error handling and popups.
        /// </summary>
        [RelayCommand]
        public async Task StartSyncAsync()
        {
            if (SelectedProfile == null)
            {
                MessageBox.Show("Please select or create a sync profile first.", "Simply Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                IsSyncing = true;
                _syncCts = new CancellationTokenSource();

                // 1. Connection Validation prior to running the transfer
                StatusMessage = "Validating target connection...";
                var validationResult = await _orchestratorService.ValidateConnectionAsync(SelectedProfile, _syncCts.Token);
                if (!validationResult.Success)
                {
                    StatusMessage = "Connection validation failed.";
                    _logger.LogError($"Pre-transfer connection validation failed for profile '{SelectedProfile.Name}': {validationResult.Message}");
                    
                    if (validationResult.Message.Contains("publickey", StringComparison.OrdinalIgnoreCase) ||
                        validationResult.Details.Contains("publickey", StringComparison.OrdinalIgnoreCase))
                    {
                        var answer = MessageBox.Show(
                            $"Connection validation failed prior to transfer:\n\n{validationResult.Message}\n\n{validationResult.Details}\n\nWould you like to open the SSH Key Assignment Guide now to view ready-to-use setup commands?",
                            "SSH Authentication Failed (publickey)",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (answer == MessageBoxResult.Yes)
                        {
                            ShowKeyInstructions();
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Connection validation failed prior to transfer:\n\n{validationResult.Message}\n\n{validationResult.Details}\n\nPlease verify host address, port, SSH credentials, and network/VPN settings before initiating transfer.",
                            "Connection Validation Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    return;
                }

                StatusMessage = "Gathering selected files...";

                // 2. Gather selected files from file tree
                var selectedFiles = FileBrowser.GetSelectedFilePaths();

                // Also check if profile has configured source paths
                if (selectedFiles.Count == 0 && SelectedProfile.SourcePaths.Count > 0)
                {
                    foreach (var path in SelectedProfile.SourcePaths)
                    {
                        if (File.Exists(path))
                        {
                            selectedFiles.Add(path);
                        }
                        else if (Directory.Exists(path))
                        {
                            try
                            {
                                selectedFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories));
                            }
                            catch { }
                        }
                    }
                }

                // Filter to only non-empty, existing files
                selectedFiles = selectedFiles.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("Please select one or more files or folders in the file browser to transfer.", "Simply Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusMessage = "Ready";
                    return;
                }

                // 3. Build transfer items
                TransferQueue.Clear();
                foreach (var filePath in selectedFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        string ext = fileInfo.Extension;
                        bool isQb = string.Equals(ext, ".qbw", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".tlg", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".qbb", StringComparison.OrdinalIgnoreCase);

                        var item = new TransferItem
                        {
                            FileName = fileInfo.Name,
                            LocalFilePath = fileInfo.FullName,
                            RemoteFilePath = fileInfo.Name, // Default to file name in dest dir
                            FileSizeBytes = fileInfo.Length,
                            IsQuickBooksFile = isQb,
                            IsVssRequired = isQb || (SelectedProfile.UseVssForLockedFiles && IsFileExclusivelyLocked(filePath)),
                            Status = TransferStatus.Pending,
                            HashStatus = HashMatchStatus.Pending,
                            StatusMessage = "Queued"
                        };
                        TransferQueue.Add(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarn($"Skipping inaccessible file '{filePath}': {ex.Message}");
                    }
                }

                if (TransferQueue.Count == 0)
                {
                    MessageBox.Show("None of the selected files could be accessed or queued for transfer.", "Simply Transfer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = "Ready";
                    return;
                }

                OverallProgress = 0;
                StatusMessage = "Starting sync operation...";

                var progress = new Progress<double>(p => OverallProgress = p);

                bool success = await _orchestratorService.ExecuteSyncAsync(
                    SelectedProfile,
                    TransferQueue.ToList(),
                    progress,
                    _syncCts.Token);

                if (success)
                {
                    StatusMessage = "Sync completed successfully with verified hashes.";
                    _logger.LogSuccess($"Sync completed successfully for profile '{SelectedProfile.Name}' with verified hashes.");
                    MessageBox.Show(
                        "All files have been transferred and cryptographically verified (SHA-256) successfully.",
                        "Transfer Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = "Sync completed with one or more errors.";
                    _logger.LogWarn($"Sync completed with errors for profile '{SelectedProfile.Name}'.");
                    MessageBox.Show(
                        "One or more files failed to transfer or failed cryptographic SHA-256 hash validation.\n\nPlease check the Transfer Queue and Activity Log for details.",
                        "Transfer Failed / Verification Mismatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Sync canceled by user.";
                OnLogMessageReceived(this, new SyncLogEventArgs("Sync canceled by user.", "WARN"));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Sync failed: {ex.Message}";
                _logger.LogError($"Unexpected exception during sync: {ex.Message}", ex);
                OnLogMessageReceived(this, new SyncLogEventArgs($"Sync failure: {ex.Message}", "ERROR"));

                MessageBox.Show(
                    $"An error occurred while starting or executing the transfer:\n\n{ex.Message}\n\nA diagnostic trace has been recorded in the application log.",
                    "Transfer Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsSyncing = false;
                _syncCts?.Dispose();
                _syncCts = null;
            }
        }

        /// <summary>
        /// Requests cancellation of the currently executing sync operation.
        /// </summary>
        [RelayCommand]
        public void CancelSync()
        {
            if (_syncCts != null && !_syncCts.IsCancellationRequested)
            {
                StatusMessage = "Canceling sync...";
                _syncCts.Cancel();
            }
        }

        /// <summary>
        /// Refreshes the local file browser tree to reflect newly added or deleted files.
        /// </summary>
        [RelayCommand]
        public void RefreshFileBrowser()
        {
            try
            {
                StatusMessage = "Refreshing local file browser...";
                FileBrowser.LoadDrives();
                StatusMessage = "File browser refreshed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to refresh file browser: {ex.Message}";
                _logger.LogError("Error refreshing file browser", ex);
            }
        }

        #endregion

        #region Profile Management

        /// <summary>
        /// Creates a new unconfigured synchronization profile template and selects it.
        /// </summary>
        [RelayCommand]
        public void NewProfile()
        {
            var profile = new SyncProfile
            {
                Name = $"New Profile {Profiles.Count + 1}",
                Host = string.Empty,
                Port = 22,
                Username = string.Empty,
                DestinationDirectory = "/backups/"
            };
            Profiles.Add(profile);
            SelectedProfile = profile;
        }

        /// <summary>
        /// Saves changes to the selected profile into persistent AppData JSON storage.
        /// </summary>
        [RelayCommand]
        public void SaveProfile()
        {
            if (SelectedProfile == null) return;

            // Save encrypted passphrase if entered
            if (!string.IsNullOrEmpty(PlainPassphrase))
            {
                SelectedProfile.EncryptedPassphrase = _securityService.EncryptString(PlainPassphrase);
            }

            _configService.SaveProfiles(Profiles.ToList());
            StatusMessage = $"Profile '{SelectedProfile.Name}' saved successfully.";
            OnLogMessageReceived(this, new SyncLogEventArgs($"Profile '{SelectedProfile.Name}' saved to persistent JSON store.", "INFO"));
        }

        /// <summary>
        /// Prompts confirmation and deletes the currently selected synchronization profile.
        /// </summary>
        [RelayCommand]
        public void DeleteProfile()
        {
            if (SelectedProfile == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete profile '{SelectedProfile.Name}'?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Profiles.Remove(SelectedProfile);
                SelectedProfile = Profiles.FirstOrDefault();
                _configService.SaveProfiles(Profiles.ToList());
                StatusMessage = "Profile deleted.";
            }
        }

        /// <summary>
        /// Opens a file dialog allowing the user to select an SSH private key file.
        /// Protects against accidentally selecting public keys (.pub).
        /// </summary>
        [RelayCommand]
        public void BrowsePrivateKey()
        {
            if (SelectedProfile == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "Select Ed25519 or RSA Private Key",
                Filter = "Private Key Files (*; id_*; *.pem; *.key)|*;id_*;*.pem;*.key;*.ppk|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                if (dialog.FileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    string possiblePrivateKey = dialog.FileName.Substring(0, dialog.FileName.Length - 4);
                    if (File.Exists(possiblePrivateKey))
                    {
                        var answer = MessageBox.Show(
                            "You selected a Public Key file (.pub).\n\n" +
                            "Simply Transfer requires the matching PRIVATE key to authenticate (e.g., id_ed25519 without .pub).\n\n" +
                            $"Would you like to use '{Path.GetFileName(possiblePrivateKey)}' as the private key instead?",
                            "Public Key Selected",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (answer == MessageBoxResult.Yes)
                        {
                            SelectedProfile.PrivateKeyPath = possiblePrivateKey;
                            OnPropertyChanged(nameof(SelectedProfile));
                            RefreshAssociatedPublicKey();
                            return;
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            "You selected a Public Key file (.pub).\n\n" +
                            "Simply Transfer requires the PRIVATE key file on this client machine. The public key is the one you place on the remote destination server in ~/.ssh/authorized_keys.",
                            "Invalid Key Selection",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }

                SelectedProfile.PrivateKeyPath = dialog.FileName;
                OnPropertyChanged(nameof(SelectedProfile));
                RefreshAssociatedPublicKey();
            }
        }

        /// <summary>
        /// Copies the active public key to the Windows clipboard for convenient assignment on the remote server.
        /// </summary>
        [RelayCommand]
        public void CopyPublicKey()
        {
            if (!string.IsNullOrWhiteSpace(AssociatedPublicKey))
            {
                Clipboard.SetText(AssociatedPublicKey);
                StatusMessage = "Public key copied to clipboard.";
                MessageBox.Show(
                    "Public key copied to clipboard!\n\nPaste this entry into ~/.ssh/authorized_keys (or C:\\ProgramData\\ssh\\administrators_authorized_keys for Windows Admin accounts) on your remote server.",
                    "Public Key Copied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "No matching public key (.pub) was found on disk for the selected private key.",
                    "Public Key Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Displays clear, copy-paste instructions on how to authorize the public key on Linux, macOS, or Windows servers.
        /// </summary>
        [RelayCommand]
        public void ShowKeyInstructions()
        {
            string pubKey = string.IsNullOrWhiteSpace(AssociatedPublicKey)
                ? "<YOUR_PUBLIC_KEY>"
                : AssociatedPublicKey;
            string user = string.IsNullOrWhiteSpace(SelectedProfile?.Username)
                ? "username"
                : SelectedProfile.Username;
            string host = string.IsNullOrWhiteSpace(SelectedProfile?.Host)
                ? "hostname"
                : SelectedProfile.Host;

            string instructions =
                "=================================================================\n" +
                "       SSH KEY PAIR ASSIGNMENT & CONFIGURATION INSTRUCTIONS      \n" +
                "=================================================================\n\n" +
                "1. KEY ROLES EXPLAINED:\n" +
                "   • PRIVATE KEY (e.g. id_ed25519):\n" +
                "     Stays on THIS machine inside Simply Transfer. NEVER copy it to the remote server.\n\n" +
                "   • PUBLIC KEY (e.g. id_ed25519.pub):\n" +
                "     Must be registered on the DESTINATION SERVER in the authorized keys file.\n" +
                "     Do NOT select the .pub file inside Simply Transfer!\n\n" +
                "-----------------------------------------------------------------\n" +
                "2. YOUR PUBLIC KEY TO AUTHORIZE ON DESTINATION SERVER:\n" +
                $"{pubKey}\n" +
                "-----------------------------------------------------------------\n\n" +
                "3. DESTINATION SERVER SETUP BY OPERATING SYSTEM:\n\n" +
                "--- A. Windows Destination Server (OpenSSH Server) ---\n" +
                "Important Note on Usernames:\n" +
                "• For local accounts or Microsoft Accounts (e.g. user@outlook.com), use the local\n" +
                "  Windows user folder name (e.g., 'BackupUser' or 'Administrator'), not the email address.\n\n" +
                "Option 1: If the destination user is an ADMINISTRATOR (Standard Windows OpenSSH default):\n" +
                "Windows OpenSSH IGNORES C:\\Users\\<user>\\.ssh\\authorized_keys for Admin accounts!\n" +
                "Run this in an ELEVATED Administrator PowerShell prompt on the destination server:\n\n" +
                $"   $key = \"{pubKey}\"\n" +
                "   $target = \"$env:ProgramData\\ssh\\administrators_authorized_keys\"\n" +
                "   New-Item -ItemType Directory -Force -Path \"$env:ProgramData\\ssh\" | Out-Null\n" +
                "   Add-Content -Path $target -Value $key -Force\n" +
                "   icacls.exe $target /inheritance:r /grant \"Administrators:F\" /grant \"SYSTEM:F\"\n" +
                "   Restart-Service sshd\n\n" +
                "Option 2: If the destination user is a STANDARD (non-admin) user:\n" +
                "Run this in PowerShell on the destination server:\n\n" +
                $"   $key = \"{pubKey}\"\n" +
                $"   $target = \"$env:USERPROFILE\\.ssh\\authorized_keys\"\n" +
                "   New-Item -ItemType Directory -Force -Path \"$env:USERPROFILE\\.ssh\" | Out-Null\n" +
                "   Add-Content -Path $target -Value $key -Force\n" +
                "   icacls.exe $target /inheritance:r /grant \"$($env:USERNAME):F\" /grant \"SYSTEM:F\"\n\n" +
                "--- B. Linux / Unix / macOS Destination Server ---\n" +
                $"Log into {host} as user '{user}', and execute:\n\n" +
                "   mkdir -p ~/.ssh\n" +
                "   chmod 700 ~/.ssh\n" +
                $"   echo \"{pubKey}\" >> ~/.ssh/authorized_keys\n" +
                "   chmod 600 ~/.ssh/authorized_keys\n\n" +
                "=================================================================";

            Clipboard.SetText(instructions);
            StatusMessage = "Key assignment instructions copied to clipboard.";

            MessageBox.Show(
                instructions,
                "Key Pair Assignment Instructions (Copied to Clipboard)",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Opens a file dialog allowing the user to select a WireGuard configuration file (.conf).
        /// </summary>
        [RelayCommand]
        public void BrowseWireGuardConfig()
        {
            if (SelectedProfile == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "Select WireGuard Configuration File",
                Filter = "WireGuard Config (*.conf)|*.conf|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedProfile.VpnConfigPath = dialog.FileName;
                if (string.IsNullOrEmpty(SelectedProfile.VpnConnectionName))
                {
                    SelectedProfile.VpnConnectionName = Path.GetFileNameWithoutExtension(dialog.FileName);
                }
                OnPropertyChanged(nameof(SelectedProfile));
            }
        }

        /// <summary>
        /// Prompts for export location and exports the current profile to a portable JSON file.
        /// </summary>
        [RelayCommand]
        public void ExportProfile()
        {
            if (SelectedProfile == null) return;

            var dialog = new SaveFileDialog
            {
                Title = "Export Portable Profile JSON",
                FileName = $"{SelectedProfile.Name.Replace(' ', '_')}_profile.json",
                Filter = "JSON Files (*.json)|*.json"
            };

            if (dialog.ShowDialog() == true)
            {
                _configService.ExportProfile(SelectedProfile, dialog.FileName, includeEncryptedSecrets: false);
                MessageBox.Show("Profile exported successfully as portable JSON payload.", "Export Profile", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Prompts for a profile JSON file and imports it into the application profile catalog.
        /// </summary>
        [RelayCommand]
        public void ImportProfile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Portable Profile JSON",
                Filter = "JSON Files (*.json)|*.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var imported = _configService.ImportProfile(dialog.FileName);
                    if (imported != null)
                    {
                        Profiles.Add(imported);
                        SelectedProfile = imported;
                        _configService.SaveProfiles(Profiles.ToList());
                        MessageBox.Show($"Profile '{imported.Name}' imported successfully.", "Import Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import profile: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Clears all entries from the activity log.
        /// </summary>
        [RelayCommand]
        public void ClearLogs()
        {
            ActivityLogs.Clear();
        }

        /// <summary>
        /// Formats and copies all activity log entries to the Windows clipboard.
        /// </summary>
        [RelayCommand]
        public void CopyLogs()
        {
            var text = string.Join(Environment.NewLine, ActivityLogs.Select(l => $"[{l.Timestamp:yyyy-MM-dd HH:mm:ss}] [{l.Level}] {l.Message}"));
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                StatusMessage = "Logs copied to clipboard.";
            }
        }

        /// <summary>
        /// Opens the persistent application log file or log directory in the default system viewer.
        /// </summary>
        [RelayCommand]
        public void OpenLogFile()
        {
            try
            {
                string logFile = _logger.LogFilePath;
                if (File.Exists(logFile))
                {
                    Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true });
                }
                else if (Directory.Exists(_logger.LogDirectory))
                {
                    Process.Start(new ProcessStartInfo(_logger.LogDirectory) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show($"Log directory does not exist yet: {_logger.LogDirectory}", "Log File", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open log file: {ex.Message}", "Log File", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsFileExclusivelyLocked(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Technician Dashboard & Health Commands

        /// <summary>
        /// Switches active runtime role between Source Client and Destination Server.
        /// </summary>
        [RelayCommand]
        public void SwitchRuntimeRole(string role)
        {
            ActiveRuntimeRole = role;
            IsDestinationMode = string.Equals(role, "Destination", StringComparison.OrdinalIgnoreCase);
            StatusMessage = $"Runtime role set to {ActiveRuntimeRole}.";
            _ = RefreshHealthAuditAsync();
        }

        /// <summary>
        /// Audits security health, key permissions, and service readiness for current role.
        /// </summary>
        [RelayCommand]
        public async Task RefreshHealthAuditAsync()
        {
            try
            {
                IsAuditingHealth = true;
                KeyHealthSummary = "Auditing endpoint security...";

                await Task.Run(() =>
                {
                    SourceHealthReport = _readinessService.AuditSourceReadiness(SelectedProfile?.PrivateKeyPath);
                    DestinationHealthReport = _readinessService.AuditDestinationReadiness(SelectedProfile?.Username, SelectedProfile?.DestinationDirectory);
                });

                if (IsDestinationMode && DestinationHealthReport != null)
                {
                    OverallSecurityHealth = DestinationHealthReport.AdminKeysAclStatus;
                    KeyAclBadgeText = DestinationHealthReport.AdminKeysAclStatus == SecurityHealthStatus.Healthy
                        ? "Secure (Administrators:F)"
                        : DestinationHealthReport.AdminKeysAclStatus == SecurityHealthStatus.Critical
                            ? "Insecure / Missing"
                            : "Warning";
                    KeyOwnerText = string.IsNullOrEmpty(DestinationHealthReport.AdminKeysOwner) ? "None" : DestinationHealthReport.AdminKeysOwner;
                    KeyFingerprint = DestinationHealthReport.AdminAuthorizedKeysCount > 0
                        ? $"{DestinationHealthReport.AdminAuthorizedKeysCount} Keys Authorized"
                        : "No Keys Authorized";
                    KeyHealthSummary = DestinationHealthReport.AdminKeysAclDetails;
                    IsAclRepairNeeded = DestinationHealthReport.AdminKeysAclStatus != SecurityHealthStatus.Healthy;
                }
                else if (SourceHealthReport != null)
                {
                    OverallSecurityHealth = SourceHealthReport.AclStatus;
                    KeyAclBadgeText = SourceHealthReport.AclStatus == SecurityHealthStatus.Healthy
                        ? "Secure (Inheritance Disabled)"
                        : SourceHealthReport.AclStatus == SecurityHealthStatus.Critical
                            ? "Insecure / Missing"
                            : "Warning";
                    KeyOwnerText = string.IsNullOrEmpty(SourceHealthReport.FileOwner) ? "None" : SourceHealthReport.FileOwner;
                    KeyFingerprint = string.IsNullOrEmpty(SourceHealthReport.KeyFingerprintSha256) ? "N/A" : SourceHealthReport.KeyFingerprintSha256;
                    KeyHealthSummary = SourceHealthReport.AclDetails;
                    IsAclRepairNeeded = SourceHealthReport.AclStatus != SecurityHealthStatus.Healthy && SourceHealthReport.KeyPairExists;

                    if (!string.IsNullOrEmpty(SourceHealthReport.PublicKeyString) && string.IsNullOrEmpty(AssociatedPublicKey))
                    {
                        AssociatedPublicKey = SourceHealthReport.PublicKeyString;
                    }
                }
            }
            catch (Exception ex)
            {
                KeyHealthSummary = $"Audit error: {ex.Message}";
                _logger.LogError("Health audit failed", ex);
            }
            finally
            {
                IsAuditingHealth = false;
            }

            if (!IsDestinationMode && SourceHealthReport != null && !_hasAutoRunPrerequisites)
            {
                if (!SourceHealthReport.OpenSshClientInstalled || !SourceHealthReport.KeyPairExists)
                {
                    _hasAutoRunPrerequisites = true;
                    Application.Current?.Dispatcher.InvokeAsync(async () =>
                    {
                        await RunSourceSetupScriptAsync();
                    });
                }
            }

            if (IsDestinationMode)
            {
                StartDestinationTracking();
            }
            else
            {
                StopDestinationTracking();
            }
        }

        private CancellationTokenSource? _destinationTrackingCts;

        private void StartDestinationTracking()
        {
            StopDestinationTracking();
            _destinationTrackingCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!_destinationTrackingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (SelectedProfile != null && !string.IsNullOrEmpty(SelectedProfile.DestinationDirectory))
                        {
                            string statusFile = Path.Combine(SelectedProfile.DestinationDirectory, ".sync-status.json");
                            if (File.Exists(statusFile))
                            {
                                string json = await File.ReadAllTextAsync(statusFile);
                                // Very simple manual parsing for robustness (or use JsonSerializer if available)
                                var currentFileMatch = System.Text.RegularExpressions.Regex.Match(json, "\"CurrentFile\":\\s*\"(.*?)\"");
                                var progressMatch = System.Text.RegularExpressions.Regex.Match(json, "\"Progress\":\\s*(\\d+)");
                                var transferredMatch = System.Text.RegularExpressions.Regex.Match(json, "\"TransferredBytes\":\\s*(\\d+)");
                                var totalMatch = System.Text.RegularExpressions.Regex.Match(json, "\"TotalBytes\":\\s*(\\d+)");

                                Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    if (currentFileMatch.Success) TelemetryActiveFile = currentFileMatch.Groups[1].Value;
                                    if (transferredMatch.Success && totalMatch.Success)
                                    {
                                        long trans = long.Parse(transferredMatch.Groups[1].Value);
                                        long total = long.Parse(totalMatch.Groups[1].Value);
                                        TelemetryBytesTransferredText = $"{TransferItem.FormatSize(trans)} / {TransferItem.FormatSize(total)}";
                                        
                                        if (total > 0)
                                        {
                                            OverallProgress = (double)trans / total * 100.0;
                                        }
                                    }
                                    
                                    if (progressMatch.Success)
                                    {
                                        int prog = int.Parse(progressMatch.Groups[1].Value);
                                        if (prog == 100)
                                        {
                                            TelemetryStatusText = "Transfer Complete";
                                            HandshakeStateDisplay = "PayloadVerified";
                                        }
                                        else
                                        {
                                            TelemetryStatusText = "Receiving Transfer...";
                                            HandshakeStateDisplay = "StreamingPayload";
                                        }
                                    }
                                });
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(2000, _destinationTrackingCts.Token);
                }
            }, _destinationTrackingCts.Token);
        }

        private void StopDestinationTracking()
        {
            if (_destinationTrackingCts != null)
            {
                _destinationTrackingCts.Cancel();
                _destinationTrackingCts.Dispose();
                _destinationTrackingCts = null;
            }
        }

        /// <summary>
        /// Automatically hardens NTFS ACLs and ownership on keys according to strict OpenSSH security policy.
        /// </summary>
        [RelayCommand]
        public async Task AutoRepairAclsAsync()
        {
            try
            {
                StatusMessage = "Repairing key permissions & ownership...";
                await Task.Run(() =>
                {
                    if (IsDestinationMode)
                    {
                        _readinessService.RepairDestinationAcls();
                    }
                    else if (SelectedProfile != null && !string.IsNullOrEmpty(SelectedProfile.PrivateKeyPath))
                    {
                        _readinessService.RepairSourceKeyAcls(SelectedProfile.PrivateKeyPath);
                    }
                    else if (SourceHealthReport?.KeyPairExists == true)
                    {
                        _readinessService.RepairSourceKeyAcls(SourceHealthReport.PrivateKeyPath);
                    }
                });

                await RefreshHealthAuditAsync();
                StatusMessage = "Key permissions and ownership successfully repaired.";
                MessageBox.Show(
                    "Security ACLs and file ownership have been hardened successfully according to OpenSSH strict mode policy.",
                    "Permissions Hardened",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Repair failed: {ex.Message}";
                MessageBox.Show($"Failed to repair ACLs: {ex.Message}", "Repair Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Automatically generates a modern Ed25519 key pair with strict ACLs and binds to active profile.
        /// </summary>
        [RelayCommand]
        public async Task AutoGenerateKeyAsync()
        {
            try
            {
                StatusMessage = "Generating modern Ed25519 SSH key pair...";
                string generatedKey = await _readinessService.GenerateSourceKeyPairAsync($"SimplyTransfer-{Environment.MachineName}");

                if (SelectedProfile != null)
                {
                    SelectedProfile.PrivateKeyPath = generatedKey;
                    OnPropertyChanged(nameof(SelectedProfile));
                    RefreshAssociatedPublicKey();
                    SaveProfile();
                }

                await RefreshHealthAuditAsync();
                StatusMessage = $"Generated Ed25519 key pair: {generatedKey}";
                MessageBox.Show(
                    $"New Ed25519 SSH Key Pair generated successfully with strict NTFS ACLs:\n\n{generatedKey}\n\nThe matching public key (.pub) is ready for deployment to your destination server.",
                    "Key Pair Generated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Key generation error: {ex.Message}";
                MessageBox.Show($"Failed to generate key pair: {ex.Message}", "Key Generation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Executes the Source Setup script directly from the GUI with optional Administrator elevation.
        /// Fulfills requirement: scripts for source and destination runnable from GUI.
        /// </summary>
        [RelayCommand]
        public async Task RunSourceSetupScriptAsync()
        {
            string args = ElevateScriptExecution ? "-Elevate -NonInteractive -GenerateDestConfig" : "-NonInteractive -GenerateDestConfig";
            await ExecuteScriptInternalAsync("setup-prerequisites.ps1", args);
        }

        /// <summary>
        /// Executes the Destination Deployment script directly from the GUI.
        /// Deploys the Simply Transfer application to the destination host.
        /// </summary>
        [RelayCommand]
        public async Task RunDestinationSetupScriptAsync()
        {
            string hostArg = !string.IsNullOrWhiteSpace(SelectedProfile?.Host) 
                ? $"-DestinationHost \"{SelectedProfile.Host}\"" 
                : string.Empty;
            string userArg = !string.IsNullOrWhiteSpace(SelectedProfile?.Username) 
                ? $"-DestinationUser \"{SelectedProfile.Username}\"" 
                : string.Empty;
            string dirArg = !string.IsNullOrWhiteSpace(SelectedProfile?.DestinationDirectory) 
                ? $"-DestinationDirectory \"{SelectedProfile.DestinationDirectory}\"" 
                : string.Empty;

            string fullArgs = $"{hostArg} {userArg} {dirArg} -NonInteractive".Trim();
            await ExecuteScriptInternalAsync("deploy-app-to-dest.ps1", fullArgs);
        }

        private async Task ExecuteScriptInternalAsync(string scriptName, string args)
        {
            if (IsScriptRunning) return;

            try
            {
                IsScriptRunning = true;
                ScriptRunStatus = $"Executing {scriptName}...";
                ScriptOutputLines.Clear();
                _scriptCts = new CancellationTokenSource();

                ScriptOutputLines.Add($"[{DateTime.Now:HH:mm:ss}] >>> Starting execution of {scriptName} <<<");
                ScriptOutputLines.Add($"[{DateTime.Now:HH:mm:ss}] Elevation: {(ElevateScriptExecution ? "Administrator (RunAs)" : "Standard In-Process")}");

                int exitCode = await _readinessService.RunScriptAsync(
                    scriptName,
                    args,
                    ElevateScriptExecution,
                    line =>
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            ScriptOutputLines.Add(line);
                        });
                    },
                    _scriptCts.Token);

                ScriptRunStatus = exitCode == 0 ? "Execution Completed" : $"Exited with Code {exitCode}";
                ScriptOutputLines.Add($"[{DateTime.Now:HH:mm:ss}] >>> {scriptName} execution finished (Exit Code: {exitCode}) <<<");

                await RefreshHealthAuditAsync();
            }
            catch (Exception ex)
            {
                ScriptRunStatus = "Execution Error";
                ScriptOutputLines.Add($"[ERROR] {ex.Message}");
                _logger.LogError($"Error executing script {scriptName}", ex);
            }
            finally
            {
                IsScriptRunning = false;
                _scriptCts?.Dispose();
                _scriptCts = null;
            }
        }

        /// <summary>
        /// Clears terminal output lines for the GUI script runner.
        /// </summary>
        [RelayCommand]
        public void ClearScriptOutput()
        {
            ScriptOutputLines.Clear();
            ScriptRunStatus = "Ready";
        }

        /// <summary>
        /// Generates and exports a cryptographic payload verification audit report.
        /// </summary>
        [RelayCommand]
        public void ExportIntegrityReport()
        {
            if (TransferQueue.Count == 0)
            {
                MessageBox.Show("No transferred files in queue to export integrity report for.", "Integrity Report", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "Export Cryptographic Payload Verification Certificate",
                FileName = $"Transfer_Integrity_Audit_{DateTime.Now:yyyyMMdd_HHmmss}.md",
                Filter = "Markdown Report (*.md)|*.md|Text Report (*.txt)|*.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine("# SIMPLY TRANSFER - CRYPTOGRAPHIC PAYLOAD VERIFICATION AUDIT");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Profile:   {SelectedProfile?.Name} ({SelectedProfile?.Host}:{SelectedProfile?.Port})");
                sb.AppendLine($"Endpoint:  {ActiveRuntimeRole} Runtime");
                sb.AppendLine();
                sb.AppendLine("| File Name | Size (Local) | Size (Remote) | Parity | Local SHA-256 | Remote SHA-256 | Verification Status |");
                sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

                foreach (var item in TransferQueue)
                {
                    string parity = item.IsSizeMatch ? "✓ 100% Match" : "✗ Mismatch";
                    sb.AppendLine($"| {item.FileName} | {item.FormattedSize} | {item.FormattedRemoteSize} | {parity} | `{item.LocalSha256}` | `{item.RemoteSha256}` | **{item.HashStatus}** |");
                }

                sb.AppendLine();
                sb.AppendLine("## Verification Verdict");
                bool allVerified = TransferQueue.All(i => i.HashStatus == HashMatchStatus.Verified && i.IsSizeMatch);
                sb.AppendLine(allVerified 
                    ? "✓ **ALL TRANSFERS VALIDATED: BIT-PERFECT CRYPTOGRAPHIC MATCH CONFIRMED**" 
                    : "✗ **WARNING: ONE OR MORE FILES FAILED INTEGRITY OR BYTE PARITY VERIFICATION**");

                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show("Cryptographic integrity audit certificate exported successfully.", "Export Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion
    }
}
