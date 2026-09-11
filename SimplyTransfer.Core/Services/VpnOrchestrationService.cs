using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Orchestrates VPN connections across multiple protocols (WireGuard Windows Service / wg-quick and Windows RAS),
    /// providing pre-flight TCP socket connectivity verification before transfers proceed.
    /// </summary>
    public class VpnOrchestrationService
    {
        /// <summary>
        /// Asynchronously initiates a VPN connection for the specified VPN technology.
        /// </summary>
        /// <param name="vpnType">The VPN provider type ("WireGuard" or "RAS").</param>
        /// <param name="connectionName">The tunnel or connection name.</param>
        /// <param name="configPath">Optional path to the VPN configuration file (e.g. WireGuard .conf).</param>
        /// <returns>True if the connection was established successfully; otherwise false.</returns>
        /// <exception cref="NotSupportedException">Thrown if an unsupported VPN type is supplied.</exception>
        public async Task<bool> ConnectVpnAsync(string vpnType, string connectionName, string? configPath = null)
        {
            if (string.Equals(vpnType, "WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                return await ConnectWireGuardAsync(connectionName, configPath);
            }
            else if (string.Equals(vpnType, "RAS", StringComparison.OrdinalIgnoreCase))
            {
                return await ConnectRasAsync(connectionName);
            }

            throw new NotSupportedException($"VPN Type '{vpnType}' is not supported.");
        }

        /// <summary>
        /// Asynchronously disconnects an active VPN connection.
        /// </summary>
        /// <param name="vpnType">The VPN provider type ("WireGuard" or "RAS").</param>
        /// <param name="connectionName">The tunnel or connection name.</param>
        /// <returns>True if disconnection succeeded or was already disconnected; otherwise false.</returns>
        public async Task<bool> DisconnectVpnAsync(string vpnType, string connectionName)
        {
            if (string.Equals(vpnType, "WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                return await DisconnectWireGuardAsync(connectionName);
            }
            else if (string.Equals(vpnType, "RAS", StringComparison.OrdinalIgnoreCase))
            {
                return await DisconnectRasAsync(connectionName);
            }

            return true;
        }

        /// <summary>
        /// Checks whether the specified VPN tunnel is currently active and connected.
        /// </summary>
        /// <param name="vpnType">The VPN provider type ("WireGuard" or "RAS").</param>
        /// <param name="connectionName">The tunnel or connection name.</param>
        /// <returns>True if the tunnel is running; otherwise false.</returns>
        public async Task<bool> IsVpnConnectedAsync(string vpnType, string connectionName)
        {
            if (string.Equals(vpnType, "WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                return IsWireGuardConnected(connectionName);
            }
            else if (string.Equals(vpnType, "RAS", StringComparison.OrdinalIgnoreCase))
            {
                return await IsRasConnectedAsync(connectionName);
            }

            return false;
        }

        /// <summary>
        /// Performs a pre-flight TCP reachability probe to the target SFTP host and port via the VPN tunnel,
        /// retrying until connected or the timeout expires.
        /// </summary>
        /// <param name="host">Target hostname or IP address.</param>
        /// <param name="port">Target TCP port.</param>
        /// <param name="timeoutSeconds">Maximum wait time in seconds before failing the pre-flight check.</param>
        /// <param name="cancellationToken">Cancellation token to abort probing.</param>
        /// <returns>True if TCP connection was successfully established within the timeout; otherwise false.</returns>
        public async Task<bool> PreFlightReachabilityProbeAsync(string host, int port, int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(host, port);
                    var delayTask = Task.Delay(2000, cancellationToken);

                    var completedTask = await Task.WhenAny(connectTask, delayTask);
                    if (completedTask == connectTask && client.Connected)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Continue polling until timeout
                }

                await Task.Delay(1000, cancellationToken);
            }

            return false;
        }

        #region WireGuard Implementation

        private async Task<bool> ConnectWireGuardAsync(string connectionName, string? configPath)
        {
            // If already running as a service
            if (IsWireGuardConnected(connectionName))
                return true;

            // Strategy 1: WireGuard Windows Service via wireguard.exe
            string wireguardExe = FindWireGuardExecutable();
            if (!string.IsNullOrEmpty(wireguardExe) && !string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                var success = await RunProcessAsync(wireguardExe, $"/installtunnelservice \"{configPath}\"");
                if (success)
                {
                    await Task.Delay(2000); // Allow service to start
                    return IsWireGuardConnected(connectionName);
                }
            }

            // Strategy 2: wg-quick fallback
            var wgQuickSuccess = await RunProcessAsync("wg-quick", $"up {connectionName}");
            if (wgQuickSuccess) return true;

            // Strategy 3: Check if service exists and start it
            try
            {
                string serviceName = $"WireGuardTunnel${connectionName}";
                using var sc = new ServiceController(serviceName);
                if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    return true;
                }
            }
            catch
            {
                // Service might not exist yet
            }

            return IsWireGuardConnected(connectionName);
        }

        private async Task<bool> DisconnectWireGuardAsync(string connectionName)
        {
            string wireguardExe = FindWireGuardExecutable();
            if (!string.IsNullOrEmpty(wireguardExe))
            {
                await RunProcessAsync(wireguardExe, $"/uninstalltunnelservice {connectionName}");
            }

            await RunProcessAsync("wg-quick", $"down {connectionName}");

            try
            {
                string serviceName = $"WireGuardTunnel${connectionName}";
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Ignore service control errors during disconnect
            }

            return !IsWireGuardConnected(connectionName);
        }

        private bool IsWireGuardConnected(string connectionName)
        {
            try
            {
                string serviceName = $"WireGuardTunnel${connectionName}";
                using var sc = new ServiceController(serviceName);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }

        private string FindWireGuardExecutable()
        {
            string[] possiblePaths = new[]
            {
                @"C:\Program Files\WireGuard\wireguard.exe",
                @"C:\Program Files (x86)\WireGuard\wireguard.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"WireGuard\wireguard.exe")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path)) return path;
            }

            return "wireguard.exe";
        }

        #endregion

        #region RAS (Remote Access Service) Implementation

        private async Task<bool> ConnectRasAsync(string connectionName)
        {
            // rasdial "VPN Name"
            var result = await RunProcessWithOutputAsync("rasdial", $"\"{connectionName}\"");
            return result.ExitCode == 0;
        }

        private async Task<bool> DisconnectRasAsync(string connectionName)
        {
            // rasdial "VPN Name" /disconnect
            var result = await RunProcessWithOutputAsync("rasdial", $"\"{connectionName}\" /disconnect");
            return result.ExitCode == 0;
        }

        private async Task<bool> IsRasConnectedAsync(string connectionName)
        {
            // Executing rasdial with no arguments lists all active connections
            var result = await RunProcessWithOutputAsync("rasdial", string.Empty);
            if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
            {
                return result.Output.Contains(connectionName, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        #endregion

        #region Process Helpers

        private async Task<bool> RunProcessAsync(string fileName, string arguments)
        {
            var result = await RunProcessWithOutputAsync(fileName, arguments);
            return result.ExitCode == 0;
        }

        private async Task<(int ExitCode, string Output, string Error)> RunProcessWithOutputAsync(string fileName, string arguments)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(processInfo);
                if (process == null) return (-1, string.Empty, "Failed to start process");

                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                string output = await stdOutTask;
                string error = await stdErrTask;

                return (process.ExitCode, output, error);
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }
        }

        #endregion
    }
}
