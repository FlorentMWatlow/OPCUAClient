using Opc.Ua;
using OpcUaClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpcUaClientTestApp
{
    public partial class Form1 : Form
    {
        private readonly OpcUaClient.OpcUaClient _client;
        private readonly BindingSource _bs = new BindingSource();

        public Form1()
        {
            InitializeComponent();

            _client = new OpcUaClient.OpcUaClient("OpcUaClientTestApp");

            _bs.DataSource = _client.Attributes;
            dataGridView1.DataSource = _bs;

            dataGridView1.AutoGenerateColumns = true;
            dataGridView1.ReadOnly = true;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.MultiSelect = false;

            cboSecurity.SelectedIndex = 0;
            txtEndpoint.Text = "opc.tcp://DESMASTERDEV:48031";
            chkAllowUntrustedCertificates.Checked = false;

            _client.AttributeUpdated += Client_AttributeUpdated;
            _client.ConnectionStateChanged += Client_ConnectionStateChanged;
            _client.NodeCacheStateChanged += Client_NodeCacheStateChanged;

            treeViewBrowser.BeforeExpand += TreeViewBrowser_BeforeExpand;
            treeViewBrowser.AfterSelect += TreeViewBrowser_AfterSelect;

            SetBrowserEnabled(false);
            UpdateNodeCacheUiState();
        }

        private void Client_ConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Client_ConnectionStateChanged(sender, e)));
                return;
            }

            lblStatus.Text = e.State.ToString();
            SetBrowserEnabled(e.State == OpcUaConnectionState.Connected);
            UpdateNodeCacheUiState();
        }

        private void Client_NodeCacheStateChanged(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Client_NodeCacheStateChanged(sender, e)));
                return;
            }

            UpdateNodeCacheUiState();
        }

        private void Client_AttributeUpdated(object sender, AttributeUpdatedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Client_AttributeUpdated(sender, e)));
                return;
            }

            var item = e.Attribute;
            var idx = _client.Attributes.IndexOf(item);
            if (idx >= 0)
                _bs.ResetItem(idx);
            else
                _bs.ResetBindings(false);
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                _client.Connect(
                    txtEndpoint.Text.Trim(),
                    txtUserName.Text.Trim(),
                    txtPassword.Text,
                    cboSecurity.SelectedIndex == 1,
                    chkAllowUntrustedCertificates.Checked);

                lblStatus.Text = _client.ConnectionState.ToString();
                LoadBrowserRoot();
                UpdateNodeCacheUiState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Connect error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            _client.Disconnect();
            lblStatus.Text = _client.ConnectionState.ToString();
            ClearBrowser();
            UpdateNodeCacheUiState();
        }

        private void btnAdd_Click(object sender, EventArgs e)
        {
            var nodeId = txtNodeId.Text.Trim();
            if (string.IsNullOrWhiteSpace(nodeId))
                return;

            try
            {
                _client.AddItem(nodeId);
                _bs.ResetBindings(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Add error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void btnAddByName_Click(object sender, EventArgs e)
        {
            var nodeName = txtNodeName.Text.Trim();
            if (string.IsNullOrWhiteSpace(nodeName))
                return;

            btnAddByName.Enabled = false;

            try
            {
                string resolvedNodeId = null;
                string errorMessage = null;

                var success = await Task.Run(() =>
                    _client.TryAddItemByName(nodeName, out resolvedNodeId, out errorMessage));

                if (success)
                {
                    txtNodeId.Text = resolvedNodeId;
                    _bs.ResetBindings(false);
                }
                else
                {
                    MessageBox.Show(errorMessage, "Add by name error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Add by name error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateNodeCacheUiState();
            }
        }

        private void btnRemove_Click(object sender, EventArgs e)
        {
            if (dataGridView1.CurrentRow == null)
                return;

            var item = dataGridView1.CurrentRow.DataBoundItem as aaAttribute;
            if (item == null)
                return;

            _client.RemoveItem(item.NodeId);
            _bs.ResetBindings(false);
        }

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (dataGridView1.CurrentRow == null)
                return;

            var item = dataGridView1.CurrentRow.DataBoundItem as aaAttribute;
            if (item == null)
                return;

            try
            {
                _client.ReadItem(item.NodeId);
                _bs.ResetBindings(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Read error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnWrite_Click(object sender, EventArgs e)
        {
            if (dataGridView1.CurrentRow == null)
                return;

            var item = dataGridView1.CurrentRow.DataBoundItem as aaAttribute;
            if (item == null)
                return;

            try
            {
                _client.WriteItem(item.NodeId, txtWriteValue.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Write error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            _bs.ResetBindings(false);
        }

        private void LoadBrowserRoot()
        {
            ClearBrowser();

            if (!_client.IsConnected)
                return;

            var root = new TreeNode("Objects")
            {
                Tag = string.Empty
            };
            root.Nodes.Add(new TreeNode("Loading..."));
            treeViewBrowser.Nodes.Add(root);
            root.Expand();
            treeViewBrowser.SelectedNode = root;
        }

        private void ClearBrowser()
        {
            treeViewBrowser.Nodes.Clear();
            txtNodeId.Text = string.Empty;
        }

        private void TreeViewBrowser_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node != null && e.Node.Tag is string)
                txtNodeId.Text = (string)e.Node.Tag;
        }

        private void TreeViewBrowser_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (!_client.IsConnected)
                return;

            if (e.Node.Nodes.Count == 1 && string.Equals(e.Node.Nodes[0].Text, "Loading...", StringComparison.Ordinal))
            {
                LoadChildren(e.Node);
            }
        }

        private void LoadChildren(TreeNode parentNode)
        {
            parentNode.Nodes.Clear();

            string nodeId = parentNode.Tag as string;
            if (nodeId == null)
                nodeId = string.Empty;

            IReadOnlyList<ReferenceDescription> references;
            try
            {
                references = _client.BrowseChildren(nodeId);
            }
            catch (Exception ex)
            {
                parentNode.Nodes.Add(new TreeNode("Error: " + ex.Message));
                return;
            }

            foreach (var reference in references)
            {
                var childNodeId = ToNodeIdText(reference);
                if (string.IsNullOrWhiteSpace(childNodeId))
                    continue;

                var text = reference.DisplayName != null && !string.IsNullOrWhiteSpace(reference.DisplayName.Text)
                    ? reference.DisplayName.Text
                    : reference.BrowseName.Name;

                if (string.IsNullOrWhiteSpace(text))
                    text = childNodeId;

                var childNode = new TreeNode(text)
                {
                    Tag = childNodeId,
                    ToolTipText = childNodeId
                };

                if (CanHaveChildren(reference.NodeClass))
                    childNode.Nodes.Add(new TreeNode("Loading..."));

                parentNode.Nodes.Add(childNode);
            }
        }

        private static bool CanHaveChildren(NodeClass nodeClass)
        {
            return nodeClass == NodeClass.Object
                || nodeClass == NodeClass.Variable
                || nodeClass == NodeClass.ObjectType
                || nodeClass == NodeClass.VariableType
                || nodeClass == NodeClass.View;
        }

        private static string ToNodeIdText(ReferenceDescription reference)
        {
            return reference.NodeId.ToString();
        }

        private void SetBrowserEnabled(bool enabled)
        {
            treeViewBrowser.Enabled = enabled;
            btnAdd.Enabled = enabled;
            btnRemove.Enabled = enabled;
            btnRead.Enabled = enabled;
            btnWrite.Enabled = enabled;
            btnRefresh.Enabled = enabled;
            txtNodeId.Enabled = enabled;
        }

        private void UpdateNodeCacheUiState()
        {
            if (_client.IsNodeCacheBuilding)
            {
                btnAddByName.Enabled = false;
                txtNodeName.Enabled = false;
                lblStatus.Text = "Connected - indexing OPC nodes...";
            }
            else if (_client.IsNodeCacheReady)
            {
                btnAddByName.Enabled = _client.IsConnected;
                txtNodeName.Enabled = _client.IsConnected;
                lblStatus.Text = "Connected - node cache ready";
            }
            else
            {
                btnAddByName.Enabled = false;
                txtNodeName.Enabled = false;

                if (_client.IsConnected)
                    lblStatus.Text = "Connected";
                else
                    lblStatus.Text = "Disconnected";
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _client.Dispose();
            base.OnFormClosed(e);
        }
    }
}
