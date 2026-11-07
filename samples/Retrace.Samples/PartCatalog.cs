using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// A small parts tree for the tree samples (DRAWPART's dialog and the palette's Parts page). Some nodes are filled when they are first opened
    /// (BeforeExpand), the way a tree that reads a database or a folder does: a node that is not filled yet holds one placeholder child, so that
    /// it shows a +. A replay has to open such a node before the next level exists.
    /// </summary>
    internal static class PartCatalog
    {
        private const string NotFilled = "not filled";
        private const string Placeholder = "(loading)";

        /// <summary>What a lazily filled node gets when it is opened, by the node's text.</summary>
        private static readonly Dictionary<string, string[]> Children = new()
        {
            ["Fasteners"] = new[] { "Bolts", "Nuts", "Washers" },
            ["Bolts"] = new[] { "M6", "M8", "M10" },
            ["Frames"] = new[] { "Aluminium", "Steel" },
        };

        /// <summary>Fills <paramref name="tree"/>: Parts (Fasteners, Frames: filled on opening; Bolts under Fasteners too) and Tools (Hammer, Wrench).</summary>
        public static void Fill(TreeView tree)
        {
            tree.BeforeExpand += OnBeforeExpand;
            tree.AfterCollapse += OnAfterCollapse;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            var parts = tree.Nodes.Add("Parts");
            parts.Nodes.Add(NotFilledNode("Fasteners"));
            parts.Nodes.Add(NotFilledNode("Frames"));
            var tools = tree.Nodes.Add("Tools");
            tools.Nodes.Add("Hammer");
            tools.Nodes.Add("Wrench");
            tree.EndUpdate();
        }

        private static TreeNode NotFilledNode(string text)
        {
            var node = new TreeNode(text) { Tag = NotFilled };
            node.Nodes.Add(Placeholder);
            return node;
        }

        /// <summary>Frames is read again each time it is opened (as a folder listing would be): collapsing it drops its children and shows the placeholder.</summary>
        private static void OnAfterCollapse(object? sender, TreeViewEventArgs e)
        {
            var node = e.Node;
            if (node == null || node.Text != "Frames") return;
            node.Nodes.Clear();
            node.Nodes.Add(Placeholder);
            node.Tag = NotFilled;
        }

        private static void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node == null || !Equals(node.Tag, NotFilled)) return;
            node.Tag = null;
            node.Nodes.Clear();
            if (!Children.TryGetValue(node.Text, out var names)) return;
            foreach (var name in names) node.Nodes.Add(Children.ContainsKey(name) ? NotFilledNode(name) : new TreeNode(name));
        }
    }
}
