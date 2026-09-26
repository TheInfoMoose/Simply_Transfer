using System;

namespace SimplyTransfer.Core.Models
{
    public class ManifestItem
    {
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
