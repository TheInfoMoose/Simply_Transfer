using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Handles secure SFTP and SSH connectivity using pure public-key authentication.
    /// Provides file stream upload routines and remote cryptographic verification via SSH commands.
    /// </summary>
    public class SftpTransferService : IDisposable
    {
        private readonly ConnectionInfo _connectionInfo;
        private readonly HashValidationService _hashService;
        private SftpClient? _sftpClient;
        private SshClient? _sshClient;
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpTransferService"/> class.
        /// Configures SSH key authentication (Ed25519 or RSA) with optional passphrase. Password authentication is disallowed.
        /// </summary>
        /// <param name="host">Target SFTP hostname or IP address.</param>
        /// <param name="port">SSH port (typically 22).</param>
        /// <param name="username">SSH username.</param>
        /// <param name="privateKeyPath">Path to private key file, or null if using byte array.</param>
        /// <param name="privateKeyBytes">Optional raw private key bytes.</param>
        /// <param name="passphrase">Optional passphrase to decrypt the private key.</param>
        /// <exception cref="ArgumentException">Thrown when required arguments are missing or invalid.</exception>
        /// <summary>
        /// Expands environment variables and user home shortcuts ('~') to resolve a valid local path.
        /// </summary>
        public static string ResolvePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string trimmed = path.Trim();
            if (trimmed.StartsWith("~"))
            {
                string relativePart = trimmed.TrimStart('~', '/', '\\');
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relativePart);
            }
            return Environment.ExpandEnvironmentVariables(trimmed);
        }

        /// <summary>
        /// Normalizes a remote path for Windows command execution, stripping leading forward-slashes
        /// before drive letters and converting separators to backslashes.
        /// </summary>
        public static string NormalizePathForWindows(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string normalized = path.Trim();
            var driveMatch = Regex.Match(normalized, @"^[/\\]*([a-zA-Z]:.*)$");
            if (driveMatch.Success)
            {
                normalized = driveMatch.Groups[1].Value;
            }
            return normalized.Replace('/', '\\');
        }

        /// <summary>
        /// Normalizes a remote path for Unix/Linux/macOS command execution using forward slashes.
        /// </summary>
        public static string NormalizePathForUnix(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return path.Trim().Replace('\\', '/');
        }

        /// <summary>
        /// Determines whether a remote path represents a Windows-style path (e.g. drive letter or backslashes).
        /// </summary>
        public static bool IsWindowsRemotePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.Trim();
            return Regex.IsMatch(trimmed, @"^[/\\]*[a-zA-Z]:") || trimmed.Contains('\\');
        }

        public SftpTransferService(
            string host, 
            int port, 
            string username, 
            string? privateKeyPath, 
            byte[]? privateKeyBytes = null, 
            string? passphrase = null,
            HashValidationService? hashService = null)
        {
            _hashService = hashService ?? new HashValidationService();
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("SFTP Host is required.", nameof(host));
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("SFTP Username is required.", nameof(username));

            PrivateKeyFile keyFile;

            string resolvedKeyPath = ResolvePath(privateKeyPath);

            if (privateKeyBytes != null && privateKeyBytes.Length > 0)
            {
                using var ms = new MemoryStream(privateKeyBytes);
                keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(ms)
                    : new PrivateKeyFile(ms, passphrase);
            }
            else if (!string.IsNullOrEmpty(resolvedKeyPath))
            {
                if (resolvedKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The specified key file is a Public Key (.pub). Simply Transfer requires the matching Private Key file (e.g., id_ed25519 without .pub) on this client machine. Public keys belong on the destination server.");
                }

                if (!File.Exists(resolvedKeyPath))
                {
                    throw new FileNotFoundException($"SSH Private Key file not found: '{resolvedKeyPath}'", resolvedKeyPath);
                }

                keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(resolvedKeyPath)
                    : new PrivateKeyFile(resolvedKeyPath, passphrase);
            }
            else
            {
                throw new ArgumentException("A valid Ed25519 or RSA private key (file path or key content) is required. Password authentication is disabled by security policy.");
            }

            var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);

            _connectionInfo = new ConnectionInfo(host, port, username, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(20),
                RetryAttempts = 3
            };
        }

        /// <summary>
        /// Asynchronously connects both SFTP and SSH clients to the remote host.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to cancel connection.</param>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Run(() =>
            {
                _sftpClient = new SftpClient(_connectionInfo);
                _sshClient = new SshClient(_connectionInfo);

                _sftpClient.Connect();
                _sshClient.Connect();
            }, cancellationToken);
        }

        /// <summary>
        /// Recursively creates the remote directory hierarchy on the SFTP server if it does not already exist.
        /// </summary>
        /// <param name="remoteDirectoryPath">The target remote directory path (Unix forward-slash format).</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if the SFTP client is not connected.</exception>
        public async Task EnsureRemoteDirectoryExistsAsync(string remoteDirectoryPath, CancellationToken cancellationToken = default)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected)
                throw new InvalidOperationException("SFTP client is not connected.");

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = remoteDirectoryPath.Replace('\\', '/').TrimEnd('/');
                
                // Ensure Windows absolute paths start with '/' for SFTP compatibility
                if (Regex.IsMatch(path, @"^[a-zA-Z]:"))
                {
                    path = "/" + path;
                }

                string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                string current = path.StartsWith('/') ? "/" : string.Empty;

                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (string.IsNullOrEmpty(current))
                    {
                        current = segment;
                    }
                    else
                    {
                        current = current == "/" ? "/" + segment : $"{current}/{segment}";
                    }
                    
                    if (!_sftpClient.Exists(current))
                    {
                        try
                        {
                            _sftpClient.CreateDirectory(current);
                        }
                        catch (SftpPathNotFoundException)
                        {
                            // Try parent first
                        }
                        catch (SshException)
                        {
                            // Directory may have been created concurrently or permission warning
                        }
                    }
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Uploads an open readable data stream to the specified remote SFTP destination path,
        /// providing real-time upload progress and throughput metrics.
        /// </summary>
        /// <param name="sourceStream">The readable data stream to upload.</param>
        /// <param name="remoteFilePath">The destination path on the remote SFTP host.</param>
        /// <param name="progressCallback">Optional callback reporting (bytesUploaded, throughputBytesPerSec).</param>
        /// <param name="cancellationToken">Cancellation token to abort the upload.</param>
        /// <exception cref="InvalidOperationException">Thrown if the SFTP client is not connected.</exception>
        public async Task UploadStreamAsync(
            Stream sourceStream, 
            string remoteFilePath, 
            Action<ulong, double>? progressCallback = null, 
            CancellationToken cancellationToken = default)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected)
                throw new InvalidOperationException("SFTP client is not connected.");

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Format remote path with Unix forward slashes
                remoteFilePath = remoteFilePath.Replace('\\', '/');
                
                // Ensure Windows absolute paths start with '/' for SFTP compatibility
                if (Regex.IsMatch(remoteFilePath, @"^[a-zA-Z]:"))
                {
                    remoteFilePath = "/" + remoteFilePath;
                }
                
                string? dir = Path.GetDirectoryName(remoteFilePath)?.Replace('\\', '/');
                string fileName = Path.GetFileName(remoteFilePath);
                
                if (!string.IsNullOrEmpty(dir))
                {
                    if (!_sftpClient.Exists(dir))
                    {
                        EnsureRemoteDirectoryExistsAsync(dir, cancellationToken).GetAwaiter().GetResult();
                    }
                    _sftpClient.ChangeDirectory(dir);
                }

                var stopwatch = Stopwatch.StartNew();

                Console.WriteLine($"[TRACE] UploadFile called with remoteFilePath: '{remoteFilePath}'");

                _sftpClient.UploadFile(sourceStream, fileName, bytesUploaded =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double speedBps = stopwatch.Elapsed.TotalSeconds > 0 
                        ? bytesUploaded / stopwatch.Elapsed.TotalSeconds 
                        : 0;
                    progressCallback?.Invoke(bytesUploaded, speedBps);
                });
            }, cancellationToken);
        }

        /// <summary>
        /// Reads a local file from disk and uploads it to the remote SFTP destination path.
        /// </summary>
        /// <param name="localFilePath">The local file path on disk.</param>
        /// <param name="remoteFilePath">The remote file path.</param>
        /// <param name="progressCallback">Optional progress callback.</param>
        /// <param name="cancellationToken">Cancellation token to abort the upload.</param>
        public async Task UploadFileAsync(
            string localFilePath, 
            string remoteFilePath, 
            Action<ulong, double>? progressCallback = null, 
            CancellationToken cancellationToken = default)
        {
            using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
            await UploadStreamAsync(fileStream, remoteFilePath, progressCallback, cancellationToken);
        }

        /// <summary>
        /// Executes remote hashing routines to calculate the SHA-256 hash of the uploaded file on the destination host.
        /// Supports Windows OpenSSH (certutil, PowerShell), Linux/Unix (sha256sum, shasum, openssl),
        /// and provides a fail-safe fallback to SFTP stream reading if remote command execution is restricted or unavailable.
        /// </summary>
        /// <param name="remoteFilePath">The remote file path on the SFTP host.</param>
        /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
        /// <returns>A lowercase hexadecimal SHA-256 string (64 characters).</returns>
        /// <exception cref="InvalidOperationException">Thrown if neither SSH nor SFTP client is connected.</exception>
        /// <exception cref="FormatException">Thrown if a 64-character SHA-256 hash cannot be obtained.</exception>
        public async Task<string> GetRemoteFileSha256Async(string remoteFilePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Strategy 1: Attempt remote shell execution via SSH client
            if (_sshClient != null && _sshClient.IsConnected)
            {
                string? remoteHash = await Task.Run(() =>
                {
                    return TryExecuteRemoteShaCommand(remoteFilePath, cancellationToken);
                }, cancellationToken);

                if (!string.IsNullOrEmpty(remoteHash))
                {
                    return remoteHash;
                }
            }

            // Strategy 2: Fallback to reading the uploaded file stream over SFTP
            if (_sftpClient != null && _sftpClient.IsConnected)
            {
                return await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sftpPath = remoteFilePath.Replace('\\', '/');
                    
                    if (Regex.IsMatch(sftpPath, @"^[a-zA-Z]:"))
                    {
                        sftpPath = "/" + sftpPath;
                    }
                    
                    string? dir = Path.GetDirectoryName(sftpPath)?.Replace('\\', '/');
                    string fileName = Path.GetFileName(sftpPath);
                    
                    if (!string.IsNullOrEmpty(dir))
                    {
                        _sftpClient.ChangeDirectory(dir);
                    }
                    
                    using var stream = _sftpClient.OpenRead(fileName);
                    return await _hashService.ComputeStreamHashAsync(stream, cancellationToken: cancellationToken);
                }, cancellationToken);
            }

            throw new InvalidOperationException("Neither SSH nor SFTP client is connected to calculate remote file hash.");
        }

        /// <summary>
        /// Retrieves the exact file size in bytes of a remote file on the SFTP host for byte parity verification.
        /// </summary>
        /// <param name="remoteFilePath">The remote file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Remote file size in bytes, or -1 if the file does not exist.</returns>
        public async Task<long> GetRemoteFileSizeAsync(string remoteFilePath, CancellationToken cancellationToken = default)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected)
                throw new InvalidOperationException("SFTP client is not connected.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sftpPath = remoteFilePath.Replace('\\', '/');
                if (Regex.IsMatch(sftpPath, @"^[a-zA-Z]:"))
                {
                    sftpPath = "/" + sftpPath;
                }

                string? dir = Path.GetDirectoryName(sftpPath)?.Replace('\\', '/');
                string fileName = Path.GetFileName(sftpPath);

                if (!string.IsNullOrEmpty(dir))
                {
                    _sftpClient.ChangeDirectory(dir);
                }

                if (_sftpClient.Exists(fileName))
                {
                    var attrs = _sftpClient.GetAttributes(fileName);
                    return attrs.Size;
                }

                return -1L;
            }, cancellationToken);
        }

        /// <summary>
        /// Attempts to execute platform-appropriate SHA-256 calculation commands over the SSH client.
        /// </summary>
        private string? TryExecuteRemoteShaCommand(string remoteFilePath, CancellationToken cancellationToken)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return null;

            bool isWindowsTarget = IsWindowsRemotePath(remoteFilePath) || 
                                   (_sshClient.ConnectionInfo.ServerVersion?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true);

            string winPath = NormalizePathForWindows(remoteFilePath);
            string unixPath = NormalizePathForUnix(remoteFilePath);

            string psLiteral = winPath.Replace("'", "''");
            string bashLiteral = unixPath.Replace("'", "'\\''");

            var commands = new List<string>();

            if (isWindowsTarget)
            {
                // 1. Windows certutil (built into System32 on all modern Windows installations)
                commands.Add($"certutil -hashfile \"{winPath}\" SHA256");
                // 2. Windows PowerShell (built into Windows 7+)
                commands.Add($"powershell -NoProfile -NonInteractive -Command \"(Get-FileHash -LiteralPath '{psLiteral}' -Algorithm SHA256).Hash\"");
                // 3. Fallbacks for Windows hosts with Git Bash / MSYS / Cygwin in PATH
                commands.Add($"sha256sum \"{winPath}\"");
                commands.Add($"sha256sum '{bashLiteral}'");
                commands.Add($"shasum -a 256 \"{winPath}\"");
                commands.Add($"openssl dgst -sha256 \"{winPath}\"");
            }
            else
            {
                // Unix / Linux / macOS target
                // 1. sha256sum (coreutils standard on Linux)
                commands.Add($"sha256sum '{bashLiteral}'");
                // 2. shasum -a 256 (standard on macOS / BSD)
                commands.Add($"shasum -a 256 '{bashLiteral}'");
                // 3. openssl dgst -sha256 (ubiquitous on Unix/BSD/macOS)
                commands.Add($"openssl dgst -sha256 '{bashLiteral}'");
                // 4. Windows fallbacks in case remote target was misidentified
                commands.Add($"certutil -hashfile \"{winPath}\" SHA256");
                commands.Add($"powershell -NoProfile -NonInteractive -Command \"(Get-FileHash -LiteralPath '{psLiteral}' -Algorithm SHA256).Hash\"");
            }

            foreach (var cmdText in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var cmd = _sshClient.CreateCommand(cmdText);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                    var output = cmd.Execute();

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        string? hash = HashValidationService.TryParseSha256(output);
                        if (hash != null)
                        {
                            return hash;
                        }
                    }
                }
                catch
                {
                    // Proceed to next command candidate
                }
            }

            return null;
        }

        /// <summary>
        /// Tests connectivity and authentication, verifying remote destination directory accessibility.
        /// </summary>
        /// <param name="destinationDirectory">Optional destination directory to verify or create.</param>
        /// <param name="cancellationToken">Cancellation token to abort the test.</param>
        /// <returns>Diagnostic connection information string.</returns>
        public async Task<string> TestConnectionAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken);

            string info = $"SFTP connected (SSH Server: {_sshClient?.ConnectionInfo.ServerVersion ?? "Unknown"})";
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                await EnsureRemoteDirectoryExistsAsync(destinationDirectory, cancellationToken);
                info += $", Destination directory '{destinationDirectory}' verified.";
            }

            return info;
        }

        /// <summary>
        /// Gracefully disconnects the SFTP and SSH clients if connected.
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_sftpClient?.IsConnected == true) _sftpClient.Disconnect();
            }
            catch
            {
                // Ignore disconnect exceptions
            }

            try
            {
                if (_sshClient?.IsConnected == true) _sshClient.Disconnect();
            }
            catch
            {
                // Ignore disconnect exceptions
            }
        }

        /// <summary>
        /// Disposes client resources and closes all network connections safely.
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                Disconnect();
                try { _sftpClient?.Dispose(); } catch { }
                try { _sshClient?.Dispose(); } catch { }
                _sftpClient = null;
                _sshClient = null;
                _isDisposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
