using SimplyTransfer.Core.Services;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class RemotePathNormalizationTests
    {
        [Theory]
        [InlineData("C:/Backups/QuickBooks.qbw", @"C:\Backups\QuickBooks.qbw")]
        [InlineData("/C:/Backups/QuickBooks.qbw", @"C:\Backups\QuickBooks.qbw")]
        [InlineData(@"\C:\Backups\Company Files\file.qbw", @"C:\Backups\Company Files\file.qbw")]
        [InlineData("D:/Data/Sub/file.dat", @"D:\Data\Sub\file.dat")]
        [InlineData(@"C:\Backups\file.txt", @"C:\Backups\file.txt")]
        public void NormalizePathForWindows_NormalizesDriveAndSeparators(string input, string expected)
        {
            string result = SftpTransferService.NormalizePathForWindows(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(@"\var\backups\data.db", "/var/backups/data.db")]
        [InlineData("/var/backups/data.db", "/var/backups/data.db")]
        [InlineData(@"subfolder\file.txt", "subfolder/file.txt")]
        public void NormalizePathForUnix_NormalizesForwardSlashes(string input, string expected)
        {
            string result = SftpTransferService.NormalizePathForUnix(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("C:/Backups/file.txt", true)]
        [InlineData("/C:/Backups/file.txt", true)]
        [InlineData(@"D:\Data\file.db", true)]
        [InlineData(@"relative\path\file.txt", true)]
        [InlineData("/var/backups/file.txt", false)]
        [InlineData("file.txt", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsWindowsRemotePath_IdentifiesWindowsSyntax(string? input, bool expected)
        {
            bool result = SftpTransferService.IsWindowsRemotePath(input);
            Assert.Equal(expected, result);
        }
    }
}
