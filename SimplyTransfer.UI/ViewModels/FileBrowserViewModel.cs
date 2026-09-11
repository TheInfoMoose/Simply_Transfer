using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplyTransfer.UI.ViewModels
{
    /// <summary>
    /// ViewModel driving the hierarchical file browser TreeView with lazy directory loading,
    /// tri-state selection propagation, and QuickBooks database detection.
    /// </summary>
    public partial class FileBrowserViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<FileNode> _rootNodes = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="FileBrowserViewModel"/> class.
        /// Loads accessible system drives into the root nodes.
        /// </summary>
        public FileBrowserViewModel()
        {
            LoadDrives();
        }

        /// <summary>
        /// Enumerates all ready disk drives on the system and populates root nodes for browsing.
        /// Falls back to the user's home directory if drive enumeration is restricted.
        /// </summary>
        public void LoadDrives()
        {
            RootNodes.Clear();
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
                foreach (var drive in drives)
                {
                    var driveNode = new FileNode(drive.RootDirectory.FullName, drive.Name, isDirectory: true);
                    driveNode.AddDummyChild();
                    RootNodes.Add(driveNode);
                }
            }
            catch
            {
                // Fallback to desktop or user profile
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var root = new FileNode(userProfile, Path.GetFileName(userProfile), isDirectory: true);
                root.AddDummyChild();
                RootNodes.Add(root);
            }
        }

        /// <summary>
        /// Collects all currently selected file paths across the entire browser tree.
        /// Recursively scans unexpanded selected folders on disk.
        /// </summary>
        /// <returns>A list of distinct absolute file paths chosen for synchronization.</returns>
        public List<string> GetSelectedFilePaths()
        {
            var result = new List<string>();
            foreach (var root in RootNodes)
            {
                CollectSelectedFiles(root, result);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void CollectSelectedFiles(FileNode node, List<string> files)
        {
            // Ignore dummy placeholder nodes completely
            if (string.IsNullOrWhiteSpace(node.FullPath))
            {
                return;
            }

            if (!node.IsDirectory && node.IsSelected == true)
            {
                if (File.Exists(node.FullPath))
                {
                    files.Add(node.FullPath);
                }
                return;
            }

            if (node.IsDirectory && node.IsSelected != false)
            {
                // If folder is fully selected and has not been expanded (i.e. children not loaded or only dummy child)
                bool isUnexpanded = node.Children.Count == 0 || 
                                    (node.Children.Count == 1 && string.IsNullOrEmpty(node.Children[0].FullPath));

                if (node.IsSelected == true && isUnexpanded)
                {
                    // Expand and gather all files underneath recursively from disk
                    try
                    {
                        if (Directory.Exists(node.FullPath))
                        {
                            var dirFiles = Directory.GetFiles(node.FullPath, "*.*", SearchOption.AllDirectories);
                            foreach (var f in dirFiles)
                            {
                                if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                                {
                                    files.Add(f);
                                }
                            }
                        }
                        return;
                    }
                    catch
                    {
                        // Ignore permission errors on restricted system folders
                    }
                }

                foreach (var child in node.Children)
                {
                    CollectSelectedFiles(child, files);
                }
            }
        }
    }

    /// <summary>
    /// Represents a single hierarchical item (directory or file) in the file browser tree.
    /// Supports tri-state selection, lazy child loading, and QuickBooks database recognition.
    /// </summary>
    public partial class FileNode : ObservableObject
    {
        private bool? _isSelected = false;
        private bool _isUpdatingSelection;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _fullPath = string.Empty;

        [ObservableProperty]
        private bool _isDirectory;

        [ObservableProperty]
        private bool _isQuickBooksFile;

        [ObservableProperty]
        private long _sizeBytes;

        [ObservableProperty]
        private bool _isExpanded;

        [ObservableProperty]
        private ObservableCollection<FileNode> _children = new();

        /// <summary>Gets or sets the parent node in the hierarchy.</summary>
        public FileNode? Parent { get; set; }

        /// <summary>Gets or sets the tri-state selection state (true: checked, false: unchecked, null: indeterminate).</summary>
        public bool? IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    OnSelectionChanged();
                }
            }
        }

        /// <summary>Gets the formatted size string for display (empty for directories).</summary>
        public string FormattedSize
        {
            get
            {
                if (IsDirectory) return string.Empty;
                if (SizeBytes < 1024) return $"{SizeBytes} B";
                if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024.0:F1} KB";
                if (SizeBytes < 1024 * 1024 * 1024) return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
                return $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }

        /// <summary>Gets the badge indicator string for QuickBooks databases.</summary>
        public string DisplayBadgeText => IsQuickBooksFile ? "QB" : string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileNode"/> class.
        /// </summary>
        /// <param name="fullPath">Absolute file system path.</param>
        /// <param name="name">File or folder name.</param>
        /// <param name="isDirectory">True if directory; false if file.</param>
        /// <param name="parent">Parent node in tree.</param>
        public FileNode(string fullPath, string name, bool isDirectory, FileNode? parent = null)
        {
            FullPath = fullPath;
            Name = name;
            IsDirectory = isDirectory;
            Parent = parent;

            if (!isDirectory)
            {
                CheckQuickBooksExtension();
            }
        }

        private void CheckQuickBooksExtension()
        {
            string ext = Path.GetExtension(FullPath);
            IsQuickBooksFile = string.Equals(ext, ".qbw", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".tlg", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".qbb", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".nd", StringComparison.OrdinalIgnoreCase);
        }

        public void AddDummyChild()
        {
            if (IsDirectory && Children.Count == 0)
            {
                Children.Add(new FileNode(string.Empty, "Loading...", isDirectory: false, this));
            }
        }

        partial void OnIsExpandedChanged(bool value)
        {
            if (value && IsDirectory)
            {
                // Lazy load directory contents if dummy is present
                if (Children.Count == 1 && string.IsNullOrEmpty(Children[0].FullPath))
                {
                    LoadChildren();
                }
            }
        }

        public void LoadChildren()
        {
            Children.Clear();
            try
            {
                var dirInfo = new DirectoryInfo(FullPath);

                // Load Directories
                foreach (var subDir in dirInfo.GetDirectories().OrderBy(d => d.Name))
                {
                    // Skip hidden or system
                    if ((subDir.Attributes & FileAttributes.Hidden) != 0 || (subDir.Attributes & FileAttributes.System) != 0)
                        continue;

                    var dirNode = new FileNode(subDir.FullName, subDir.Name, isDirectory: true, this);
                    dirNode.AddDummyChild();
                    if (IsSelected == true)
                    {
                        dirNode._isSelected = true;
                    }
                    Children.Add(dirNode);
                }

                // Load Files
                foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
                {
                    if ((file.Attributes & FileAttributes.Hidden) != 0) continue;

                    var fileNode = new FileNode(file.FullName, file.Name, isDirectory: false, this)
                    {
                        SizeBytes = file.Length
                    };
                    if (IsSelected == true)
                    {
                        fileNode._isSelected = true;
                    }
                    Children.Add(fileNode);
                }
            }
            catch
            {
                // Access denied or unreadable volume
            }
        }

        private void OnSelectionChanged()
        {
            if (_isUpdatingSelection) return;

            _isUpdatingSelection = true;
            try
            {
                // Propagate down to children
                if (IsSelected.HasValue && IsDirectory)
                {
                    PropagateSelectionDown(this, IsSelected.Value);
                }

                // Propagate up to parent
                Parent?.UpdateSelectionFromChildren();
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        private static void PropagateSelectionDown(FileNode parent, bool isSelected)
        {
            foreach (var child in parent.Children)
            {
                // Never select or propagate to dummy placeholder nodes
                if (string.IsNullOrEmpty(child.FullPath)) continue;

                child._isSelected = isSelected;
                child.OnPropertyChanged(nameof(IsSelected));
                if (child.IsDirectory)
                {
                    PropagateSelectionDown(child, isSelected);
                }
            }
        }

        private void UpdateSelectionFromChildren()
        {
            // Ignore dummy nodes when evaluating parent selection
            var realChildren = Children.Where(c => !string.IsNullOrEmpty(c.FullPath)).ToList();
            if (realChildren.Count == 0) return;

            bool allChecked = realChildren.All(c => c.IsSelected == true);
            bool allUnchecked = realChildren.All(c => c.IsSelected == false);

            bool? newSelection;
            if (allChecked) newSelection = true;
            else if (allUnchecked) newSelection = false;
            else newSelection = null; // Indeterminate

            if (_isSelected != newSelection)
            {
                _isSelected = newSelection;
                OnPropertyChanged(nameof(IsSelected));
                Parent?.UpdateSelectionFromChildren();
            }
        }
    }
}
