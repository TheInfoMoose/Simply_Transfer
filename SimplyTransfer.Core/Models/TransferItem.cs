using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimplyTransfer.Core.Models
{
    /// <summary>
    /// Represents the operational state of an individual file transfer item.
    /// </summary>
    public enum TransferStatus
    {
        /// <summary>File is queued and awaiting transfer.</summary>
        Pending,
        /// <summary>VSS Volume Shadow Copy snapshot is being created for this file.</summary>
        Snapshotting,
        /// <summary>File stream is actively uploading to remote SFTP destination.</summary>
        Transferring,
        /// <summary>Computing and comparing local and remote SHA-256 hashes.</summary>
        Validating,
        /// <summary>Transfer and cryptographic verification completed successfully.</summary>
        Completed,
        /// <summary>Transfer or integrity validation failed.</summary>
        Failed,
        /// <summary>Transfer was skipped.</summary>
        Skipped
    }

    /// <summary>
    /// Represents the cryptographic hash verification state of a transferred file.
    /// </summary>
    public enum HashMatchStatus
    {
        /// <summary>Hash verification has not yet run.</summary>
        Pending,
        /// <summary>Hash calculation or remote sha256sum execution is currently in progress.</summary>
        Validating,
        /// <summary>Local and remote SHA-256 hashes match exactly.</summary>
        Verified,
        /// <summary>Local and remote SHA-256 hashes do not match (integrity failure).</summary>
        Mismatch
    }

    /// <summary>
    /// Represents an individual file undergoing transfer, tracking its sizing, VSS shadow path,
    /// upload speed, progress, and cryptographic verification status.
    /// </summary>
    public class TransferItem : INotifyPropertyChanged
    {
        private string _fileName = string.Empty;
        private string _localFilePath = string.Empty;
        private string _remoteFilePath = string.Empty;
        private long _fileSizeBytes;
        private long _remoteFileSizeBytes;
        private bool _isQuickBooksFile;
        private bool _isVssRequired;
        private string? _vssResolvedPath;
        private TransferStatus _status = TransferStatus.Pending;
        private double _progressPercentage;
        private long _bytesTransferred;
        private double _transferSpeedBps;
        private string _localSha256 = string.Empty;
        private string _remoteSha256 = string.Empty;
        private HashMatchStatus _hashStatus = HashMatchStatus.Pending;
        private string _statusMessage = "Queued";
        private string? _errorMessage;
        private DateTime? _completedTime;

        /// <summary>Occurs when a property value changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Gets or sets the display file name.</summary>
        public string FileName
        {
            get => _fileName;
            set => SetField(ref _fileName, value);
        }

        /// <summary>Gets or sets the full local file path on disk.</summary>
        public string LocalFilePath
        {
            get => _localFilePath;
            set => SetField(ref _localFilePath, value);
        }

        /// <summary>Gets or sets the target relative or absolute path on the remote SFTP host.</summary>
        public string RemoteFilePath
        {
            get => _remoteFilePath;
            set => SetField(ref _remoteFilePath, value);
        }

        /// <summary>Gets or sets the file size in bytes.</summary>
        public long FileSizeBytes
        {
            get => _fileSizeBytes;
            set
            {
                if (SetField(ref _fileSizeBytes, value))
                {
                    OnPropertyChanged(nameof(FormattedSize));
                    OnPropertyChanged(nameof(IsSizeMatch));
                    OnPropertyChanged(nameof(SizeParityText));
                }
            }
        }

        /// <summary>Gets or sets the destination file size in bytes as verified on the remote host.</summary>
        public long RemoteFileSizeBytes
        {
            get => _remoteFileSizeBytes;
            set
            {
                if (SetField(ref _remoteFileSizeBytes, value))
                {
                    OnPropertyChanged(nameof(FormattedRemoteSize));
                    OnPropertyChanged(nameof(IsSizeMatch));
                    OnPropertyChanged(nameof(SizeParityText));
                }
            }
        }

        /// <summary>Gets the formatted human-readable remote file size.</summary>
        public string FormattedRemoteSize => _remoteFileSizeBytes <= 0 ? "Pending" : FormatSize(_remoteFileSizeBytes);

        /// <summary>Gets whether the local source size matches the remote destination size exactly.</summary>
        public bool IsSizeMatch => _fileSizeBytes > 0 && _fileSizeBytes == _remoteFileSizeBytes;

        /// <summary>Gets a display status string confirming byte-level parity.</summary>
        public string SizeParityText
        {
            get
            {
                if (_remoteFileSizeBytes <= 0) return "Awaiting Parity Check";
                if (_fileSizeBytes == _remoteFileSizeBytes) return "✓ 100% Parity (Byte Match)";
                return $"✗ Size Mismatch ({_fileSizeBytes} B vs {_remoteFileSizeBytes} B)";
            }
        }
        
        /// <summary>Gets or sets whether the file is identified as a QuickBooks database (.qbw, .tlg, etc.).</summary>
        public bool IsQuickBooksFile
        {
            get => _isQuickBooksFile;
            set => SetField(ref _isQuickBooksFile, value);
        }

        /// <summary>Gets or sets whether a VSS Volume Shadow Copy snapshot is required for reading this file.</summary>
        public bool IsVssRequired
        {
            get => _isVssRequired;
            set => SetField(ref _isVssRequired, value);
        }

        /// <summary>Gets or sets the resolved VSS snapshot device path (e.g. \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyX\...).</summary>
        public string? VssResolvedPath
        {
            get => _vssResolvedPath;
            set => SetField(ref _vssResolvedPath, value);
        }

        /// <summary>Gets or sets the current transfer progress status.</summary>
        public TransferStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        /// <summary>Gets or sets the transfer completion percentage (0.0 to 100.0).</summary>
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetField(ref _progressPercentage, value);
        }

        /// <summary>Gets or sets the number of bytes transferred so far.</summary>
        public long BytesTransferred
        {
            get => _bytesTransferred;
            set => SetField(ref _bytesTransferred, value);
        }

        /// <summary>Gets or sets the current transfer throughput in bytes per second.</summary>
        public double TransferSpeedBps
        {
            get => _transferSpeedBps;
            set => SetField(ref _transferSpeedBps, value);
        }

        /// <summary>Gets or sets the client-side calculated SHA-256 hexadecimal hash string.</summary>
        public string LocalSha256
        {
            get => _localSha256;
            set => SetField(ref _localSha256, value);
        }

        /// <summary>Gets or sets the server-side calculated SHA-256 hexadecimal hash string.</summary>
        public string RemoteSha256
        {
            get => _remoteSha256;
            set => SetField(ref _remoteSha256, value);
        }

        /// <summary>Gets or sets the result of the two-step SHA-256 integrity validation.</summary>
        public HashMatchStatus HashStatus
        {
            get => _hashStatus;
            set => SetField(ref _hashStatus, value);
        }

        /// <summary>Gets or sets a user-facing diagnostic status message.</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        /// <summary>Gets or sets the detailed error message if the transfer failed.</summary>
        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetField(ref _errorMessage, value);
        }

        /// <summary>Gets or sets the timestamp when the transfer and verification completed.</summary>
        public DateTime? CompletedTime
        {
            get => _completedTime;
            set => SetField(ref _completedTime, value);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        /// <summary>
        /// Gets the human-readable formatted file size string (B, KB, MB, or GB).
        /// </summary>
        public string FormattedSize => FormatSize(FileSizeBytes);

        /// <summary>
        /// Helper to set property and notify change.
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
