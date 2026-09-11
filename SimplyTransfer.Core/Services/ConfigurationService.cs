using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimplyTransfer.Core.Models;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Manages application configuration profiles, providing persistence to local AppData JSON storage,
    /// as well as portable import and export functionality.
    /// </summary>
    public class ConfigurationService
    {
        private readonly string _configFilePath;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationService"/> class.
        /// Ensures the application data directory exists and configures JSON serialization options.
        /// </summary>
        public ConfigurationService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appData, "SimplyTransfer");
            Directory.CreateDirectory(appFolder);
            _configFilePath = Path.Combine(appFolder, "profiles.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// Persists the list of synchronization profiles to the local configuration file.
        /// </summary>
        /// <param name="profiles">The list of profiles to save.</param>
        /// <exception cref="InvalidOperationException">Thrown when file writing or serialization fails.</exception>
        public void SaveProfiles(List<SyncProfile> profiles)
        {
            try
            {
                var json = JsonSerializer.Serialize(profiles, _jsonOptions);
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save profiles: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads synchronization profiles from the local configuration file.
        /// If the configuration file does not exist or is unreadable, default profiles are created.
        /// </summary>
        /// <returns>A list of loaded <see cref="SyncProfile"/> instances.</returns>
        public List<SyncProfile> LoadProfiles()
        {
            if (!File.Exists(_configFilePath))
            {
                var defaults = CreateDefaultProfiles();
                SaveProfiles(defaults);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(_configFilePath);
                var profiles = JsonSerializer.Deserialize<List<SyncProfile>>(json, _jsonOptions);
                return profiles ?? CreateDefaultProfiles();
            }
            catch
            {
                return CreateDefaultProfiles();
            }
        }

        /// <summary>
        /// Exports a synchronization profile to a portable JSON string and optionally writes it to disk.
        /// Excludes machine-specific DPAPI secret blobs by default unless explicitly specified.
        /// </summary>
        /// <param name="profile">The profile to export.</param>
        /// <param name="exportFilePath">Optional file path to write the JSON content.</param>
        /// <param name="includeEncryptedSecrets">True to include DPAPI encrypted secrets; false to strip them for portability.</param>
        /// <returns>The serialized JSON string representing the profile.</returns>
        public string ExportProfile(SyncProfile profile, string exportFilePath, bool includeEncryptedSecrets = false)
        {
            // Clone profile for export
            var exportItem = new SyncProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                PrivateKeyPath = profile.PrivateKeyPath,
                KeyType = profile.KeyType,
                UseVpn = profile.UseVpn,
                VpnType = profile.VpnType,
                VpnConnectionName = profile.VpnConnectionName,
                VpnConfigPath = profile.VpnConfigPath,
                PreFlightTimeoutSeconds = profile.PreFlightTimeoutSeconds,
                DisconnectVpnAfterSync = profile.DisconnectVpnAfterSync,
                UseVssForLockedFiles = profile.UseVssForLockedFiles,
                AutoDetectQuickBooks = profile.AutoDetectQuickBooks,
                SourcePaths = new List<string>(profile.SourcePaths),
                DestinationDirectory = profile.DestinationDirectory,
                // Include DPAPI blobs only if explicitly desired (note: DPAPI is machine/user specific)
                EncryptedPrivateKey = includeEncryptedSecrets ? profile.EncryptedPrivateKey : null,
                EncryptedPassphrase = includeEncryptedSecrets ? profile.EncryptedPassphrase : null
            };

            var json = JsonSerializer.Serialize(exportItem, _jsonOptions);
            if (!string.IsNullOrEmpty(exportFilePath))
            {
                File.WriteAllText(exportFilePath, json);
            }
            return json;
        }

        /// <summary>
        /// Imports a synchronization profile from a JSON file.
        /// Generates a fresh unique identifier if the imported profile lacks one.
        /// </summary>
        /// <param name="importFilePath">The path of the JSON file to import.</param>
        /// <returns>The deserialized <see cref="SyncProfile"/> instance.</returns>
        /// <exception cref="FileNotFoundException">Thrown when the specified file path does not exist.</exception>
        public SyncProfile? ImportProfile(string importFilePath)
        {
            if (!File.Exists(importFilePath))
                throw new FileNotFoundException("Import file not found", importFilePath);

            var json = File.ReadAllText(importFilePath);
            var profile = JsonSerializer.Deserialize<SyncProfile>(json, _jsonOptions);
            if (profile != null && profile.Id == Guid.Empty)
            {
                profile.Id = Guid.NewGuid();
            }
            return profile;
        }

        /// <summary>
        /// Creates a clean default synchronization profile template for fresh installations.
        /// </summary>
        /// <returns>A list containing a clean template <see cref="SyncProfile"/>.</returns>
        private List<SyncProfile> CreateDefaultProfiles()
        {
            return new List<SyncProfile>
            {
                new SyncProfile
                {
                    Name = "Default Profile",
                    Host = string.Empty,
                    Port = 22,
                    Username = string.Empty,
                    PrivateKeyPath = string.Empty,
                    KeyType = "Auto",
                    UseVpn = false,
                    VpnType = "WireGuard",
                    VpnConnectionName = string.Empty,
                    VpnConfigPath = string.Empty,
                    PreFlightTimeoutSeconds = 20,
                    DisconnectVpnAfterSync = false,
                    UseVssForLockedFiles = true,
                    AutoDetectQuickBooks = true,
                    SourcePaths = new List<string>(),
                    DestinationDirectory = "/backups/"
                }
            };
        }
    }
}
