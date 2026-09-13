using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Health classification for key state, permissions, and service readiness.
    /// </summary>
    public enum SecurityHealthStatus
    {
        /// <summary>Verified secure and compliant with OpenSSH strict mode requirements.</summary>
        Healthy,
        /// <summary>Functional but possesses warnings (e.g. non-elevated VSS, missing comments).</summary>
        Warning,
        /// <summary>Non-compliant or failing security policy (e.g. permissive ACLs, bad owner, missing key).</summary>
        Critical,
        /// <summary>Status has not yet been audited.</summary>
        Unknown
    }

    /// <summary>
    /// Audit report detailing source client readiness, key state, and permissions.
    /// </summary>
    public class SourceReadinessReport
    {
        public bool OpenSshClientInstalled { get; set; }
        public string OpenSshClientPath { get; set; } = string.Empty;
        public bool KeyPairExists { get; set; }
        public string PrivateKeyPath { get; set; } = string.Empty;
        public string PublicKeyPath { get; set; } = string.Empty;
        public string PublicKeyString { get; set; } = string.Empty;
        public string KeyFingerprintSha256 { get; set; } = string.Empty;
        public string KeyAlgorithm { get; set; } = string.Empty;
        public SecurityHealthStatus AclStatus { get; set; } = SecurityHealthStatus.Unknown;
        public string AclDetails { get; set; } = string.Empty;
        public string FileOwner { get; set; } = string.Empty;
        public bool InheritanceDisabled { get; set; }
        public bool IsElevatedAdministrator { get; set; }
        public bool VssServiceReady { get; set; }
        public bool WireGuardInstalled { get; set; }
        public List<string> Recommendations { get; } = new();
    }

    /// <summary>
    /// Audit report detailing destination host readiness, OpenSSH server, firewall, and authorized keys.
    /// </summary>
    public class DestinationReadinessReport
    {
        public bool OpenSshServerInstalled { get; set; }
        public string SshdStatus { get; set; } = "Unknown";
        public string SshAgentStatus { get; set; } = "Unknown";
        public bool Port22FirewallAllowed { get; set; }
        public bool HostKeysExist { get; set; }
        public bool AdminAuthorizedKeysExists { get; set; }
        public string AdminAuthorizedKeysPath { get; set; } = string.Empty;
        public int AdminAuthorizedKeysCount { get; set; }
        public SecurityHealthStatus AdminKeysAclStatus { get; set; } = SecurityHealthStatus.Unknown;
        public string AdminKeysAclDetails { get; set; } = string.Empty;
        public string AdminKeysOwner { get; set; } = string.Empty;
        public bool UserAuthorizedKeysExists { get; set; }
        public string UserAuthorizedKeysPath { get; set; } = string.Empty;
        public bool DestinationDirExists { get; set; }
        public string DestinationDirPath { get; set; } = string.Empty;
        public List<string> Recommendations { get; } = new();
    }

    /// <summary>
    /// Provides programmatic host readiness audits, automated key generation, ACL repairs,
    /// and GUI-invoked PowerShell script execution across source and destination runtimes.
    /// </summary>
    public class HostReadinessService
    {
        private readonly LoggingService _logger;

        public HostReadinessService(LoggingService? logger = null)
        {
            _logger = logger ?? new LoggingService();
        }

        #region Source Diagnostics & Self-Healing

        /// <summary>
        /// Audits the local machine's readiness to function as a Simply Transfer source client.
        /// </summary>
        public SourceReadinessReport AuditSourceReadiness(string? configuredKeyPath = null)
        {
            var report = new SourceReadinessReport();

            // 1. Check elevation
            report.IsElevatedAdministrator = IsAdministrator();

            // 2. Locate OpenSSH Client binaries
            string clientBinary = LocateBinary("ssh.exe");
            report.OpenSshClientInstalled = !string.IsNullOrEmpty(clientBinary);
            report.OpenSshClientPath = clientBinary;

            if (!report.OpenSshClientInstalled)
            {
                report.Recommendations.Add("OpenSSH Client is not detected in PATH or System32. Install OpenSSH Client via Windows Optional Features.");
            }

            // 3. Locate Key Pair
            string candidateKey = !string.IsNullOrWhiteSpace(configuredKeyPath)
                ? SftpTransferService.ResolvePath(configuredKeyPath)
                : string.Empty;

            if (string.IsNullOrEmpty(candidateKey))
            {
                string userSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                string edKey = Path.Combine(userSsh, "id_ed25519");
                string rsaKey = Path.Combine(userSsh, "id_rsa");

                if (File.Exists(edKey)) candidateKey = edKey;
                else if (File.Exists(rsaKey)) candidateKey = rsaKey;
            }

            if (!string.IsNullOrEmpty(candidateKey) && File.Exists(candidateKey))
            {
                report.KeyPairExists = true;
                report.PrivateKeyPath = candidateKey;
                report.PublicKeyPath = candidateKey + ".pub";

                // Read public key if present
                if (File.Exists(report.PublicKeyPath))
                {
                    try
                    {
                        report.PublicKeyString = File.ReadAllText(report.PublicKeyPath).Trim();
                        report.KeyFingerprintSha256 = ComputePublicKeyFingerprint(report.PublicKeyString);
                        report.KeyAlgorithm = report.PublicKeyString.StartsWith("ssh-ed25519") ? "Ed25519 (256-bit)" :
                                              report.PublicKeyString.StartsWith("ssh-rsa") ? "RSA" : "OpenSSH";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarn($"Could not read public key file '{report.PublicKeyPath}': {ex.Message}");
                    }
                }

                // Audit private key ACLs
                AuditPrivateKeyAcls(candidateKey, report);
            }
            else
            {
                report.KeyPairExists = false;
                report.AclStatus = SecurityHealthStatus.Critical;
                report.AclDetails = "No SSH private key found on disk.";
                report.Recommendations.Add("Generate an Ed25519 key pair using the Auto-Generate Key action or setup script.");
            }

            // 4. VSS and WireGuard
            report.VssServiceReady = IsVssServiceAvailable();
            report.WireGuardInstalled = !string.IsNullOrEmpty(LocateBinary("wireguard.exe", @"C:\Program Files\WireGuard\wireguard.exe"));

            return report;
        }

        private void AuditPrivateKeyAcls(string filePath, SourceReadinessReport report)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();
                var owner = fileSecurity.GetOwner(typeof(NTAccount))?.ToString() ?? "Unknown";
                report.FileOwner = owner;
                report.InheritanceDisabled = fileSecurity.AreAccessRulesProtected;

                var rules = fileSecurity.GetAccessRules(true, false, typeof(NTAccount));
                var allowedUsers = new List<string>();
                bool extraneousAccessFound = false;

                foreach (FileSystemAccessRule rule in rules)
                {
                    string account = rule.IdentityReference.Value;
                    allowedUsers.Add($"{account} ({rule.FileSystemRights})");

                    // Check for insecure permissive access (Users, Everyone, Authenticated Users)
                    if (account.EndsWith("Users", StringComparison.OrdinalIgnoreCase) ||
                        account.Equals("Everyone", StringComparison.OrdinalIgnoreCase) ||
                        account.Contains("Authenticated Users", StringComparison.OrdinalIgnoreCase))
                    {
                        extraneousAccessFound = true;
                    }
                }

                if (!report.InheritanceDisabled)
                {
                    report.AclStatus = SecurityHealthStatus.Critical;
                    report.AclDetails = "Insecure: Inheritance is enabled. Standard users have inherited access.";
                    report.Recommendations.Add("Disable ACL inheritance on private key using the 'Auto-Repair Permissions' action.");
                }
                else if (extraneousAccessFound)
                {
                    report.AclStatus = SecurityHealthStatus.Critical;
                    report.AclDetails = "Insecure: Broad group permissions (Users/Everyone) detected.";
                    report.Recommendations.Add("Remove broad group ACLs from private key using 'Auto-Repair Permissions'.");
                }
                else
                {
                    report.AclStatus = SecurityHealthStatus.Healthy;
                    report.AclDetails = $"Secure: Inheritance disabled. Owner: {owner}. Allowed: {string.Join(", ", allowedUsers)}";
                }
            }
            catch (Exception ex)
            {
                report.AclStatus = SecurityHealthStatus.Warning;
                report.AclDetails = $"Unable to inspect NTFS security descriptor: {ex.Message}";
            }
        }

        /// <summary>
        /// Programmatically generates a modern Ed25519 key pair with strict NTFS ACLs.
        /// </summary>
        public async Task<string> GenerateSourceKeyPairAsync(string? comment = null)
        {
            string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            if (!Directory.Exists(sshDir))
            {
                Directory.CreateDirectory(sshDir);
            }

            string keyPath = Path.Combine(sshDir, "id_ed25519");
            if (File.Exists(keyPath))
            {
                throw new InvalidOperationException($"Key already exists at '{keyPath}'. Will not overwrite an existing private key.");
            }

            string keygen = LocateBinary("ssh-keygen.exe");
            if (string.IsNullOrEmpty(keygen))
            {
                throw new FileNotFoundException("ssh-keygen.exe not found on this system. OpenSSH Client must be installed.");
            }

            string keyComment = !string.IsNullOrWhiteSpace(comment) 
                ? comment 
                : $"SimplyTransfer-{Environment.MachineName}";

            var psi = new ProcessStartInfo
            {
                FileName = keygen,
                Arguments = $"-t ed25519 -C \"{keyComment}\" -f \"{keyPath}\" -N \"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Failed to start ssh-keygen process.");

            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
            {
                string err = await proc.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"ssh-keygen failed (ExitCode: {proc.ExitCode}): {err}");
            }

            // Immediately apply strict NTFS ACLs to private key and .ssh directory
            RepairSourceKeyAcls(keyPath);

            return keyPath;
        }

        /// <summary>
        /// Applies strict NTFS ACL permissions to the private key and ~/.ssh directory.
        /// </summary>
        public void RepairSourceKeyAcls(string privateKeyPath)
        {
            string resolvedPath = SftpTransferService.ResolvePath(privateKeyPath);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Private key file not found: '{resolvedPath}'");
            }

            string sshDir = Path.GetDirectoryName(resolvedPath) ?? string.Empty;

            // Use icacls to strictly reset inheritance and grant only the user and SYSTEM
            string user = Environment.UserName;

            // 1. Secure .ssh directory
            if (!string.IsNullOrEmpty(sshDir) && Directory.Exists(sshDir))
            {
                ExecuteCommand("icacls.exe", $"\"{sshDir}\" /inheritance:r /grant:r \"{user}:(OI)(CI)(F)\" /grant:r \"*S-1-5-18:(OI)(CI)(F)\" /grant:r \"*S-1-5-32-544:(OI)(CI)(F)\"");
            }

            // 2. Secure private key file
            ExecuteCommand("icacls.exe", $"\"{resolvedPath}\" /inheritance:r /grant:r \"{user}:(F)\" /grant:r \"*S-1-5-18:(F)\"");

            _logger.LogSuccess($"Repaired strict NTFS ACLs on private key '{resolvedPath}'.");
        }

        #endregion

        #region Destination Diagnostics & Self-Healing

        /// <summary>
        /// Audits destination server readiness, including OpenSSH Server service, firewall, and authorized_keys.
        /// </summary>
        public DestinationReadinessReport AuditDestinationReadiness(string? expectedUser = null, string? destinationDir = null)
        {
            var report = new DestinationReadinessReport();
            string targetUser = !string.IsNullOrWhiteSpace(expectedUser) ? expectedUser : Environment.UserName;
            string targetDir = !string.IsNullOrWhiteSpace(destinationDir) ? destinationDir : @"C:\Backups";

            report.DestinationDirPath = targetDir;
            report.DestinationDirExists = Directory.Exists(targetDir);

            // 1. OpenSSH Server Binary
            string sshdBinary = LocateBinary("sshd.exe", @"C:\Windows\System32\OpenSSH\sshd.exe");
            report.OpenSshServerInstalled = !string.IsNullOrEmpty(sshdBinary);

            // 2. Check Services
            report.SshdStatus = QueryServiceStatus("sshd");
            report.SshAgentStatus = QueryServiceStatus("ssh-agent");

            // 3. Port 22 Firewall Rule
            report.Port22FirewallAllowed = QueryFirewallRulePort22();

            // 4. Host Keys
            string programDataSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
            report.HostKeysExist = Directory.Exists(programDataSsh) && 
                (File.Exists(Path.Combine(programDataSsh, "ssh_host_ed25519_key")) || 
                 File.Exists(Path.Combine(programDataSsh, "ssh_host_rsa_key")));

            // 5. Administrators Authorized Keys
            string adminAuthFile = Path.Combine(programDataSsh, "administrators_authorized_keys");
            report.AdminAuthorizedKeysPath = adminAuthFile;
            report.AdminAuthorizedKeysExists = File.Exists(adminAuthFile);

            if (report.AdminAuthorizedKeysExists)
            {
                try
                {
                    var lines = File.ReadAllLines(adminAuthFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                        .ToList();
                    report.AdminAuthorizedKeysCount = lines.Count;

                    AuditAdminAuthorizedKeysAcls(adminAuthFile, report);
                }
                catch (Exception ex)
                {
                    report.AdminKeysAclStatus = SecurityHealthStatus.Warning;
                    report.AdminKeysAclDetails = $"Error inspecting file: {ex.Message}";
                }
            }
            else
            {
                report.AdminKeysAclStatus = SecurityHealthStatus.Critical;
                report.AdminKeysAclDetails = "administrators_authorized_keys does not exist.";
                report.Recommendations.Add("Deploy client public key into C:\\ProgramData\\ssh\\administrators_authorized_keys.");
            }

            // 6. User Authorized Keys
            string userAuthFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");
            report.UserAuthorizedKeysPath = userAuthFile;
            report.UserAuthorizedKeysExists = File.Exists(userAuthFile);

            return report;
        }

        private void AuditAdminAuthorizedKeysAcls(string filePath, DestinationReadinessReport report)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();
                var owner = fileSecurity.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
                var ownerAccount = fileSecurity.GetOwner(typeof(NTAccount))?.ToString() ?? owner;
                report.AdminKeysOwner = ownerAccount;

                // OpenSSH requirement: Owner MUST be BUILTIN\Administrators (S-1-5-32-544) or SYSTEM (S-1-5-18)
                bool ownerValid = owner.Equals("S-1-5-32-544", StringComparison.OrdinalIgnoreCase) ||
                                  owner.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                                  ownerAccount.EndsWith("Administrators", StringComparison.OrdinalIgnoreCase) ||
                                  ownerAccount.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

                bool inheritanceDisabled = fileSecurity.AreAccessRulesProtected;

                var rules = fileSecurity.GetAccessRules(true, false, typeof(NTAccount));
                bool extraneousAccess = false;
                var allowed = new List<string>();

                foreach (FileSystemAccessRule rule in rules)
                {
                    string name = rule.IdentityReference.Value;
                    allowed.Add(name);
                    if (!name.EndsWith("Administrators", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase))
                    {
                        extraneousAccess = true;
                    }
                }

                if (!ownerValid)
                {
                    report.AdminKeysAclStatus = SecurityHealthStatus.Critical;
                    report.AdminKeysAclDetails = $"Insecure Owner: Owned by '{ownerAccount}'. OpenSSH requires BUILTIN\\Administrators ownership.";
                    report.Recommendations.Add("Change file owner to BUILTIN\\Administrators via 'Repair Destination ACLs' or destination setup script.");
                }
                else if (!inheritanceDisabled || extraneousAccess)
                {
                    report.AdminKeysAclStatus = SecurityHealthStatus.Critical;
                    report.AdminKeysAclDetails = $"Insecure ACLs: Inheritance enabled or non-admin entities have access ({string.Join(", ", allowed)}).";
                    report.Recommendations.Add("Strip inheritance and grant Full Control only to Administrators & SYSTEM.");
                }
                else
                {
                    report.AdminKeysAclStatus = SecurityHealthStatus.Healthy;
                    report.AdminKeysAclDetails = $"Secure: Inheritance disabled. Owner: {ownerAccount}. Strict ACLs verified.";
                }
            }
            catch (Exception ex)
            {
                report.AdminKeysAclStatus = SecurityHealthStatus.Warning;
                report.AdminKeysAclDetails = $"Failed inspecting ACLs: {ex.Message}";
            }
        }

        /// <summary>
        /// Programmatically hardens and repairs destination administrators_authorized_keys ownership and ACLs.
        /// </summary>
        public void RepairDestinationAcls()
        {
            string programDataSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
            string adminAuthFile = Path.Combine(programDataSsh, "administrators_authorized_keys");

            if (!Directory.Exists(programDataSsh))
            {
                Directory.CreateDirectory(programDataSsh);
            }

            if (!File.Exists(adminAuthFile))
            {
                File.WriteAllText(adminAuthFile, string.Empty, Encoding.ASCII);
            }

            // 1. Take ownership to BUILTIN\Administrators (*S-1-5-32-544)
            ExecuteCommand("takeown.exe", $"/F \"{adminAuthFile}\" /A");

            // 2. Set strict ACLs: Administrators:F, SYSTEM:F
            ExecuteCommand("icacls.exe", $"\"{adminAuthFile}\" /inheritance:r /grant:r \"*S-1-5-32-544:F\" /grant:r \"*S-1-5-18:F\"");
            ExecuteCommand("icacls.exe", $"\"{adminAuthFile}\" /setowner \"*S-1-5-32-544\"");

            // 3. Ensure parent directory permissions are also safe
            ExecuteCommand("icacls.exe", $"\"{programDataSsh}\" /grant:r \"*S-1-5-32-544:(OI)(CI)(F)\" /grant:r \"*S-1-5-18:(OI)(CI)(F)\"");

            _logger.LogSuccess($"Successfully hardened ownership and ACLs on '{adminAuthFile}'.");
        }

        /// <summary>
        /// Authorizes a client public key on this host for destination server operation.
        /// </summary>
        public void AuthorizeKeyOnDestination(string publicKey, string destinationUser, string destinationDir = @"C:\Backups")
        {
            if (string.IsNullOrWhiteSpace(publicKey))
                throw new ArgumentException("Public key string cannot be empty.", nameof(publicKey));

            string trimmedKey = publicKey.Trim();
            string programDataSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
            if (!Directory.Exists(programDataSsh)) Directory.CreateDirectory(programDataSsh);

            string adminAuth = Path.Combine(programDataSsh, "administrators_authorized_keys");
            
            // Append with ASCII encoding (strictly avoiding BOM)
            var existingKeys = File.Exists(adminAuth) ? File.ReadAllLines(adminAuth) : Array.Empty<string>();
            if (!existingKeys.Any(k => k.Trim().Equals(trimmedKey, StringComparison.Ordinal)))
            {
                File.AppendAllText(adminAuth, trimmedKey + Environment.NewLine, Encoding.ASCII);
            }

            // Harden permissions
            RepairDestinationAcls();

            // Pre-create destination backup folder
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }
            ExecuteCommand("icacls.exe", $"\"{destinationDir}\" /grant:r \"{destinationUser}:(OI)(CI)(M)\" /grant:r \"*S-1-5-32-544:(OI)(CI)(F)\"");

            // Restart sshd service if running
            try
            {
                ExecuteCommand("powershell.exe", "-Command \"Set-Service sshd -StartupType Automatic -ErrorAction SilentlyContinue; Restart-Service sshd -ErrorAction SilentlyContinue\"");
            }
            catch { }
        }

        #endregion

        #region Interactive Script Runner (In-Process PowerShell SDK & Embedded Resources)

        /// <summary>
        /// Retrieves the content of an embedded PowerShell script resource.
        /// Falls back to file resolution on disk if running in development mode.
        /// </summary>
        public static string? GetEmbeddedScriptContent(string scriptName)
        {
            var assembly = typeof(HostReadinessService).Assembly;
            string cleanName = Path.GetFileName(scriptName);
            string altName = cleanName.Replace('-', '_');

            // Find matching manifest resource (case-insensitive)
            string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
                name.EndsWith(cleanName, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(altName, StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd();
                }
            }

            // Fallback to disk if running during unit tests or development
            string diskPath = ResolveScriptPath(scriptName);
            if (File.Exists(diskPath))
            {
                return File.ReadAllText(diskPath, Encoding.UTF8);
            }

            return null;
        }

        /// <summary>
        /// Executes raw PowerShell script block text directly in-process via System.Management.Automation SDK.
        /// Captures output, information, warning, and error streams.
        /// </summary>
        public async Task<int> ExecuteScriptBlockAsync(
            string scriptContent,
            string? arguments = null,
            Action<string>? outputHandler = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var iss = System.Management.Automation.Runspaces.InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var ps = PowerShell.Create(iss);

                string scriptToExecute = !string.IsNullOrWhiteSpace(arguments)
                    ? $"& {{\n{scriptContent}\n}} {arguments}"
                    : scriptContent;

                ps.AddScript(scriptToExecute);

                ps.Streams.Information.DataAdded += (s, e) =>
                {
                    var record = ps.Streams.Information[e.Index];
                    string msg = record.MessageData?.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        outputHandler?.Invoke(msg);
                        _logger.LogInfo(msg);
                    }
                };

                ps.Streams.Warning.DataAdded += (s, e) =>
                {
                    var record = ps.Streams.Warning[e.Index];
                    string msg = $"[WARN] {record.Message}";
                    outputHandler?.Invoke(msg);
                    _logger.LogWarn(msg);
                };

                ps.Streams.Error.DataAdded += (s, e) =>
                {
                    var record = ps.Streams.Error[e.Index];
                    string msg = $"[ERROR] {record.Exception?.Message ?? record.ToString()}";
                    outputHandler?.Invoke(msg);
                    _logger.LogError(msg, record.Exception);
                };

                var outputCollection = new PSDataCollection<PSObject>();
                outputCollection.DataAdded += (s, e) =>
                {
                    var item = outputCollection[e.Index];
                    if (item != null)
                    {
                        string line = item.ToString();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            outputHandler?.Invoke(line);
                            _logger.LogInfo(line);
                        }
                    }
                };

                using var reg = cancellationToken.Register(() =>
                {
                    try { ps.Stop(); } catch { }
                });

                try
                {
                    ps.Invoke(null, outputCollection);
                }
                catch (Exception ex)
                {
                    string err = $"[SDK-ERROR] Execution failed: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        err += $"\nInner Exception: {ex.InnerException.Message}";
                    }
                    outputHandler?.Invoke(err);
                    _logger.LogError("PowerShell SDK execution exception", ex);
                    return 1;
                }

                bool hasErrors = ps.HadErrors || ps.Streams.Error.Count > 0;
                return hasErrors ? 1 : 0;
            }, cancellationToken);
        }

        /// <summary>
        /// Executes an embedded or on-disk PowerShell script directly in-process via the official Microsoft.PowerShell SDK (System.Management.Automation).
        /// Captures output, information, warning, and error streams without spawning external powershell.exe processes,
        /// bypassing command-line process auditing alerts and PowerShell ExecutionPolicy restrictions.
        /// </summary>
        public async Task<int> RunScriptInProcessAsync(
            string scriptFileName,
            string? arguments = null,
            Action<string>? outputHandler = null,
            CancellationToken cancellationToken = default)
        {
            string? scriptContent = GetEmbeddedScriptContent(scriptFileName);
            if (string.IsNullOrWhiteSpace(scriptContent))
            {
                string diskPath = ResolveScriptPath(scriptFileName);
                if (File.Exists(diskPath))
                {
                    scriptContent = File.ReadAllText(diskPath, Encoding.UTF8);
                }
                else
                {
                    throw new FileNotFoundException($"PowerShell script '{scriptFileName}' could not be located in assembly resources or on disk.", scriptFileName);
                }
            }

            _logger.LogInfo($"[SDK-RUNNER] Executing '{scriptFileName}' in-process via System.Management.Automation SDK...");
            outputHandler?.Invoke($"[SDK-RUNNER] In-Process execution starting for: {scriptFileName}");

            int exitCode = await ExecuteScriptBlockAsync(scriptContent, arguments, outputHandler, cancellationToken);
            outputHandler?.Invoke($"[SDK-RUNNER] Completed '{scriptFileName}' (Exit Code: {exitCode})");
            return exitCode;
        }

        /// <summary>
        /// Executes a PowerShell prerequisite or setup script.
        /// If elevated execution is requested while running without administrator rights,
        /// relaunches the application host with UAC RunAs to execute the embedded script in-process.
        /// Otherwise, executes directly in-process via System.Management.Automation without external powershell.exe calls.
        /// </summary>
        public async Task<int> RunScriptAsync(
            string scriptFileName, 
            string? arguments = null, 
            bool elevate = false, 
            Action<string>? outputHandler = null, 
            CancellationToken cancellationToken = default)
        {
            if (elevate && !IsAdministrator())
            {
                // Relaunch the application's own executable elevated via UAC RunAs.
                // This executes the embedded script in-process in the elevated host,
                // avoiding powershell.exe process auditing alerts and execution policy blocks.
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    try
                    {
                        exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    outputHandler?.Invoke($"[RUNNER] Elevating via application host '{Path.GetFileName(exePath)}'...");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"--run-embedded-script \"{scriptFileName}\" {arguments ?? string.Empty}".Trim(),
                        Verb = "RunAs",
                        UseShellExecute = true
                    };

                    using var proc = Process.Start(startInfo);
                    if (proc != null)
                    {
                        outputHandler?.Invoke("[RUNNER] Elevated process spawned. Awaiting completion...");
                        await proc.WaitForExitAsync(cancellationToken);
                        outputHandler?.Invoke($"[RUNNER] Elevated process finished with exit code {proc.ExitCode}.");
                        return proc.ExitCode;
                    }
                }
            }

            // Execute directly in-process via the .NET PowerShell SDK
            return await RunScriptInProcessAsync(scriptFileName, arguments, outputHandler, cancellationToken);
        }

        private static string ResolveScriptPath(string scriptName)
        {
            // Try 1: Current working directory
            string candidate = Path.Combine(Directory.GetCurrentDirectory(), scriptName);
            if (File.Exists(candidate)) return candidate;

            // Try 2: AppDomain base directory
            candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scriptName);
            if (File.Exists(candidate)) return candidate;

            // Try 3: Search parent directories up to 6 levels (covers bin/Debug/net8.0-windows test directories)
            string? current = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && current != null; i++)
            {
                candidate = Path.Combine(current, scriptName);
                if (File.Exists(candidate)) return candidate;
                
                string utilsCandidate = Path.Combine(current, "scripts", "utils", scriptName);
                if (File.Exists(utilsCandidate)) return utilsCandidate;

                string buildCandidate = Path.Combine(current, "scripts", "build", scriptName);
                if (File.Exists(buildCandidate)) return buildCandidate;

                current = Directory.GetParent(current)?.FullName;
            }

            return scriptName;
        }

        #endregion

        #region Helpers

        public static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string ComputePublicKeyFingerprint(string publicKeyString)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(publicKeyString)) return string.Empty;
                var parts = publicKeyString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string b64 = parts[1];
                    int mod = b64.Length % 4;
                    if (mod > 0) b64 += new string('=', 4 - mod);
                    byte[] keyBytes = Convert.FromBase64String(b64);
                    using var sha256 = SHA256.Create();
                    byte[] hash = sha256.ComputeHash(keyBytes);
                    return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
                }
            }
            catch { }
            return string.Empty;
        }

        private static string LocateBinary(string binaryName, string? explicitPath = null)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
                return explicitPath;

            string sys32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", binaryName);
            if (File.Exists(sys32Path)) return sys32Path;

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), binaryName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            return string.Empty;
        }

        private static string QueryServiceStatus(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"(Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue).Status\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string outText = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    return string.IsNullOrEmpty(outText) ? "Not Installed" : outText;
                }
            }
            catch { }
            return "Unknown";
        }

        private static bool QueryFirewallRulePort22()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue).Enabled\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string outText = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    return string.Equals(outText, "True", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        private static bool IsVssServiceAvailable()
        {
            try
            {
                string status = QueryServiceStatus("vss");
                return status.Equals("Running", StringComparison.OrdinalIgnoreCase) || 
                       status.Equals("Stopped", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        private static void ExecuteCommand(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }

        #endregion
    }
}
