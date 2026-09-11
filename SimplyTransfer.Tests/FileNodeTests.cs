using SimplyTransfer.UI.ViewModels;
using Xunit;

namespace SimplyTransfer.Tests
{
    public class FileNodeTests
    {
        [Theory]
        [InlineData(@"C:\Company Files\MyCompany.qbw", true)]
        [InlineData(@"C:\Company Files\MyCompany.tlg", true)]
        [InlineData(@"C:\Company Files\MyCompany.qbb", true)]
        [InlineData(@"C:\Company Files\MyCompany.nd", true)]
        [InlineData(@"C:\Documents\report.pdf", false)]
        [InlineData(@"C:\Documents\data.csv", false)]
        public void FileNode_DetectsQuickBooksFiles(string path, bool expectedQb)
        {
            var node = new FileNode(path, "testfile", isDirectory: false);
            Assert.Equal(expectedQb, node.IsQuickBooksFile);
        }

        [Fact]
        public void FileNode_SelectionPropagatesToChildren()
        {
            var parent = new FileNode(@"C:\Folder", "Folder", isDirectory: true);
            var child1 = new FileNode(@"C:\Folder\file1.txt", "file1.txt", isDirectory: false, parent);
            var child2 = new FileNode(@"C:\Folder\file2.txt", "file2.txt", isDirectory: false, parent);

            parent.Children.Add(child1);
            parent.Children.Add(child2);

            // Selecting parent should select children
            parent.IsSelected = true;

            Assert.True(child1.IsSelected);
            Assert.True(child2.IsSelected);

            // Unselecting parent should unselect children
            parent.IsSelected = false;

            Assert.False(child1.IsSelected);
            Assert.False(child2.IsSelected);
        }
    }
}
