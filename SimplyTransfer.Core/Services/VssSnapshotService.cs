using System;
using System.IO;
using System.Security.Principal;
using Alphaleonis.Win32.Vss;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Interacts with the Windows Volume Shadow Copy Service (VSS) using AlphaVSS.
    /// Creates point-in-time, read-only shadow snapshots to safely read exclusively locked files
    /// (such as live QuickBooks .qbw and .tlg databases) without halting client operations.
    /// </summary>
    public class VssSnapshotService : IDisposable
    {
        private IVssBackupComponents? _backupComponents;
        private Guid _snapshotSetId = Guid.Empty;
        private Guid _snapshotId = Guid.Empty;
        private string? _snapshotDeviceObject;
        private string? _snapshotVolume;

        /// <summary>
        /// Checks whether the current process is running with elevated Administrator privileges.
        /// </summary>
        /// <returns>True if running as Administrator; otherwise false.</returns>
        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates a point-in-time VSS snapshot for the specified volume.
        /// Requires Administrator elevation.
        /// </summary>
        /// <param name="volumeName">The volume root (e.g. "C:\" or "D:\").</param>
        /// <returns>The VSS snapshot device object string (e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1).</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown if process lacks Administrator privileges.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the volume does not support VSS.</exception>
        /// <exception cref="Exception">Thrown if snapshot creation fails.</exception>
        public string CreateSnapshot(string volumeName)
        {
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException("VSS Volume Shadow Copy creation requires Administrator privileges. Please run Simply Transfer as Administrator.");
            }

            try
            {
                // Format volume name with trailing slash (e.g., "C:\")
                if (!volumeName.EndsWith("\\"))
                {
                    volumeName += "\\";
                }
                _snapshotVolume = volumeName;

                IVssFactory vssFactory = VssFactoryProvider.Default.GetVssFactory();
                _backupComponents = vssFactory.CreateVssBackupComponents();
                _backupComponents.InitializeForBackup(null);
                _backupComponents.SetBackupState(false, true, VssBackupType.Full, false);

                // Set context for backup snapshot
                _backupComponents.SetContext(VssSnapshotContext.Backup);

                _snapshotSetId = _backupComponents.StartSnapshotSet();

                if (!_backupComponents.IsVolumeSupported(volumeName, Guid.Empty))
                {
                    throw new InvalidOperationException($"Volume '{volumeName}' does not support Volume Shadow Copy (VSS).");
                }

                _snapshotId = _backupComponents.AddToSnapshotSet(volumeName);

                _backupComponents.PrepareForBackup();
                _backupComponents.DoSnapshotSet();

                var snapshotProperties = _backupComponents.GetSnapshotProperties(_snapshotId);
                _snapshotDeviceObject = snapshotProperties.SnapshotDeviceObject;

                return _snapshotDeviceObject;
            }
            catch (Exception ex)
            {
                Dispose();
                throw new Exception($"Failed to create VSS snapshot for volume '{volumeName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Translates a regular Windows file path into its corresponding path within the mounted VSS snapshot device.
        /// </summary>
        /// <param name="originalFilePath">The original local file path (e.g. "C:\Folder\file.qbw").</param>
        /// <returns>The fully qualified shadow copy device path.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no snapshot is active.</exception>
        public string GetSnapshotFilePath(string originalFilePath)
        {
            if (string.IsNullOrEmpty(_snapshotDeviceObject))
                throw new InvalidOperationException("No active VSS snapshot has been created.");

            string pathRoot = Path.GetPathRoot(originalFilePath) ?? string.Empty;
            string relativePath = originalFilePath.Substring(pathRoot.Length);

            // Strip leading slashes to prevent root-escape
            relativePath = relativePath.TrimStart('\\', '/');

            // Construct global root path: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyX\path\to\file
            string combined = Path.Combine(_snapshotDeviceObject, relativePath);
            return combined;
        }

        /// <summary>
        /// Opens a readable file stream to the file located inside the point-in-time VSS shadow snapshot.
        /// </summary>
        /// <param name="originalFilePath">The original file path on the host volume.</param>
        /// <returns>A readable <see cref="FileStream"/> referencing the shadow snapshot copy.</returns>
        public Stream OpenSnapshotFile(string originalFilePath)
        {
            string snapshotPath = GetSnapshotFilePath(originalFilePath);
            
            // Open read-only with ReadWrite share to safely copy live QuickBooks .qbw and .tlg files
            return new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
        }

        /// <summary>
        /// Deletes the VSS snapshot set and releases all underlying Windows VSS COM resources.
        /// </summary>
        public void Dispose()
        {
            if (_backupComponents != null)
            {
                try
                {
                    if (_snapshotSetId != Guid.Empty)
                    {
                        _backupComponents.BackupComplete();
                        _backupComponents.DeleteSnapshotSet(_snapshotSetId, false);
                        _snapshotSetId = Guid.Empty;
                    }
                }
                catch
                {
                    // Suppress cleanup errors to avoid crashing during shutdown
                }
                finally
                {
                    _backupComponents.Dispose();
                    _backupComponents = null;
                    _snapshotDeviceObject = null;
                }
            }
            GC.SuppressFinalize(this);
        }
    }
}
