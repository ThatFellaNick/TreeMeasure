// -----------------------------------------------------------------------------
// TreeMeasure
// -----------------------------------------------------------------------------
// A lightweight Windows disk-usage viewer.
//
// Design goals:
// - Build as a small .NET Framework WinForms executable with no installer.
// - Work in normal desktop sessions and ScreenConnect Backstage/SYSTEM sessions.
// - Scan folders on a background thread while progressively updating the tree.
// - Keep the UI familiar: expandable rows, usage bars, sortable headers,
//   familiar shell icons, and a small read-only right-click menu.
//
// This file intentionally keeps the application in one source file so it can be
// compiled by the C# compiler included with .NET Framework on Windows.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("TreeMeasure")]
[assembly: AssemblyDescription("Portable Windows disk usage analyzer")]
[assembly: AssemblyCompany("TreeMeasure Project")]
[assembly: AssemblyProduct("TreeMeasure")]
[assembly: AssemblyCopyright("Copyright 2026 TreeMeasure Project")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace TreeMeasure
{
    // Application entry point and command-line startup path parser.
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(ParseStartupPath(args)));
        }

        private static string ParseStartupPath(string[] args)
        {
            // Accept either a bare path or "/path <folder>" so backstage scripts can launch directly into a scan.
            if (args == null || args.Length == 0) return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if ((arg.Equals("/path", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-path", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                if (Directory.Exists(arg) || File.Exists(arg))
                {
                    return arg;
                }
            }

            return null;
        }
    }

    // Represents one scanned filesystem item. Folders aggregate child sizes and counts.
    [DataContract]
    internal sealed class DiskItem
    {
        // Display and filesystem identity.
        [DataMember(Name = "name", Order = 1)]
        public string Name;

        [DataMember(Name = "path", Order = 2)]
        public string Path;

        public bool IsFile;

        [DataMember(Name = "type", Order = 3)]
        public string ItemType
        {
            get { return IsFile ? "file" : "folder"; }
            private set { IsFile = string.Equals(value, "file", StringComparison.OrdinalIgnoreCase); }
        }

        // Disk usage metrics shown in the table.
        [DataMember(Name = "sizeBytes", Order = 4)]
        public long Size;

        [DataMember(Name = "fileCount", Order = 5)]
        public long FileCount;

        [DataMember(Name = "folderCount", Order = 6)]
        public long FolderCount;

        [DataMember(Name = "skippedCount", Order = 7)]
        public int SkippedCount;

        // Tree relationships and UI bookkeeping.
        public DiskItem Parent;
        public TreeNode Node;
        public int UpdateQueued;
        public bool ChildrenMaterialized;

        [DataMember(Name = "children", Order = 8)]
        public List<DiskItem> Children = new List<DiskItem>();
    }

    // Top-level JSON export envelope with a stable schema version and generation timestamp.
    [DataContract]
    internal sealed class ScanExport
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion = 1;

        [DataMember(Name = "generatedUtc", Order = 2)]
        public string GeneratedUtc;

        [DataMember(Name = "root", Order = 3)]
        public DiskItem Root;
    }

    // Sort targets shared by the toolbar dropdown and clickable table headers.
    internal enum SortColumn
    {
        None,
        Name,
        Size,
        Files,
        Percent,
        Path
    }

    // TreeView with reduced flicker during frequent live scan updates.
    internal sealed class BufferedTreeView : TreeView
    {
        public BufferedTreeView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            UpdateStyles();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_COMPOSITED gives the owner-drawn tree another layer of buffering.
                cp.ExStyle |= 0x02000000;
                return cp;
            }
        }
    }

    // Main TreeMeasure window. Owns scanning, tree rendering, sorting, and file actions.
    internal sealed class MainForm : Form
    {
        // Core UI controls.
        private readonly TreeView tree = new BufferedTreeView();
        private readonly Label header = new Label();
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel scanText = new ToolStripStatusLabel();
        private readonly ToolStrip toolStrip = new ToolStrip();
        private readonly ToolStripComboBox unitBox = new ToolStripComboBox();
        private readonly ToolStripComboBox sortBox = new ToolStripComboBox();
        private readonly ContextMenuStrip nodeMenu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer();

        // Scan progress is updated from the scanner thread and read from the UI thread.
        private readonly object progressLock = new object();
        private readonly ConcurrentQueue<DiskItem> liveUpdates = new ConcurrentQueue<DiskItem>();

        // Explorer shell icons are expensive to request repeatedly, so cache by extension.
        private readonly Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        // Current scan state.
        private DiskItem rootItem;
        private string currentRoot;
        private Thread scanThread;
        private CancellationTokenSource cancelSource;
        private volatile bool scanning;
        private long scannedFiles;
        private long scannedFolders;
        private int scanSkipped;
        private SortColumn sortColumn = SortColumn.Size;
        private bool sortDescending = true;

        // Optional path passed on the command line.
        private readonly string startupPath;

        public MainForm(string startupPath)
        {
            this.startupPath = startupPath;
            Text = "TreeMeasure";
            // Use the icon embedded into the executable by build.ps1.
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            Width = 1180;
            Height = 760;
            MinimumSize = new Size(860, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            BuildToolbar();
            BuildTree();
            BuildMenu();

            // The header is custom drawn so it can behave like a simple sortable table header.
            header.Dock = DockStyle.Top;
            header.Height = 25;
            header.BackColor = Color.FromArgb(245, 247, 250);
            header.ForeColor = Color.FromArgb(34, 40, 49);
            header.TextAlign = ContentAlignment.MiddleLeft;
            header.Padding = new Padding(8, 0, 0, 0);
            header.Paint += DrawHeader;
            header.MouseClick += HeaderMouseClick;
            header.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                header.Cursor = HitTestHeader(e.X) == SortColumn.None ? Cursors.Default : Cursors.Hand;
            };

            // The status bar reports scan progress, totals, and skipped items.
            status.Items.Add(statusText);
            status.Items.Add(new ToolStripStatusLabel { Spring = true });
            status.Items.Add(scanText);

            Controls.Add(tree);
            Controls.Add(header);
            Controls.Add(toolStrip);
            Controls.Add(status);

            progressTimer.Interval = 400;
            progressTimer.Tick += delegate { UpdateProgressStatus(); };
            Shown += delegate
            {
                // If launched with a path, start scanning immediately; otherwise ask the user.
                if (!string.IsNullOrEmpty(this.startupPath) && Directory.Exists(this.startupPath))
                    StartScan(this.startupPath);
                else
                    PromptAndScan();
            };
            FormClosing += delegate { StopScan(); };
        }

        private void BuildToolbar()
        {
            // Keep the toolbar compact for backstage and remote sessions where screen space is limited.
            toolStrip.Dock = DockStyle.Top;
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;

            // Scan controls.
            var selectButton = new ToolStripButton("Select");
            selectButton.Click += delegate { PromptAndScan(); };

            var stopButton = new ToolStripButton("Stop");
            stopButton.Click += delegate { StopScan(); };

            var refreshButton = new ToolStripButton("Refresh");
            refreshButton.Click += delegate { if (!string.IsNullOrEmpty(currentRoot)) StartScan(currentRoot); };

            var exportButton = new ToolStripButton("Export JSON");
            exportButton.ToolTipText = "Export the completed file structure for analysis.";
            exportButton.Click += delegate { ExportJson(rootItem, "TreeMeasure-scan.json"); };

            // Unit and sorting controls mirror the clickable header behavior.
            unitBox.DropDownStyle = ComboBoxStyle.DropDownList;
            unitBox.Width = 92;
            unitBox.Items.AddRange(new object[] { "Auto units", "GB", "MB", "KB", "Bytes" });
            unitBox.SelectedIndex = 0;
            unitBox.SelectedIndexChanged += delegate { tree.Invalidate(); };

            sortBox.DropDownStyle = ComboBoxStyle.DropDownList;
            sortBox.Width = 116;
            sortBox.Items.AddRange(new object[] { "Size descending", "Size ascending", "Name A-Z", "Name Z-A", "Files descending", "Files ascending", "Percent descending", "Percent ascending", "Path A-Z", "Path Z-A" });
            sortBox.SelectedIndex = 0;
            sortBox.SelectedIndexChanged += delegate
            {
                ApplyToolbarSort();
                if (!scanning) RebuildTree();
                header.Invalidate();
            };

            toolStrip.Items.Add(selectButton);
            toolStrip.Items.Add(stopButton);
            toolStrip.Items.Add(refreshButton);
            toolStrip.Items.Add(exportButton);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(new ToolStripLabel("Units"));
            toolStrip.Items.Add(unitBox);
            toolStrip.Items.Add(new ToolStripLabel("Sort"));
            toolStrip.Items.Add(sortBox);
        }

        private void BuildTree()
        {
            // The native TreeView supplies selection, keyboard navigation, and expand/collapse state.
            // TreeMeasure draws the row contents itself to get multi-column usage rows and bars.
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.HideSelection = false;
            tree.FullRowSelect = true;
            tree.ShowLines = false;
            tree.ShowPlusMinus = false;
            tree.ShowRootLines = false;
            tree.Indent = TreeIndent();
            tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
            tree.ItemHeight = 24;
            tree.DrawNode += DrawNode;
            tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e)
            {
                // Children are materialized lazily so large scans do not create thousands of nodes at once.
                var item = e.Node.Tag as DiskItem;
                if (item != null) EnsureVisibleChildNodes(item);
            };
            tree.AfterExpand += delegate { tree.Invalidate(); };
            tree.AfterCollapse += delegate { tree.Invalidate(); };
            tree.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    // Use the app-owned menu so right-click actions stay predictable and read-only.
                    tree.SelectedNode = e.Node;
                    nodeMenu.Show(tree, e.Location);
                }
                else if (e.Button == MouseButtons.Left && IsExpandMarkerClick(e))
                {
                    // Single click toggles only when the user clicks directly on the chevron.
                    ToggleFolderNode(e.Node);
                }
            };
        }

        private void BuildMenu()
        {
            // Keep actions explicit and non-destructive; no shell extension commands are loaded.
            nodeMenu.Items.Add("Open in File Explorer", null, delegate { OpenSelectedInExplorer(); });
            nodeMenu.Items.Add("Copy Path", null, delegate { CopySelectedPath(); });
            nodeMenu.Items.Add("Expand Branch", null, delegate { if (tree.SelectedNode != null) tree.SelectedNode.ExpandAll(); });
            nodeMenu.Items.Add("Collapse Branch", null, delegate { if (tree.SelectedNode != null) tree.SelectedNode.Collapse(false); });
            nodeMenu.Items.Add(new ToolStripSeparator());
            nodeMenu.Items.Add("Rescan This Folder", null, delegate { RescanSelected(); });
            nodeMenu.Items.Add("Export Branch to JSON", null, delegate { ExportJson(SelectedItem(), "TreeMeasure-branch.json"); });
            nodeMenu.Items.Add("Export Branch to CSV", null, delegate { ExportSelectedCsv(); });
        }

        private void PromptAndScan()
        {
            // Show the start dialog for manual drive/folder selection.
            using (var dialog = new StartDialog())
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    StartScan(dialog.SelectedPath);
                }
            }
        }

        private void StartScan(string path)
        {
            // Normalize local, mapped-drive, and UNC paths before starting the worker.
            try
            {
                path = new DirectoryInfo(path).FullName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "That folder path is invalid:\r\n\r\n" + ex.Message, "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "That folder could not be reached. For a network share, verify the UNC path and the current Windows account's permissions.", "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Reset all scan state and immediately show the root row so the UI feels alive.
            StopScan();
            currentRoot = path;
            rootItem = new DiskItem { Name = FriendlyName(path), Path = path, IsFile = false };
            tree.Nodes.Clear();
            var rootNode = new TreeNode(rootItem.Name) { Tag = rootItem };
            rootItem.Node = rootNode;
            tree.Nodes.Add(rootNode);
            rootNode.Expand();
            statusText.Text = "Scanning " + path;
            scannedFiles = 0;
            scannedFolders = 0;
            scanSkipped = 0;
            DiskItem ignored;
            while (liveUpdates.TryDequeue(out ignored)) { }
            scanning = true;
            cancelSource = new CancellationTokenSource();
            progressTimer.Start();

            scanThread = new Thread(delegate()
            {
                // The scanner owns filesystem traversal; UI work is marshaled back to the form thread.
                ScanPath(path, rootItem, cancelSource.Token);
                if (cancelSource.IsCancellationRequested) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    scanning = false;
                    RebuildTree();
                    progressTimer.Stop();
                    statusText.Text = "Ready";
                    scanText.Text = FormatSize(rootItem.Size) + " in " + rootItem.FileCount.ToString("N0") + " files, " +
                                    rootItem.FolderCount.ToString("N0") + " folders, " + rootItem.SkippedCount.ToString("N0") + " skipped";
                    tree.Invalidate();
                });
            });
            scanThread.IsBackground = true;
            // Below-normal priority keeps the machine usable during a full-drive scan.
            scanThread.Priority = ThreadPriority.BelowNormal;
            scanThread.Start();
        }

        private void StopScan()
        {
            // Cooperative cancellation lets long scans stop without aborting the thread unsafely.
            if (cancelSource != null)
            {
                cancelSource.Cancel();
            }
            scanning = false;
            progressTimer.Stop();
        }

        private void ScanPath(string path, DiskItem root, CancellationToken token)
        {
            // Start recursion from the selected root directory.
            var info = new DirectoryInfo(path);
            ScanDirectory(info, root, token);
        }

        private void ScanDirectory(DirectoryInfo directory, DiskItem item, CancellationToken token)
        {
            // Directory enumeration is defensive: protected folders and transient files are counted as skipped.
            if (token.IsCancellationRequested) return;
            IncrementFolder();
            QueueLiveUpdate(item);

            FileSystemInfo[] entries;
            try { entries = directory.GetFileSystemInfos(); }
            catch { AddSkipped(item); return; }

            int fileBatch = 0;
            foreach (var entry in entries)
            {
                var file = entry as FileInfo;
                if (file == null) continue;

                // Store file rows so expanded folders show their contents, but only draw them when expanded.
                if (token.IsCancellationRequested) return;
                long length = 0;
                try { length = file.Length; }
                catch { AddSkipped(item); continue; }

                AddChildFile(item, new DiskItem
                {
                    Name = file.Name,
                    Path = file.FullName,
                    IsFile = true,
                    Size = length,
                    FileCount = 1,
                    Parent = item
                });
                AddFileSize(item, length);
                IncrementFile();
                fileBatch++;
                if (fileBatch >= 100)
                {
                    QueueLiveUpdate(item);
                    fileBatch = 0;
                }
            }
            QueueLiveUpdate(item);

            foreach (var entry in entries)
            {
                var childDirectory = entry as DirectoryInfo;
                if (childDirectory == null) continue;

                if (token.IsCancellationRequested) return;
                // Reparse points can create loops or expensive detours, so avoid recursing into them.
                if (IsReparsePoint(childDirectory)) continue;

                var child = new DiskItem { Name = childDirectory.Name, Path = childDirectory.FullName, IsFile = false, Parent = item };
                AddChildFolder(item, child);
                QueueLiveUpdate(child);
                QueueLiveUpdate(item);
                ScanDirectory(childDirectory, child, token);
                QueueLiveUpdate(child);
                QueueLiveUpdate(item);
            }
        }

        private void AddChildFolder(DiskItem parent, DiskItem child)
        {
            // Add folder to the parent and propagate folder counts up to the root.
            lock (parent.Children)
            {
                parent.Children.Add(child);
            }

            for (DiskItem cursor = parent; cursor != null; cursor = cursor.Parent)
            {
                cursor.FolderCount++;
            }
        }

        private void AddChildFile(DiskItem parent, DiskItem child)
        {
            // File children are kept for on-demand expansion, but file counts are propagated separately.
            lock (parent.Children)
            {
                parent.Children.Add(child);
            }
        }

        private void AddFileSize(DiskItem item, long length)
        {
            // Bubble size and file count upward so parent folders update while the scan is still running.
            for (DiskItem cursor = item; cursor != null; cursor = cursor.Parent)
            {
                cursor.Size += length;
                cursor.FileCount++;
            }
        }

        private void AddSkipped(DiskItem item)
        {
            // Bubble skipped items upward so totals match what the user sees at every level.
            for (DiskItem cursor = item; cursor != null; cursor = cursor.Parent)
            {
                cursor.SkippedCount++;
            }
            IncrementSkipped();
        }

        private void QueueLiveUpdate(DiskItem item)
        {
            // Coalesce updates per item so the UI queue cannot grow without bound during fast scans.
            if (item == null) return;
            if (Interlocked.Exchange(ref item.UpdateQueued, 1) == 0)
            {
                liveUpdates.Enqueue(item);
            }
        }

        private void ApplyLiveUpdates(int maxItems)
        {
            // Apply a bounded batch each timer tick to keep the message loop responsive.
            DiskItem item;
            int count = 0;
            bool changed = false;
            tree.BeginUpdate();
            while (count < maxItems && liveUpdates.TryDequeue(out item))
            {
                Interlocked.Exchange(ref item.UpdateQueued, 0);
                bool nodeCreated = EnsureTreeNode(item);
                if (item.Node != null)
                {
                    item.Node.Text = item.Name;
                    SyncNodeExpander(item);
                    changed = true;
                }
                if (nodeCreated) changed = true;
                count++;
            }
            tree.EndUpdate();
            if (changed) tree.Invalidate();
        }

        private bool EnsureTreeNode(DiskItem item)
        {
            // Create a node only if its parent branch is already materialized.
            if (item == null || item.Node != null) return false;
            if (item.Parent == null) return false;

            if (item.Parent.Node == null) return false;
            if (!item.Parent.ChildrenMaterialized && item.Parent.Parent != null) return false;

            var node = CreateShallowNode(item);
            item.Node = node;
            InsertSortedNode(item.Parent.Node, node);
            return true;
        }

        private void EnsureVisibleChildNodes(DiskItem item)
        {
            // Populate one expanded branch with the children already discovered by the scanner.
            if (item == null || item.Node == null) return;
            if (item.ChildrenMaterialized) return;

            List<DiskItem> children;
            lock (item.Children)
            {
                children = new List<DiskItem>(item.Children);
            }

            SortChildList(children);
            tree.BeginUpdate();
            item.Node.Nodes.Clear();
            foreach (var child in children)
            {
                var node = CreateShallowNode(child);
                child.Node = node;
                item.Node.Nodes.Add(node);
            }
            item.ChildrenMaterialized = true;
            tree.EndUpdate();
            tree.Invalidate();
        }

        private void InsertSortedNode(TreeNode parentNode, TreeNode node)
        {
            // Live updates insert into the currently visible branch using the active sort order.
            var item = node.Tag as DiskItem;
            if (item == null)
            {
                parentNode.Nodes.Add(node);
                return;
            }

            int insertAt = parentNode.Nodes.Count;
            for (int i = 0; i < parentNode.Nodes.Count; i++)
            {
                var other = parentNode.Nodes[i].Tag as DiskItem;
                if (other == null) continue;
                if (CompareForSort(item, other) < 0)
                {
                    insertAt = i;
                    break;
                }
            }
            parentNode.Nodes.Insert(insertAt, node);
        }

        private void SyncNodeExpander(DiskItem item)
        {
            // If children arrive after a folder row has been drawn, add the placeholder expander node.
            if (item == null || item.Node == null) return;
            if (item.ChildrenMaterialized) return;
            if (item.Node.Nodes.Count == 0 && HasDiskChildren(item))
            {
                item.Node.Nodes.Add(new TreeNode(""));
            }
        }

        private static bool IsReparsePoint(DirectoryInfo directory)
        {
            // Treat unreadable attribute checks as unsafe to recurse into.
            try
            {
                return (directory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return true;
            }
        }

        private void RebuildTree()
        {
            // Rebuilds happen after sorting or scan completion; preserve the user's current navigation state.
            HashSet<string> expandedPaths = CaptureExpandedPaths();
            string selectedPath = SelectedItem() == null ? null : SelectedItem().Path;

            tree.BeginUpdate();
            tree.Nodes.Clear();
            if (rootItem != null)
            {
                SortChildren(rootItem);
                ClearTreeNodeReferences(rootItem);
                var rootNode = CreateShallowNode(rootItem);
                tree.Nodes.Add(rootNode);
                rootItem.Node = rootNode;
                EnsureVisibleChildNodes(rootItem);
                RestoreExpandedPaths(rootNode, expandedPaths);
                if (!rootNode.IsExpanded) rootNode.Expand();
                RestoreSelection(selectedPath);
            }
            tree.EndUpdate();
            tree.Invalidate();
        }

        private TreeNode CreateShallowNode(DiskItem item)
        {
            // Shallow nodes show an expander placeholder without recursively creating all descendants.
            var node = new TreeNode(item.Name) { Tag = item };
            item.Node = node;
            item.ChildrenMaterialized = false;
            if (HasDiskChildren(item))
            {
                node.Nodes.Add(new TreeNode(""));
            }
            return node;
        }

        private bool HasDiskChildren(DiskItem item)
        {
            // A folder with files but no subfolders is still expandable because file rows are visible.
            lock (item.Children)
            {
                return item.Children.Count > 0;
            }
        }

        private bool HasChildFolders(DiskItem item)
        {
            // Used for visual decisions that care specifically about folder descendants.
            List<DiskItem> children;
            lock (item.Children)
            {
                children = new List<DiskItem>(item.Children);
            }
            foreach (var child in children)
            {
                if (!child.IsFile) return true;
            }
            return false;
        }

        private bool IsExpandableFolder(TreeNode node)
        {
            // Only folders can expand; files are leaf rows.
            var item = node == null ? null : node.Tag as DiskItem;
            return item != null && !item.IsFile && HasDiskChildren(item);
        }

        private bool IsExpandMarkerClick(TreeNodeMouseClickEventArgs e)
        {
            // Keep single-click expansion limited to the chevron area, not the whole folder name.
            if (!IsExpandableFolder(e.Node)) return false;
            return MarkerBounds(e.Node, e.Node.Bounds.Top).Contains(e.X, e.Y);
        }

        private void ToggleFolderNode(TreeNode node)
        {
            // Shared expand/collapse behavior for the custom chevron.
            if (!IsExpandableFolder(node)) return;
            if (node.IsExpanded)
                node.Collapse(false);
            else
                node.Expand();
            tree.Invalidate();
        }

        private HashSet<string> CaptureExpandedPaths()
        {
            // Store expanded paths rather than TreeNode references because rebuilds replace nodes.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode node in tree.Nodes)
            {
                CaptureExpandedPaths(node, paths);
            }
            return paths;
        }

        private void CaptureExpandedPaths(TreeNode node, HashSet<string> paths)
        {
            // Recursive helper for CaptureExpandedPaths.
            var item = node.Tag as DiskItem;
            if (item != null && node.IsExpanded) paths.Add(item.Path);
            foreach (TreeNode child in node.Nodes)
            {
                CaptureExpandedPaths(child, paths);
            }
        }

        private void RestoreExpandedPaths(TreeNode node, HashSet<string> paths)
        {
            // Re-expand matching paths after a tree rebuild.
            var item = node.Tag as DiskItem;
            if (item == null) return;
            if (paths.Contains(item.Path))
            {
                node.Expand();
                EnsureVisibleChildNodes(item);
            }
            foreach (TreeNode child in node.Nodes)
            {
                RestoreExpandedPaths(child, paths);
            }
        }

        private void RestoreSelection(string selectedPath)
        {
            // Keep the selected item stable across sorting and completion rebuilds when possible.
            if (string.IsNullOrEmpty(selectedPath)) return;
            TreeNode node = FindNodeByPath(selectedPath);
            if (node != null) tree.SelectedNode = node;
        }

        private TreeNode FindNodeByPath(string path)
        {
            // Search visible/materialized nodes for a filesystem path.
            foreach (TreeNode node in tree.Nodes)
            {
                TreeNode found = FindNodeByPath(node, path);
                if (found != null) return found;
            }
            return null;
        }

        private TreeNode FindNodeByPath(TreeNode node, string path)
        {
            // Recursive helper for FindNodeByPath.
            var item = node.Tag as DiskItem;
            if (item != null && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (TreeNode child in node.Nodes)
            {
                TreeNode found = FindNodeByPath(child, path);
                if (found != null) return found;
            }
            return null;
        }

        private void ClearTreeNodeReferences(DiskItem item)
        {
            // Reset UI references before building a fresh tree from the DiskItem model.
            item.Node = null;
            item.ChildrenMaterialized = false;
            List<DiskItem> children;
            lock (item.Children)
            {
                children = new List<DiskItem>(item.Children);
            }
            foreach (var child in children) ClearTreeNodeReferences(child);
        }

        private void SortChildren(DiskItem item)
        {
            // Sort the model recursively so newly materialized branches follow the active order.
            lock (item.Children)
            {
                item.Children.Sort(CompareForSort);
            }
            List<DiskItem> children;
            lock (item.Children)
            {
                children = new List<DiskItem>(item.Children);
            }
            foreach (var child in children) SortChildren(child);
        }

        private void SortChildList(List<DiskItem> children)
        {
            // Sort a copied child list before adding visible nodes.
            children.Sort(CompareForSort);
        }

        private int CompareForSort(DiskItem a, DiskItem b)
        {
            // Match Explorer convention: folders first, then files, within each folder.
            if (a.IsFile != b.IsFile) return a.IsFile ? 1 : -1;

            int result;
            if (sortColumn == SortColumn.Name)
                result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            else if (sortColumn == SortColumn.Files)
                result = a.FileCount.CompareTo(b.FileCount);
            else if (sortColumn == SortColumn.Percent)
                result = a.Size.CompareTo(b.Size);
            else if (sortColumn == SortColumn.Path)
                result = string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
            else
                result = a.Size.CompareTo(b.Size);

            if (sortDescending) result = -result;
            if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return result;
        }

        private void DrawHeader(object sender, PaintEventArgs e)
        {
            // Draw clickable table headers with the active sort direction marker.
            var g = e.Graphics;
            using (var pen = new Pen(Color.FromArgb(215, 221, 230)))
            {
                g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            }
            DrawHeaderText(g, SortColumn.Name, "Name", NameColumnX(), SizeColumnX() - NameColumnX());
            DrawHeaderText(g, SortColumn.Size, "Size", SizeColumnX(), FilesColumnX() - SizeColumnX());
            DrawHeaderText(g, SortColumn.Files, "Files", FilesColumnX(), PercentColumnX() - FilesColumnX());
            DrawHeaderText(g, SortColumn.Percent, "% of root", PercentColumnX(), PathColumnX() - PercentColumnX());
            DrawHeaderText(g, SortColumn.Path, "Path", PathColumnX(), header.Width - PathColumnX());
        }

        private void DrawHeaderText(Graphics g, SortColumn column, string text, int x, int width)
        {
            // Append a simple direction marker to whichever column is currently sorted.
            string label = text + (sortColumn == column ? (sortDescending ? " v" : " ^") : "");
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, 3, Math.Max(40, width - 6), 18), Color.FromArgb(34, 40, 49),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private void HeaderMouseClick(object sender, MouseEventArgs e)
        {
            // Clicking a header selects that sort column; clicking it again flips direction.
            SortColumn column = HitTestHeader(e.X);
            if (column == SortColumn.None) return;

            if (sortColumn == column)
                sortDescending = !sortDescending;
            else
            {
                sortColumn = column;
                sortDescending = column != SortColumn.Name && column != SortColumn.Path;
            }

            SyncToolbarSort();
            if (!scanning) RebuildTree();
            header.Invalidate();
        }

        private SortColumn HitTestHeader(int x)
        {
            // Convert a header x-coordinate into the matching logical column.
            if (x >= NameColumnX() && x < SizeColumnX()) return SortColumn.Name;
            if (x >= SizeColumnX() && x < FilesColumnX()) return SortColumn.Size;
            if (x >= FilesColumnX() && x < PercentColumnX()) return SortColumn.Files;
            if (x >= PercentColumnX() && x < PathColumnX()) return SortColumn.Percent;
            if (x >= PathColumnX()) return SortColumn.Path;
            return SortColumn.None;
        }

        private void ApplyToolbarSort()
        {
            // Keep the toolbar dropdown as an alternate way to choose sort order.
            switch (sortBox.SelectedIndex)
            {
                case 1: sortColumn = SortColumn.Size; sortDescending = false; break;
                case 2: sortColumn = SortColumn.Name; sortDescending = false; break;
                case 3: sortColumn = SortColumn.Name; sortDescending = true; break;
                case 4: sortColumn = SortColumn.Files; sortDescending = true; break;
                case 5: sortColumn = SortColumn.Files; sortDescending = false; break;
                case 6: sortColumn = SortColumn.Percent; sortDescending = true; break;
                case 7: sortColumn = SortColumn.Percent; sortDescending = false; break;
                case 8: sortColumn = SortColumn.Path; sortDescending = false; break;
                case 9: sortColumn = SortColumn.Path; sortDescending = true; break;
                default: sortColumn = SortColumn.Size; sortDescending = true; break;
            }
        }

        private void SyncToolbarSort()
        {
            // Header clicks update the toolbar dropdown so both controls stay in agreement.
            int index = 0;
            if (sortColumn == SortColumn.Size) index = sortDescending ? 0 : 1;
            else if (sortColumn == SortColumn.Name) index = sortDescending ? 3 : 2;
            else if (sortColumn == SortColumn.Files) index = sortDescending ? 4 : 5;
            else if (sortColumn == SortColumn.Percent) index = sortDescending ? 6 : 7;
            else if (sortColumn == SortColumn.Path) index = sortDescending ? 9 : 8;

            if (sortBox.SelectedIndex != index) sortBox.SelectedIndex = index;
        }

        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            // Owner-draw the entire row so the tree can behave like a multi-column disk usage table.
            var item = e.Node.Tag as DiskItem;
            if (item == null) return;

            var bounds = new Rectangle(0, e.Bounds.Top, tree.ClientSize.Width, tree.ItemHeight);
            bool selected = (e.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
            Color back = selected ? Color.FromArgb(214, 232, 255) : Color.White;
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, bounds);

            double percent = rootItem == null || rootItem.Size == 0 ? 0 : (double)item.Size / rootItem.Size;
            var barBounds = new Rectangle(PercentColumnX(), bounds.Top + 5, 118, bounds.Height - 10);
            // The yellow bar provides a quick visual comparison of space usage.
            using (var barBack = new SolidBrush(Color.FromArgb(239, 242, 246)))
            using (var barFill = new SolidBrush(Color.FromArgb(244, 197, 64)))
            {
                e.Graphics.FillRectangle(barBack, barBounds);
                e.Graphics.FillRectangle(barFill, barBounds.X, barBounds.Y, Math.Max(1, (int)(barBounds.Width * percent)), barBounds.Height);
            }

            Rectangle markerBounds = MarkerBounds(e.Node, bounds.Top);
            Rectangle iconBounds = IconBounds(e.Node, bounds.Top);
            int textLeft = iconBounds.Right + 4;

            // Name column: indentation guide, chevron, shell icon, then display name.
            DrawIndentGuides(e.Graphics, e.Node, bounds);

            string marker = RowMarker(e.Node, item);
            if (marker.Length > 0)
            {
                TextRenderer.DrawText(e.Graphics, marker, Font, markerBounds,
                    Color.FromArgb(34, 40, 49), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            DrawItemIcon(e.Graphics, iconBounds, item);

            TextRenderer.DrawText(e.Graphics, item.Name, Font, new Rectangle(textLeft, bounds.Top + 2, SizeColumnX() - textLeft - 4, bounds.Height - 4),
                Color.FromArgb(20, 24, 32), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, FormatSize(item.Size), Font, new Rectangle(SizeColumnX(), bounds.Top + 2, 88, bounds.Height - 4),
                Color.FromArgb(20, 24, 32), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, item.FileCount.ToString("N0"), Font, new Rectangle(FilesColumnX(), bounds.Top + 2, 72, bounds.Height - 4),
                Color.FromArgb(20, 24, 32), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, percent.ToString("P1", CultureInfo.CurrentCulture), Font, new Rectangle(PercentColumnX() + 124, bounds.Top + 2, 58, bounds.Height - 4),
                Color.FromArgb(20, 24, 32), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, item.Path, Font, new Rectangle(PathColumnX(), bounds.Top + 2, tree.ClientSize.Width - PathColumnX() - 6, bounds.Height - 4),
                Color.FromArgb(88, 96, 112), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            using (var pen = new Pen(Color.FromArgb(236, 240, 245)))
            {
                e.Graphics.DrawLine(pen, 0, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            }
        }

        private int NameColumnX() { return 8; }
        private int TreeIndent() { return 22; }
        // Column positions are calculated from the right so the table remains usable at smaller widths.
        private int SizeColumnX() { return Math.Max(420, tree.ClientSize.Width - 580); }
        private int FilesColumnX() { return Math.Max(520, tree.ClientSize.Width - 480); }
        private int PercentColumnX() { return Math.Max(620, tree.ClientSize.Width - 380); }
        private int PathColumnX() { return Math.Max(810, tree.ClientSize.Width - 185); }

        private string RowMarker(TreeNode node, DiskItem item)
        {
            // Use text chevrons instead of native TreeView glyphs for stable owner-drawn alignment.
            if (item.IsFile) return "";
            if (HasDiskChildren(item)) return node.IsExpanded ? "v" : ">";
            return "";
        }

        private Rectangle MarkerBounds(TreeNode node, int top)
        {
            // Marker position is based on level, not Windows label bounds, to avoid jumpy navigation.
            int x = NameColumnX() + (node.Level * TreeIndent());
            return new Rectangle(x, top + 2, 14, tree.ItemHeight - 4);
        }

        private Rectangle IconBounds(TreeNode node, int top)
        {
            // Icons sit just after the chevron and before the filename.
            int x = MarkerBounds(node, top).Right + 2;
            return new Rectangle(x, top + 5, 14, 14);
        }

        private void DrawIndentGuides(Graphics g, TreeNode node, Rectangle rowBounds)
        {
            // Faint vertical guides make nested folders easier to follow during deep navigation.
            if (node.Level <= 0) return;

            using (var pen = new Pen(Color.FromArgb(232, 236, 242)))
            {
                for (int level = 0; level < node.Level; level++)
                {
                    int x = NameColumnX() + (level * TreeIndent()) + 7;
                    g.DrawLine(pen, x, rowBounds.Top, x, rowBounds.Bottom);
                }
            }
        }

        private void DrawItemIcon(Graphics g, Rectangle bounds, DiskItem item)
        {
            // Prefer Explorer shell icons; fall back to a simple folder drawing if the shell call fails.
            Icon icon = GetShellIcon(item);
            if (icon != null)
            {
                g.DrawIcon(icon, bounds);
                return;
            }

            using (var tab = new SolidBrush(Color.FromArgb(255, 210, 86)))
            using (var body = new SolidBrush(Color.FromArgb(255, 190, 46)))
            using (var edge = new Pen(Color.FromArgb(218, 156, 30)))
            {
                g.FillRectangle(tab, bounds.X + 1, bounds.Y + 1, 6, 4);
                g.FillRectangle(body, bounds.X, bounds.Y + 4, bounds.Width, bounds.Height - 4);
                g.DrawRectangle(edge, bounds.X, bounds.Y + 4, bounds.Width - 1, bounds.Height - 5);
            }
        }

        private Icon GetShellIcon(DiskItem item)
        {
            // Request the small system icon by extension/folder type without touching file contents.
            string key = item.IsFile ? Path.GetExtension(item.Path) : "<folder>";
            if (string.IsNullOrEmpty(key)) key = item.IsFile ? "<file>" : "<folder>";

            Icon cached;
            if (iconCache.TryGetValue(key, out cached)) return cached;

            SHFILEINFO info = new SHFILEINFO();
            uint attributes = item.IsFile ? FILE_ATTRIBUTE_NORMAL : FILE_ATTRIBUTE_DIRECTORY;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
            IntPtr result = SHGetFileInfo(item.IsFile ? item.Path : "folder", attributes, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            Icon icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            iconCache[key] = icon;
            return icon;
        }

        private string FormatSize(long bytes)
        {
            // Convert raw bytes to the unit selected in the toolbar, or choose a readable automatic unit.
            string unit = unitBox.SelectedItem == null ? "Auto units" : unitBox.SelectedItem.ToString();
            if (unit == "Bytes") return bytes.ToString("N0") + " B";
            if (unit == "KB") return (bytes / 1024d).ToString("N1") + " KB";
            if (unit == "MB") return (bytes / 1048576d).ToString("N1") + " MB";
            if (unit == "GB") return (bytes / 1073741824d).ToString("N2") + " GB";

            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }
            return value.ToString(index == 0 ? "N0" : "N1") + " " + units[index];
        }

        private static string FriendlyName(string path)
        {
            // Show drive labels for roots, otherwise show the selected folder path.
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
                {
                    var drive = new DriveInfo(path);
                    return string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel + " (" + drive.Name.TrimEnd('\\') + ")";
                }
            }
            catch { }
            return path.TrimEnd('\\');
        }

        private DiskItem SelectedItem()
        {
            // Convenience accessor for actions that operate on the selected row.
            return tree.SelectedNode == null ? null : tree.SelectedNode.Tag as DiskItem;
        }

        private void OpenSelectedInExplorer()
        {
            // Open folders directly and select files when possible.
            var item = SelectedItem();
            if (item == null) return;
            try
            {
                if (item.IsFile)
                    Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
                else
                    Process.Start("explorer.exe", "\"" + item.Path + "\"");
            }
            catch (Exception ex)
            {
                statusText.Text = "Explorer is not available: " + ex.Message;
            }
        }

        private void CopySelectedPath()
        {
            // Copy the full filesystem path for quick pasting into tools or tickets.
            var item = SelectedItem();
            if (item != null) Clipboard.SetText(item.Path);
        }

        private void RescanSelected()
        {
            // Start a new scan at the selected folder branch.
            var item = SelectedItem();
            if (item != null && !item.IsFile) StartScan(item.Path);
        }

        private void ExportJson(DiskItem item, string suggestedName)
        {
            // Export only stable completed data; the scanner mutates the hierarchy while running.
            if (scanning)
            {
                MessageBox.Show(this, "Please wait for the scan to finish before exporting JSON.", "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (item == null)
            {
                MessageBox.Show(this, "There is no completed scan to export.", "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "JSON files (*.json)|*.json";
                dialog.FileName = suggestedName;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string outputPath = dialog.FileName;
                statusText.Text = "Exporting JSON...";

                var exportThread = new Thread(delegate()
                {
                    try
                    {
                        var document = new ScanExport
                        {
                            GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                            Root = item
                        };

                        // Raise the graph limit because full-drive scans can contain hundreds of thousands of items.
                        var serializer = new DataContractJsonSerializer(typeof(ScanExport), null, int.MaxValue, false, null, false);
                        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            serializer.WriteObject(stream, document);
                        }

                        BeginInvoke((MethodInvoker)delegate { statusText.Text = "Exported " + outputPath; });
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            statusText.Text = "JSON export failed";
                            MessageBox.Show(this, ex.Message, "JSON export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        });
                    }
                });
                exportThread.IsBackground = true;
                exportThread.Priority = ThreadPriority.BelowNormal;
                exportThread.Start();
            }
        }

        private void ExportSelectedCsv()
        {
            // Export the selected subtree for reporting or offline analysis.
            var item = SelectedItem();
            if (item == null) return;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "CSV files (*.csv)|*.csv";
                dialog.FileName = "TreeMeasure-export.csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                using (var writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8))
                {
                    writer.WriteLine("Path,SizeBytes,Files,Folders,Skipped");
                    WriteCsv(writer, item);
                }
                statusText.Text = "Exported " + dialog.FileName;
            }
        }

        private void WriteCsv(StreamWriter writer, DiskItem item)
        {
            // Depth-first export preserves the tree relationship in path order.
            writer.WriteLine("\"" + item.Path.Replace("\"", "\"\"") + "\"," + item.Size + "," + item.FileCount + "," + item.FolderCount + "," + item.SkippedCount);
            foreach (var child in item.Children) WriteCsv(writer, child);
        }

        private void IncrementFile()
        {
            // Progress counters are lock-protected because the scanner thread updates them.
            lock (progressLock) scannedFiles++;
        }

        private void IncrementFolder()
        {
            // See IncrementFile.
            lock (progressLock) scannedFolders++;
        }

        private void IncrementSkipped()
        {
            // See IncrementFile.
            lock (progressLock) scanSkipped++;
        }

        private void UpdateProgressStatus()
        {
            // Timer callback: apply a small UI batch and refresh the status text.
            if (!scanning) return;
            ApplyLiveUpdates(500);
            long files;
            long folders;
            int skipped;
            lock (progressLock)
            {
                files = scannedFiles;
                folders = scannedFolders;
                skipped = scanSkipped;
            }
            scanText.Text = "Scanning: " + files.ToString("N0") + " files, " + folders.ToString("N0") + " folders, " + skipped.ToString("N0") + " skipped";
        }

        // Shell icon constants used by SHGetFileInfo.
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }

    internal sealed class StartDialog : Form
    {
        // Startup dialog for choosing a ready drive or entering a local or UNC path.
        private readonly ComboBox locationBox = new ComboBox();
        private readonly TextBox manualPath = new TextBox();
        public string SelectedPath { get; private set; }

        public StartDialog()
        {
            // Keep the dialog plain and dependable for remote/backstage environments.
            Text = "Select a volume, folder, or network share";
            Width = 520;
            Height = 215;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            var label = new Label { Left = 14, Top = 16, Width = 470, Text = "Choose a local drive, mapped network drive, folder, or UNC share." };
            locationBox.Left = 14;
            locationBox.Top = 44;
            locationBox.Width = 392;
            locationBox.DropDownStyle = ComboBoxStyle.DropDownList;

            try
            {
                // Enumerate only ready drives; some removable/network drives can throw while inspected.
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady)
                        {
                            string labelText = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel + " (" + drive.Name + ")";
                            locationBox.Items.Add(new PathChoice(labelText, drive.RootDirectory.FullName));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (Directory.Exists(@"C:\"))
            {
                // Always offer C:\ when available, even if drive enumeration was limited.
                bool hasC = false;
                foreach (object item in locationBox.Items)
                {
                    var choice = item as PathChoice;
                    if (choice != null && string.Equals(choice.Path, @"C:\", StringComparison.OrdinalIgnoreCase))
                    {
                        hasC = true;
                        break;
                    }
                }
                if (!hasC) locationBox.Items.Insert(0, new PathChoice(@"C:\", @"C:\"));
            }
            if (locationBox.Items.Count > 0) locationBox.SelectedIndex = 0;

            var browse = new Button { Left = 414, Top = 43, Width = 78, Height = 27, Text = "Browse" };
            browse.Click += delegate
            {
                // FolderBrowserDialog gives a normal desktop user a quick folder picker.
                using (var folder = new FolderBrowserDialog())
                {
                    folder.Description = "Select a folder to scan";
                    if (folder.ShowDialog(this) == DialogResult.OK)
                    {
                        var choice = new PathChoice(folder.SelectedPath, folder.SelectedPath);
                        locationBox.Items.Add(choice);
                        locationBox.SelectedItem = choice;
                        manualPath.Text = folder.SelectedPath;
                    }
                }
            };

            var manualLabel = new Label { Left = 14, Top = 82, Width = 470, Text = @"Folder or UNC path (example: \\server\share):" };
            manualPath.Left = 14;
            manualPath.Top = 105;
            manualPath.Width = 478;
            manualPath.Text = locationBox.SelectedItem == null ? @"C:\" : ((PathChoice)locationBox.SelectedItem).Path;
            locationBox.SelectedIndexChanged += delegate
            {
                // Keep the manual path editable but synchronized with drive choices.
                var choice = locationBox.SelectedItem as PathChoice;
                if (choice != null) manualPath.Text = choice.Path;
            };

            var scan = new Button { Left = 316, Top = 145, Width = 84, Height = 28, Text = "Scan", DialogResult = DialogResult.OK };
            var cancel = new Button { Left = 408, Top = 145, Width = 84, Height = 28, Text = "Cancel", DialogResult = DialogResult.Cancel };
            scan.Click += delegate
            {
                // Validate and normalize local or UNC input before closing.
                string path = manualPath.Text.Trim();
                try
                {
                    path = new DirectoryInfo(path).FullName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "That folder path is invalid:\r\n\r\n" + ex.Message, "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }

                if (!Directory.Exists(path))
                {
                    MessageBox.Show(this, "That folder could not be reached. For a network share, verify the UNC path and the current Windows account's permissions.", "TreeMeasure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                SelectedPath = path;
            };

            AcceptButton = scan;
            CancelButton = cancel;
            Controls.Add(label);
            Controls.Add(locationBox);
            Controls.Add(browse);
            Controls.Add(manualLabel);
            Controls.Add(manualPath);
            Controls.Add(scan);
            Controls.Add(cancel);
        }

        private sealed class PathChoice
        {
            // ComboBox item wrapper: display label plus actual filesystem path.
            public readonly string Label;
            public readonly string Path;

            public PathChoice(string label, string path)
            {
                Label = label;
                Path = path;
            }

            public override string ToString()
            {
                return Label;
            }
        }
    }

}
