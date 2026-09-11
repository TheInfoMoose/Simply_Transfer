using System;
using System.Collections.Generic;

namespace SimplyTransfer.Core.Models
{
    /// <summary>
    /// Represents a complete synchronization profile, containing SSH/SFTP endpoint details,
    /// cryptographic keys, VPN orchestration parameters, and VSS settings.
    /// </summary>
    public class SyncProfile
    {
        /// <summary>
        /// Gets or sets the unique identifier for the profile.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Gets or sets the human-readable profile name.
        /// </summary>
        public string Name { get; set; } = "Default Profile";

        /// <summary>
        /// Gets or sets the target SFTP server hostname or IP address.
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the target SFTP server SSH port (default: 22).
        /// </summary>
        public int Port { get; set; } = 22;

        /// <summary>
        /// Gets or sets the SSH username on the remote host.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the local file path to the Ed25519 or RSA private key file.
        /// </summary>
        public string PrivateKeyPath { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the optional DPAPI-encrypted private key raw bytes.
        /// </summary>
        public byte[]? EncryptedPrivateKey { get; set; }

        /// <summary>
        /// Gets or sets the DPAPI-encrypted passphrase for decrypting the SSH private key.
        /// </summary>
        public byte[]? EncryptedPassphrase { get; set; }

        /// <summary>
        /// Gets or sets the key algorithm type (e.g., "Auto", "Ed25519", "RSA").
        /// </summary>
        public string KeyType { get; set; } = "Auto";

        /// <summary>
        /// Gets or sets a value indicating whether a pre-flight VPN connection must be established before syncing.
        /// </summary>
        public bool UseVpn { get; set; }

        /// <summary>
        /// Gets or sets the VPN technology used ("WireGuard" or "RAS").
        /// </summary>
        public string VpnType { get; set; } = "WireGuard";

        /// <summary>
        /// Gets or sets the VPN connection or tunnel name (e.g., "wg0", "OfficeVPN").
        /// </summary>
        public string VpnConnectionName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path to the WireGuard configuration file (.conf), if applicable.
        /// </summary>
        public string VpnConfigPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timeout in seconds for verifying SFTP socket reachability after VPN connection.
        /// </summary>
        public int PreFlightTimeoutSeconds { get; set; } = 20;

        /// <summary>
        /// Gets or sets a value indicating whether the VPN should be automatically disconnected after sync finishes.
        /// </summary>
        public bool DisconnectVpnAfterSync { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether Volume Shadow Copy (VSS) snapshots should be used for locked files.
        /// </summary>
        public bool UseVssForLockedFiles { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether QuickBooks files (.qbw, .tlg, etc.) should be automatically detected.
        /// </summary>
        public bool AutoDetectQuickBooks { get; set; } = true;

        /// <summary>
        /// Gets or sets the list of configured local source paths (files or directories) to synchronize.
        /// </summary>
        public List<string> SourcePaths { get; set; } = new();

        /// <summary>
        /// Gets or sets the remote destination directory on the SFTP server.
        /// </summary>
        public string DestinationDirectory { get; set; } = "/backups/";
    }
}
