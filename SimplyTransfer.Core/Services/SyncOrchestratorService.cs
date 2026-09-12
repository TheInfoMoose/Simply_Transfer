using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimplyTransfer.Core.Models;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Event arguments containing log details, severity level, and timestamp.
    /// </summary>
    public class SyncLogEventArgs : EventArgs
    {
        /// <summary>Gets the log message text.</summary>
        public string Message { get; }

        /// <summary>Gets the log severity level ("INFO", "WARN", "ERROR", "SUCCESS").</summary>
        public string Level { get; }

        /// <summary>Gets the timestamp when the log entry was generated.</summary>
        public DateTime Timestamp { get; } = DateTime.Now;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncLogEventArgs"/> class.
        /// </summary>
        /// <param name="message">The log message string.</param>
        /// <param name="level">The severity level (default: "INFO").</param>
        public SyncLogEventArgs(string message, string level = "INFO")
        {
            Message = message;
            Level = level;
        }
    }

    /// <summary>
    /// Central orchestrator executing the 5-phase synchronization pipeline:
    /// 1. Pre-flight VPN connection and TCP reachability verification.
    /// 2. Windows Volume Shadow Copy (VSS) atomic snapshot creation for locked files.
    /// 3. Secure key-authenticated SFTP connection establishment.
    /// 4. Parallel-ready streaming file transfer with two-step client/server SHA-256 validation.
    /// 5. Deterministic resource cleanup releasing VSS snapshots and VPN tunnels.
    /// </summary>
    /// <summary>
    /// Represents granular connection handshake and lifecycle states during synchronization.
    /// </summary>
    public enum ConnectionHandshakeState
    {
        /// <summary>No active transfer or handshake.</summary>
        Idle,
        /// <summary>Testing pre-flight socket reachability and VPN tunnel.</summary>
        PreFlightProbe,
        /// <summary>Creating point-in-time VSS snapshots for locked files.</summary>
        VssSnapshotting,
        /// <summary>Establishing TCP connection and negotiating SSH handshake.</summary>
        SshHandshake,
        /// <summary>Authenticating public key against destination authorized keys.</summary>
        KeyAuthentication,
        /// <summary>Opening and initializing SFTP subsystem channel.</summary>
        SftpSessionEstablished,
        /// <summary>Streaming file byte payload across SFTP pipeline.</summary>
        StreamingPayload,
        /// <summary>Executing remote cryptographic SHA-256 hashing command.</summary>
        RemoteIntegrityHashing,
        /// <summary>Confirming payload hash match and byte-level size parity.</summary>
        PayloadVerified,
        /// <summary>All queue items successfully transferred and verified.</summary>
        Completed,
        /// <summary>Handshake, transfer, or integrity verification failed.</summary>
        Failed
    }

    /// <summary>
    /// Event arguments containing live technician transfer telemetry and handshake metrics.
    /// </summary>
    public class TransferTelemetryEventArgs : EventArgs
    {
        /// <summary>Gets the current connection handshake lifecycle state.</summary>
        public ConnectionHandshakeState HandshakeState { get; set; } = ConnectionHandshakeState.Idle;

        /// <summary>Gets the name of the file actively transferring or verifying.</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>Gets the total bytes transferred so far across current item/session.</summary>
        public long BytesTransferred { get; set; }

        /// <summary>Gets the total bytes in queue.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Gets the instantaneous transfer speed in bytes per second.</summary>
        public double TransferSpeedBps { get; set; }

        /// <summary>Gets round-trip latency in milliseconds.</summary>
        public long LatencyMs { get; set; }

        /// <summary>Gets the index of the currently processing item (1-based).</summary>
        public int ItemIndex { get; set; }

        /// <summary>Gets the total count of items in the queue.</summary>
        public int TotalItems { get; set; }

        /// <summary>Gets human-readable telemetry status text.</summary>
        public string StatusMessage { get; set; } = string.Empty;

        /// <summary>Gets the timestamp of this telemetry update.</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Represents the outcome of an automated or manual connection validation probe.
    /// </summary>
    public class ConnectionValidationResult
    {
        /// <summary>Gets or sets whether the connection probe succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Gets or sets the user-facing status message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets detailed diagnostic or exception information.</summary>
        public string Details { get; set; } = string.Empty;

        /// <summary>Gets or sets round-trip latency in milliseconds.</summary>
        public long LatencyMs { get; set; }
    }

    /// <summary>
    /// Central orchestrator executing the 5-phase synchronization pipeline:
    /// 1. Pre-flight VPN connection and TCP reachability verification.
    /// 2. Windows Volume Shadow Copy (VSS) atomic snapshot creation for locked files.
    /// 3. Secure key-authenticated SFTP connection establishment.
    /// 4. Parallel-ready streaming file transfer with two-step client/server SHA-256 validation.
    /// 5. Deterministic resource cleanup releasing VSS snapshots and VPN tunnels.
    /// </summary>
    public class SyncOrchestratorService
    {
        private readonly VpnOrchestrationService _vpnService;
        private readonly HashValidationService _hashService;
        private readonly SecurityService _securityService;
        private readonly LoggingService _logger;

        /// <summary>
        /// Occurs when an operational log message is generated.
        /// </summary>
        public event EventHandler<SyncLogEventArgs>? LogMessage;

        /// <summary>
        /// Occurs when an individual transfer item's status, progress, or verification state updates.
        /// </summary>
        public event EventHandler<TransferItem>? ItemProgressUpdated;

        /// <summary>
        /// Occurs when real-time transfer telemetry or connection handshake state changes.
        /// </summary>
        public event EventHandler<TransferTelemetryEventArgs>? TelemetryUpdated;

        /// <summary>
        /// Gets the current connection handshake lifecycle state.
        /// </summary>
        public ConnectionHandshakeState CurrentHandshakeState { get; private set; } = ConnectionHandshakeState.Idle;

        /// <summary>
        /// Emits a live telemetry update event across subscribers.
        /// </summary>
        public void UpdateTelemetry(
            ConnectionHandshakeState state, 
            string message, 
            string currentFile = "", 
            long bytesTransferred = 0, 
            long totalBytes = 0, 
            double speedBps = 0, 
            long latencyMs = 0, 
            int itemIndex = 0, 
            int totalItems = 0)
        {
            CurrentHandshakeState = state;
            TelemetryUpdated?.Invoke(this, new TransferTelemetryEventArgs
            {
                HandshakeState = state,
                CurrentFile = currentFile,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                TransferSpeedBps = speedBps,
                LatencyMs = latencyMs,
                ItemIndex = itemIndex,
                TotalItems = totalItems,
                StatusMessage = message,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncOrchestratorService"/> class.
        /// </summary>
        /// <param name="vpnService">The VPN orchestration service.</param>
        /// <param name="hashService">The cryptographic hash validation service.</param>
        /// <param name="securityService">The DPAPI security service.</param>
        /// <param name="logger">Optional persistent file logging service.</param>
        public SyncOrchestratorService(
            VpnOrchestrationService vpnService,
            HashValidationService hashService,
            SecurityService securityService,
            LoggingService? logger = null)
        {
            _vpnService = vpnService;
            _hashService = hashService;
            _securityService = securityService;
            _logger = logger ?? new LoggingService();
        }

        private void Log(string message, string level = "INFO", Exception? ex = null)
        {
            LogMessage?.Invoke(this, new SyncLogEventArgs(message, level));
            _logger.Log(message, level, ex);
        }

        /// <summary>
        /// Validates connection parameters, reachability, VPN status, SSH key authentication, and remote destination directory.
        /// </summary>
        /// <param name="profile">The synchronization profile to validate.</param>
        /// <param name="cancellationToken">Cancellation token to abort the validation probe.</param>
        /// <returns>A <see cref="ConnectionValidationResult"/> containing detailed test results.</returns>
        public async Task<ConnectionValidationResult> ValidateConnectionAsync(SyncProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null)
            {
                return new ConnectionValidationResult
                {
                    Success = false,
                    Message = "No sync profile selected.",
                    Details = "Please select or configure a synchronization profile."
                };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log($"[Validation] Beginning connection validation for profile '{profile.Name}'...", "INFO");

            // 1. Validate required fields
            if (string.IsNullOrWhiteSpace(profile.Host))
            {
                return new ConnectionValidationResult
                {
                    Success = false,
                    Message = "SFTP Host is required.",
                    Details = "Target host/IP address must be specified in profile settings."
                };
            }

            if (profile.Port <= 0 || profile.Port > 65535)
            {
                return new ConnectionValidationResult
                {
                    Success = false,
                    Message = $"Invalid port number: {profile.Port}.",
                    Details = "Port must be between 1 and 65535 (standard SSH port is 22)."
                };
            }

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                return new ConnectionValidationResult
                {
                    Success = false,
                    Message = "SSH Username is required.",
                    Details = "Username must be configured for SSH authentication."
                };
            }

            // 2. Validate private key
            byte[]? decryptedKeyBytes = profile.EncryptedPrivateKey != null && profile.EncryptedPrivateKey.Length > 0
                ? _securityService.DecryptBytes(profile.EncryptedPrivateKey)
                : null;

            string? decryptedPassphrase = profile.EncryptedPassphrase != null && profile.EncryptedPassphrase.Length > 0
                ? _securityService.DecryptString(profile.EncryptedPassphrase)
                : null;

            if (decryptedKeyBytes == null || decryptedKeyBytes.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                {
                    return new ConnectionValidationResult
                    {
                        Success = false,
                        Message = "SSH Private Key is required.",
                        Details = "An Ed25519 or RSA private key is required for authentication."
                    };
                }

                string resolvedKeyPath = SftpTransferService.ResolvePath(profile.PrivateKeyPath);

                if (resolvedKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    return new ConnectionValidationResult
                    {
                        Success = false,
                        Message = "Invalid Key: A Public Key file (.pub) was selected.",
                        Details = "Simply Transfer requires the matching PRIVATE key file (e.g. id_ed25519 without .pub) on this client machine.\n\nPublic keys must be assigned to ~/.ssh/authorized_keys on the destination server."
                    };
                }

                if (!File.Exists(resolvedKeyPath))
                {
                    return new ConnectionValidationResult
                    {
                        Success = false,
                        Message = $"SSH Private Key file not found: '{profile.PrivateKeyPath}'",
                        Details = $"The specified private key path ('{resolvedKeyPath}') does not exist on disk."
                    };
                }
            }

            // 3. VPN Validation (if enabled)
            if (profile.UseVpn)
            {
                Log($"[Validation] Checking VPN tunnel '{profile.VpnConnectionName}' ({profile.VpnType})...", "INFO");
                try
                {
                    bool isVpnActive = await _vpnService.IsVpnConnectedAsync(profile.VpnType, profile.VpnConnectionName);
                    if (!isVpnActive)
                    {
                        Log($"[Validation] VPN is disconnected. Attempting to connect '{profile.VpnConnectionName}'...", "INFO");
                        bool connected = await _vpnService.ConnectVpnAsync(profile.VpnType, profile.VpnConnectionName, profile.VpnConfigPath);
                        if (!connected)
                        {
                            return new ConnectionValidationResult
                            {
                                Success = false,
                                Message = $"Failed to establish VPN connection '{profile.VpnConnectionName}'.",
                                Details = $"Ensure the {profile.VpnType} tunnel is properly configured and running."
                            };
                        }
                    }

                    Log("[Validation] Probing host reachability through VPN tunnel...", "INFO");
                    bool reachable = await _vpnService.PreFlightReachabilityProbeAsync(
                        profile.Host, 
                        profile.Port, 
                        profile.PreFlightTimeoutSeconds, 
                        cancellationToken);

                    if (!reachable)
                    {
                        return new ConnectionValidationResult
                        {
                            Success = false,
                            Message = $"Host {profile.Host}:{profile.Port} is not reachable through VPN tunnel.",
                            Details = $"TCP socket connection timed out after {profile.PreFlightTimeoutSeconds}s."
                        };
                    }
                }
                catch (Exception ex)
                {
                    return new ConnectionValidationResult
                    {
                        Success = false,
                        Message = $"VPN validation error: {ex.Message}",
                        Details = ex.ToString()
                    };
                }
            }
            else
            {
                // Direct TCP reachability probe
                Log($"[Validation] Probing TCP socket reachability for {profile.Host}:{profile.Port}...", "INFO");
                try
                {
                    using var tcpClient = new System.Net.Sockets.TcpClient();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, Math.Max(5, profile.PreFlightTimeoutSeconds))));

                    var connectTask = tcpClient.ConnectAsync(profile.Host, profile.Port);
                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token));

                    if (completedTask != connectTask)
                    {
                        return new ConnectionValidationResult
                        {
                            Success = false,
                            Message = $"TCP connection to {profile.Host}:{profile.Port} timed out.",
                            Details = "Check if the remote host is reachable and port 22 (or configured SSH port) is open in firewalls."
                        };
                    }

                    await connectTask; // Unwrap any exceptions
                    Log($"[Validation] TCP socket {profile.Host}:{profile.Port} reachable.", "SUCCESS");
                }
                catch (Exception ex)
                {
                    return new ConnectionValidationResult
                    {
                        Success = false,
                        Message = $"Unable to reach {profile.Host}:{profile.Port} - {ex.Message}",
                        Details = ex.ToString()
                    };
                }
            }

            // 4. Test SFTP & SSH Authentication
            Log($"[Validation] Authenticating SFTP & SSH session to {profile.Username}@{profile.Host}:{profile.Port}...", "INFO");
            SftpTransferService? testSftpService = null;
            try
            {
                testSftpService = new SftpTransferService(
                    profile.Host,
                    profile.Port,
                    profile.Username,
                    profile.PrivateKeyPath,
                    decryptedKeyBytes,
                    decryptedPassphrase,
                    _hashService);

                string diagnosticInfo = await testSftpService.TestConnectionAsync(profile.DestinationDirectory, cancellationToken);
                sw.Stop();

                Log($"[Validation] Connection validation SUCCESSFUL: {diagnosticInfo} ({sw.ElapsedMilliseconds}ms)", "SUCCESS");

                return new ConnectionValidationResult
                {
                    Success = true,
                    Message = $"Successfully connected and authenticated to {profile.Host}:{profile.Port} ({sw.ElapsedMilliseconds}ms).",
                    Details = diagnosticInfo,
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"[Validation] SFTP authentication failed: {ex.Message}", "ERROR", ex);

                string helpfulDetails = ex.Message;
                if (ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("publickey", StringComparison.OrdinalIgnoreCase))
                {
                    string pubKeyInfo = string.Empty;
                    string resolvedKey = SftpTransferService.ResolvePath(profile.PrivateKeyPath);
                    if (!string.IsNullOrEmpty(resolvedKey) && File.Exists(resolvedKey + ".pub"))
                    {
                        try { pubKeyInfo = File.ReadAllText(resolvedKey + ".pub").Trim(); } catch { }
                    }
                    if (string.IsNullOrEmpty(pubKeyInfo) && !string.IsNullOrEmpty(resolvedKey))
                    {
                        string userSshPub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", Path.GetFileName(resolvedKey) + ".pub");
                        if (File.Exists(userSshPub))
                        {
                            try { pubKeyInfo = File.ReadAllText(userSshPub).Trim(); } catch { }
                        }
                    }
                    if (string.IsNullOrEmpty(pubKeyInfo))
                    {
                        string defaultPub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519.pub");
                        if (File.Exists(defaultPub))
                        {
                            try { pubKeyInfo = File.ReadAllText(defaultPub).Trim(); } catch { }
                        }
                    }

                    helpfulDetails =
                        "The destination server rejected the SSH key: Permission denied (publickey).\n\n" +
                        "POSSIBLE CAUSES & REMEDIES:\n\n" +
                        "1. Public Key Not Authorized on Server:\n" +
                        $"   Your public key must be added to the destination server's authorized_keys file.\n" +
                        $"   Your Public Key:\n   {(string.IsNullOrEmpty(pubKeyInfo) ? "(Check your .pub file)" : pubKeyInfo)}\n\n" +
                        $"2. Username Mismatch:\n" +
                        $"   Ensure '{profile.Username}' exactly matches the local or domain username on {profile.Host}.\n\n" +
                        "3. Windows OpenSSH Server Administrator Gotcha:\n" +
                        $"   If '{profile.Username}' is a member of the local Administrators group on {profile.Host}, Windows OpenSSH ignores C:\\Users\\...\\.ssh\\authorized_keys!\n" +
                        "   You MUST add the public key line to:\n" +
                        "   C:\\ProgramData\\ssh\\administrators_authorized_keys\n" +
                        "   and run: icacls \"C:\\ProgramData\\ssh\\administrators_authorized_keys\" /inheritance:r /grant \"Administrators:F\" /grant \"SYSTEM:F\"\n\n" +
                        "4. Linux / macOS Destination:\n" +
                        $"   Ensure ~/.ssh has chmod 700 and ~/.ssh/authorized_keys has chmod 600 for user '{profile.Username}'.";
                }
                else if (ex.Message.Contains("Invalid private key", StringComparison.OrdinalIgnoreCase))
                {
                    helpfulDetails =
                        "The specified key file is not a valid private key.\n\n" +
                        "Note: Do NOT select the public key file (.pub). Simply Transfer requires the private key file (e.g. id_ed25519 or id_rsa without the .pub extension).";
                }
                else
                {
                    helpfulDetails = ex.ToString();
                }

                return new ConnectionValidationResult
                {
                    Success = false,
                    Message = $"SFTP authentication failed: {ex.Message}",
                    Details = helpfulDetails,
                    LatencyMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                testSftpService?.Dispose();
            }
        }

        /// <summary>
        /// Asynchronously executes the complete end-to-end synchronization workflow for the specified profile and files.
        /// </summary>
        /// <param name="profile">The synchronization profile containing server and VPN configurations.</param>
        /// <param name="items">The collection of files to transfer.</param>
        /// <param name="overallProgress">Optional reporter for overall progress across all items (0.0 to 100.0).</param>
        /// <param name="cancellationToken">Cancellation token to gracefully abort the synchronization.</param>
        /// <returns>True if all files transferred and verified successfully; otherwise false.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="profile"/> is null.</exception>
        public async Task<bool> ExecuteSyncAsync(
            SyncProfile profile, 
            List<TransferItem> items, 
            IProgress<double>? overallProgress = null, 
            CancellationToken cancellationToken = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (items == null || items.Count == 0)
            {
                Log("No files selected for synchronization.", "WARN");
                return false;
            }

            var activeVssServices = new Dictionary<string, VssSnapshotService>(StringComparer.OrdinalIgnoreCase);
            SftpTransferService? sftpService = null;
            TelemetrySenderService? telemetrySender = null;
            bool overallSuccess = true;

            try
            {
                try
                {
                    telemetrySender = new TelemetrySenderService(profile.Host);
                }
                catch { /* Ignore if host is invalid initially */ }

                long totalBytes = items.Sum(i => i.FileSizeBytes);
                long transferredBytesTotal = 0;

                // ==========================================
                // 1. Pre-Flight VPN Orchestration
                // ==========================================
                if (profile.UseVpn)
                {
                    UpdateTelemetry(ConnectionHandshakeState.PreFlightProbe, $"Initiating pre-flight tunnel for '{profile.VpnConnectionName}'...", totalBytes: totalBytes);
                    Log($"[VPN] Initiating pre-flight tunnel for '{profile.VpnConnectionName}' ({profile.VpnType})...", "INFO");
                    
                    bool vpnConnected = await _vpnService.ConnectVpnAsync(profile.VpnType, profile.VpnConnectionName, profile.VpnConfigPath);
                    if (!vpnConnected)
                    {
                        UpdateTelemetry(ConnectionHandshakeState.Failed, $"Failed to establish VPN connection '{profile.VpnConnectionName}'.");
                        Log($"[VPN] Failed to establish VPN connection '{profile.VpnConnectionName}'. Aborting sync.", "ERROR");
                        return false;
                    }

                    Log("[VPN] Tunnel active. Probing target host reachability...", "INFO");
                    bool reachable = await _vpnService.PreFlightReachabilityProbeAsync(
                        profile.Host, 
                        profile.Port, 
                        profile.PreFlightTimeoutSeconds, 
                        cancellationToken);

                    if (!reachable)
                    {
                        UpdateTelemetry(ConnectionHandshakeState.Failed, $"Pre-flight reachability probe failed: {profile.Host}:{profile.Port} unreachable.");
                        Log($"[VPN] Pre-flight reachability probe failed: {profile.Host}:{profile.Port} is not reachable via tunnel within {profile.PreFlightTimeoutSeconds}s.", "ERROR");
                        return false;
                    }

                    Log($"[VPN] Target host {profile.Host}:{profile.Port} successfully verified reachable.", "SUCCESS");
                }

                // ==========================================
                // 2. QuickBooks & VSS Snapshot Preparation
                // ==========================================
                bool needsVss = profile.UseVssForLockedFiles && items.Any(i => i.IsQuickBooksFile || i.IsVssRequired);
                if (needsVss)
                {
                    UpdateTelemetry(ConnectionHandshakeState.VssSnapshotting, "Creating Volume Shadow Copy snapshots for locked databases...", totalBytes: totalBytes);
                    Log("[VSS] QuickBooks or locked files detected. Preparing Volume Shadow Copy snapshots...", "INFO");

                    if (!VssSnapshotService.IsAdministrator())
                    {
                        Log("[VSS] Warning: Application is not running as Administrator. VSS snapshots may fail if files are exclusively locked.", "WARN");
                    }

                    // Group files by volume root (e.g., "C:\")
                    var volumes = items
                        .Where(i => i.IsQuickBooksFile || i.IsVssRequired)
                        .Select(i => Path.GetPathRoot(i.LocalFilePath))
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var vol in volumes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (vol == null) continue;

                        try
                        {
                            Log($"[VSS] Creating atomic snapshot for volume {vol}...", "INFO");
                            var vss = new VssSnapshotService();
                            string deviceObject = vss.CreateSnapshot(vol);
                            activeVssServices[vol] = vss;
                            Log($"[VSS] Snapshot mounted at {deviceObject} for volume {vol}.", "SUCCESS");
                        }
                        catch (Exception ex)
                        {
                            Log($"[VSS] Failed to create VSS snapshot for volume {vol}: {ex.Message}. Attempting standard file read.", "WARN");
                        }
                    }
                }

                // ==========================================
                // 3. Connect Pure Key-Based SFTP
                // ==========================================
                UpdateTelemetry(ConnectionHandshakeState.SshHandshake, $"Negotiating SSH handshake with {profile.Host}:{profile.Port}...", totalBytes: totalBytes);
                Log($"[SFTP] Connecting to {profile.Username}@{profile.Host}:{profile.Port} using key authentication...", "INFO");

                byte[]? decryptedKeyBytes = profile.EncryptedPrivateKey != null 
                    ? _securityService.DecryptBytes(profile.EncryptedPrivateKey) 
                    : null;
                
                string? decryptedPassphrase = profile.EncryptedPassphrase != null 
                    ? _securityService.DecryptString(profile.EncryptedPassphrase) 
                    : null;

                sftpService = new SftpTransferService(
                    profile.Host,
                    profile.Port,
                    profile.Username,
                    profile.PrivateKeyPath,
                    decryptedKeyBytes,
                    decryptedPassphrase,
                    _hashService);

                UpdateTelemetry(ConnectionHandshakeState.KeyAuthentication, $"Authenticating public key for user '{profile.Username}'...", totalBytes: totalBytes);
                await sftpService.ConnectAsync(cancellationToken);
                UpdateTelemetry(ConnectionHandshakeState.SftpSessionEstablished, "SFTP subsystem session established.", totalBytes: totalBytes);
                Log("[SFTP] Authenticated and connected successfully.", "SUCCESS");

                // Ensure remote base directory exists
                await sftpService.EnsureRemoteDirectoryExistsAsync(profile.DestinationDirectory, cancellationToken);

                // ==========================================
                // 4. File Transfers & SHA-256 Validation
                // ==========================================
                for (int idx = 0; idx < items.Count; idx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = items[idx];

                    UpdateTelemetry(ConnectionHandshakeState.StreamingPayload, $"Uploading [{idx + 1}/{items.Count}] {item.FileName}...", item.FileName, transferredBytesTotal, totalBytes, 0, 0, idx + 1, items.Count);
                    Log($"[Transfer] [{idx + 1}/{items.Count}] Processing {item.FileName} ({item.FormattedSize})...", "INFO");
                    item.Status = TransferStatus.Transferring;
                    item.ProgressPercentage = 0;
                    ItemProgressUpdated?.Invoke(this, item);

                    // Write status file to destination for tracking
                    try
                    {
                        string statusFile = CombineUnixPath(profile.DestinationDirectory, ".sync-status.json");
                        string statusJson = $"{{\n  \"CurrentFile\": \"{item.FileName}\",\n  \"Progress\": 0,\n  \"TransferredBytes\": {transferredBytesTotal},\n  \"TotalBytes\": {totalBytes}\n}}";
                        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(statusJson));
                        await sftpService.UploadStreamAsync(ms, statusFile, null, cancellationToken);
                    }
                    catch { /* Ignore status write errors */ }

                    Stream? fileStream = null;
                    try
                    {
                        string volumeRoot = Path.GetPathRoot(item.LocalFilePath) ?? string.Empty;
                        bool usingVss = activeVssServices.TryGetValue(volumeRoot, out var vss) && (item.IsQuickBooksFile || item.IsVssRequired);

                        if (usingVss && vss != null)
                        {
                            Log($"[VSS] Extracting '{item.FileName}' from shadow volume snapshot...", "INFO");
                            fileStream = vss.OpenSnapshotFile(item.LocalFilePath);
                            item.VssResolvedPath = vss.GetSnapshotFilePath(item.LocalFilePath);
                        }
                        else
                        {
                            fileStream = new FileStream(item.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
                        }

                        // Compute local SHA-256 hash
                        Log($"[Crypto] Computing local SHA-256 for '{item.FileName}'...", "INFO");
                        item.LocalSha256 = await _hashService.ComputeStreamHashAsync(fileStream, cancellationToken: cancellationToken);
                        Log($"[Crypto] Local SHA-256: {item.LocalSha256}", "INFO");

                        // Reset stream position to beginning for upload
                        fileStream.Position = 0;

                        // Upload to remote SFTP
                        string remoteTarget = CombineUnixPath(profile.DestinationDirectory, item.RemoteFilePath);

                        await sftpService.UploadStreamAsync(
                            fileStream,
                            remoteTarget,
                            (bytesUploaded, speed) =>
                            {
                                item.BytesTransferred = (long)bytesUploaded;
                                item.TransferSpeedBps = speed;
                                if (item.FileSizeBytes > 0)
                                {
                                    item.ProgressPercentage = Math.Min(100.0, (double)item.BytesTransferred / item.FileSizeBytes * 100.0);
                                }
                                
                                long curTotal = transferredBytesTotal + (long)bytesUploaded;
                                if (totalBytes > 0 && overallProgress != null)
                                {
                                    overallProgress.Report(Math.Min(100.0, (double)curTotal / totalBytes * 100.0));
                                }

                                UpdateTelemetry(ConnectionHandshakeState.StreamingPayload, $"Uploading {item.FileName} ({speed / (1024.0 * 1024.0):F2} MB/s)", item.FileName, curTotal, totalBytes, speed, 0, idx + 1, items.Count);
                                ItemProgressUpdated?.Invoke(this, item);

                                telemetrySender?.SendTelemetry(new TelemetryPacket
                                {
                                    Action = "Transferring",
                                    CurrentFile = item.FileName,
                                    ProgressPercentage = item.ProgressPercentage,
                                    BytesTransferred = curTotal,
                                    TotalBytes = totalBytes,
                                    TransferSpeedBps = speed,
                                    ItemIndex = idx + 1,
                                    TotalItems = items.Count
                                });
                            },
                            cancellationToken);

                        transferredBytesTotal += item.FileSizeBytes;
                        item.ProgressPercentage = 100.0;
                        Log($"[SFTP] Uploaded '{item.FileName}' to '{remoteTarget}'.", "SUCCESS");

                        try
                        {
                            string statusFile = CombineUnixPath(profile.DestinationDirectory, ".sync-status.json");
                            string statusJson = $"{{\n  \"CurrentFile\": \"{item.FileName}\",\n  \"Progress\": 100,\n  \"TransferredBytes\": {transferredBytesTotal},\n  \"TotalBytes\": {totalBytes}\n}}";
                            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(statusJson));
                            await sftpService.UploadStreamAsync(ms, statusFile, null, cancellationToken);
                        }
                        catch { /* Ignore */ }

                        // Two-step automated SHA-256 and byte parity verification
                        item.Status = TransferStatus.Validating;
                        item.HashStatus = HashMatchStatus.Validating;
                        UpdateTelemetry(ConnectionHandshakeState.RemoteIntegrityHashing, $"Computing remote SHA-256 and checking byte parity for {item.FileName}...", item.FileName, transferredBytesTotal, totalBytes, 0, 0, idx + 1, items.Count);
                        ItemProgressUpdated?.Invoke(this, item);

                        Log($"[Verify] Executing remote cryptographic SHA-256 validation for '{remoteTarget}'...", "INFO");
                        string remoteHash = await sftpService.GetRemoteFileSha256Async(remoteTarget, cancellationToken);
                        item.RemoteSha256 = remoteHash;
                        Log($"[Verify] Remote SHA-256: {item.RemoteSha256}", "INFO");

                        long remoteSize = await sftpService.GetRemoteFileSizeAsync(remoteTarget, cancellationToken);
                        item.RemoteFileSizeBytes = remoteSize;
                        Log($"[Verify] Remote File Size: {remoteSize} bytes (Local: {item.FileSizeBytes} bytes)", "INFO");

                        bool hashMatches = _hashService.ValidateHashes(item.LocalSha256, item.RemoteSha256);
                        bool sizeMatches = (remoteSize > 0 && remoteSize == item.FileSizeBytes) || (item.FileSizeBytes == 0 && remoteSize == 0);

                        if (hashMatches && sizeMatches)
                        {
                            item.HashStatus = HashMatchStatus.Verified;
                            item.Status = TransferStatus.Completed;
                            item.StatusMessage = $"Verified ({item.FormattedSize} & Hash Match)";
                            item.CompletedTime = DateTime.Now;
                            UpdateTelemetry(ConnectionHandshakeState.PayloadVerified, $"✓ Bit-perfect match confirmed for {item.FileName} ({item.FormattedSize}).", item.FileName, transferredBytesTotal, totalBytes, 0, 0, idx + 1, items.Count);
                            Log($"[Verify] ✓ HASH & BYTE PARITY MATCH CONFIRMED for '{item.FileName}'. SHA-256: {item.LocalSha256} | Size: {item.FormattedSize}", "SUCCESS");

                            telemetrySender?.SendTelemetry(new TelemetryPacket
                            {
                                Action = "Verified",
                                CurrentFile = item.FileName,
                                ProgressPercentage = 100,
                                BytesTransferred = transferredBytesTotal,
                                TotalBytes = totalBytes,
                                StatusMessage = "File Verified Successfully"
                            });
                        }
                        else if (!hashMatches)
                        {
                            item.HashStatus = HashMatchStatus.Mismatch;
                            item.Status = TransferStatus.Failed;
                            item.StatusMessage = "Hash mismatch";
                            item.ErrorMessage = $"Hash mismatch! Local: {item.LocalSha256} != Remote: {item.RemoteSha256}";
                            Log($"[Verify] ✗ HASH MISMATCH for '{item.FileName}'! Local: {item.LocalSha256}, Remote: {item.RemoteSha256}", "ERROR");
                            overallSuccess = false;
                        }
                        else
                        {
                            item.HashStatus = HashMatchStatus.Mismatch;
                            item.Status = TransferStatus.Failed;
                            item.StatusMessage = "Size parity mismatch";
                            item.ErrorMessage = $"File size parity failed! Local: {item.FileSizeBytes} B != Remote: {remoteSize} B";
                            Log($"[Verify] ✗ SIZE PARITY MISMATCH for '{item.FileName}'! Local: {item.FileSizeBytes} B, Remote: {remoteSize} B", "ERROR");
                            overallSuccess = false;
                        }

                        ItemProgressUpdated?.Invoke(this, item);
                    }
                    catch (Exception ex)
                    {
                        item.Status = TransferStatus.Failed;
                        item.HashStatus = HashMatchStatus.Mismatch;
                        item.ErrorMessage = ex.Message;
                        item.StatusMessage = $"Failed: {ex.Message}";
                        Log($"[Transfer] Error transferring '{item.FileName}': {ex.Message}", "ERROR");
                        overallSuccess = false;
                        ItemProgressUpdated?.Invoke(this, item);
                    }
                    finally
                    {
                        fileStream?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                UpdateTelemetry(ConnectionHandshakeState.Failed, "Synchronization canceled by user.");
                Log("[Sync] Synchronization was canceled by user.", "WARN");
                throw;
            }
            catch (Exception ex)
            {
                UpdateTelemetry(ConnectionHandshakeState.Failed, $"Critical sync error: {ex.Message}");
                Log($"[Sync] Critical synchronization error: {ex.Message}", "ERROR", ex);
                overallSuccess = false;
            }
            finally
            {
                // ==========================================
                // 5. Cleanup Resources (VSS, SFTP, VPN)
                // ==========================================
                Log("[Cleanup] Releasing VSS snapshots and closing connections...", "INFO");

                foreach (var kvp in activeVssServices)
                {
                    try
                    {
                        kvp.Value.Dispose();
                        Log($"[VSS] Released snapshot for volume {kvp.Key}.", "INFO");
                    }
                    catch (Exception ex)
                    {
                        Log($"[VSS] Error disposing snapshot for {kvp.Key}: {ex.Message}", "WARN");
                    }
                }

                sftpService?.Dispose();
                telemetrySender?.Dispose();

                if (profile.UseVpn && profile.DisconnectVpnAfterSync)
                {
                    Log($"[VPN] Disconnecting VPN '{profile.VpnConnectionName}' as configured...", "INFO");
                    await _vpnService.DisconnectVpnAsync(profile.VpnType, profile.VpnConnectionName);
                    Log("[VPN] VPN disconnected.", "INFO");
                }
            }

            UpdateTelemetry(overallSuccess ? ConnectionHandshakeState.Completed : ConnectionHandshakeState.Failed, overallSuccess ? "Sync operation completed successfully." : "Sync operation completed with errors.");
            Log(overallSuccess ? "Sync operation completed successfully." : "Sync operation completed with errors.", overallSuccess ? "SUCCESS" : "WARN");
            return overallSuccess;
        }

        private static string CombineUnixPath(string basePath, string relativePath)
        {
            string baseNorm = basePath.Replace('\\', '/').TrimEnd('/');
            
            // If baseNorm is a Windows drive letter (e.g. "C:/..."), prefix it with '/' so SFTP treats it as absolute
            if (System.Text.RegularExpressions.Regex.IsMatch(baseNorm, @"^[a-zA-Z]:"))
            {
                baseNorm = "/" + baseNorm;
            }

            string relNorm = relativePath.Replace('\\', '/').TrimStart('/');
            return $"{baseNorm}/{relNorm}";
        }
    }
}
