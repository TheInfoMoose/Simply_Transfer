using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimplyTransfer.Core.Services
{
    /// <summary>
    /// Provides cryptographic SHA-256 hash calculation and comparison routines
    /// for local files and streaming data.
    /// </summary>
    public class HashValidationService
    {
        private const int BufferSize = 1024 * 1024; // 1 MB buffer for high throughput

        /// <summary>
        /// Asynchronously computes the SHA-256 hash for a given file on disk.
        /// </summary>
        /// <param name="filePath">The absolute path to the local file.</param>
        /// <param name="progress">Optional progress reporter for reporting calculation progress (0.0 to 100.0).</param>
        /// <param name="cancellationToken">Cancellation token to cancel calculation.</param>
        /// <returns>A lowercase hexadecimal SHA-256 string (64 characters).</returns>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        public async Task<string> ComputeLocalHashAsync(string filePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found for hash calculation", filePath);

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
            return await ComputeStreamHashAsync(fileStream, progress, cancellationToken);
        }

        /// <summary>
        /// Asynchronously computes the SHA-256 hash for an open readable stream using a buffered transform.
        /// </summary>
        /// <param name="stream">The source readable stream.</param>
        /// <param name="progress">Optional progress reporter for reporting calculation progress (0.0 to 100.0).</param>
        /// <param name="cancellationToken">Cancellation token to cancel calculation.</param>
        /// <returns>A lowercase hexadecimal SHA-256 string (64 characters).</returns>
        public async Task<string> ComputeStreamHashAsync(Stream stream, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            using var sha256 = SHA256.Create();
            byte[] buffer = new byte[BufferSize];
            long totalBytes = stream.CanSeek ? stream.Length : 0;
            long bytesReadTotal = 0;

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                bytesReadTotal += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)bytesReadTotal / totalBytes * 100.0);
                }
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] hashBytes = sha256.Hash ?? Array.Empty<byte>();

            return ConvertBytesToHex(hashBytes);
        }

        /// <summary>
        /// Compares two SHA-256 hash strings using case-insensitive ordinal comparison.
        /// </summary>
        /// <param name="localHash">The local SHA-256 hash.</param>
        /// <param name="remoteHash">The remote SHA-256 hash computed on the destination host.</param>
        /// <returns>True if both hashes are non-empty and match exactly; otherwise false.</returns>
        public bool ValidateHashes(string localHash, string remoteHash)
        {
            if (string.IsNullOrWhiteSpace(localHash) || string.IsNullOrWhiteSpace(remoteHash))
                return false;

            return string.Equals(localHash.Trim(), remoteHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts and normalizes a 64-character hexadecimal SHA-256 string from command output
        /// produced by common tools such as certutil, powershell, sha256sum, shasum, or openssl.
        /// </summary>
        /// <param name="output">The standard output string from a remote shell hashing command.</param>
        /// <returns>A 64-character lowercase hexadecimal hash, or null if no valid hash is found.</returns>
        public static string? TryParseSha256(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var match = Regex.Match(output, @"\b[0-9a-fA-F]{64}\b");
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }

        /// <summary>
        /// Converts a byte array to a lowercase hexadecimal string.
        /// </summary>
        private static string ConvertBytesToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
