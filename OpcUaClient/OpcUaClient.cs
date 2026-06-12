using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace OpcUaClient
{
    public sealed class OpcUaClient : IDisposable
    {
        private readonly BindingList<aaAttribute> _attributes = new BindingList<aaAttribute>();
        private readonly Dictionary<string, MonitoredItem> _monitoredItemsByNodeId = new Dictionary<string, MonitoredItem>(StringComparer.OrdinalIgnoreCase);
        private readonly SynchronizationContext _syncContext;
        private readonly object _syncRoot = new object();
        private Session _session;
        private Subscription _subscription;
        private bool _disposed;
        private readonly string _applicationName;
        private bool _allowUntrustedCertificates;

        public BindingList<aaAttribute> Attributes
        {
            get { return _attributes; }
        }

        public OpcUaConnectionState ConnectionState { get; private set; }
        public int AttributeCount { get { lock (_syncRoot) return _attributes.Count; } }
        public bool IsConnected { get { return _session != null && _session.Connected; } }
        public string LastError { get; private set; }
        public bool AllowUntrustedCertificates { get { return _allowUntrustedCertificates; } }

        public event EventHandler<AttributeUpdatedEventArgs> AttributeUpdated;
        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        public OpcUaClient(string applicationName)
        {
            _applicationName = string.IsNullOrWhiteSpace(applicationName) ? "OpcUaClient" : applicationName;
            _syncContext = SynchronizationContext.Current;
            LastError = string.Empty;
            ConnectionState = OpcUaConnectionState.Disconnected;
        }

        public OpcUaClient() : this("OpcUaClient")
        {
        }

        public void Connect(string endpointUrl, string userName, string password, bool useSecurity)
        {
            Connect(endpointUrl, userName, password, useSecurity, false);
        }

        public void Connect(string endpointUrl, string userName, string password, bool useSecurity, bool allowUntrustedCertificates)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(endpointUrl))
                throw new ArgumentException("Endpoint URL is required.", nameof(endpointUrl));

            Disconnect();
            SetConnectionState(OpcUaConnectionState.Connecting);

            try
            {
                _allowUntrustedCertificates = allowUntrustedCertificates;
                var configuration = CreateConfiguration(_applicationName, allowUntrustedCertificates);

                var selectedEndpoint = CoreClientUtils.SelectEndpoint(configuration, endpointUrl, useSecurity, 15000);
                var endpointConfiguration = EndpointConfiguration.Create(configuration);
                var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                IUserIdentity identity;
                if (string.IsNullOrWhiteSpace(userName))
                {
                    identity = new UserIdentity();
                }
                else
                {
                    identity = new UserIdentity(userName, Encoding.UTF8.GetBytes(password ?? string.Empty));
                }

                _session = Session.Create(
                    configuration,
                    configuredEndpoint,
                    false,
                    _applicationName,
                    60000,
                    identity,
                    null).GetAwaiter().GetResult();

                _subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingInterval = 1000,
                    LifetimeCount = 60,
                    KeepAliveCount = 10,
                    MaxNotificationsPerPublish = 1000,
                    PublishingEnabled = true,
                    Priority = 0
                };

                _session.AddSubscription(_subscription);
                _subscription.Create();

                SetConnectionState(OpcUaConnectionState.Connected);
                LastError = string.Empty;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetConnectionState(OpcUaConnectionState.Error);
                throw;
            }
        }

        public void Disconnect()
        {
            Session sessionToDispose = null;
            Subscription subscriptionToDispose = null;
            List<MonitoredItem> monitoredItems;

            lock (_syncRoot)
            {
                monitoredItems = _monitoredItemsByNodeId.Values.ToList();
                subscriptionToDispose = _subscription;
                sessionToDispose = _session;

                foreach (var item in monitoredItems)
                    item.Notification -= MonitoredItem_Notification;

                _monitoredItemsByNodeId.Clear();
                _subscription = null;
                _session = null;
                _attributes.Clear();
            }

            try
            {
                if (subscriptionToDispose != null)
                {
                    foreach (var item in monitoredItems)
                        subscriptionToDispose.RemoveItem(item);

                    subscriptionToDispose.ApplyChanges();
                    subscriptionToDispose.Delete(true);
                }
            }
            catch
            {
            }

            try
            {
                if (sessionToDispose != null && subscriptionToDispose != null)
                    sessionToDispose.RemoveSubscription(subscriptionToDispose);
            }
            catch
            {
            }

            try
            {
                if (sessionToDispose != null)
                {
                    sessionToDispose.Close();
                    sessionToDispose.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                SetConnectionState(OpcUaConnectionState.Disconnected);
            }
        }

        public IList<ReferenceDescription> BrowseChildren(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            NodeId startNodeId;
            if (string.IsNullOrWhiteSpace(nodeIdText))
                startNodeId = ObjectIds.ObjectsFolder;
            else
                startNodeId = NodeId.Parse(nodeIdText);

            var browser = new Browser(_session)
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (int)NodeClass.Object | (int)NodeClass.Variable,
                ResultMask = (uint)BrowseResultMask.All
            };

            return browser.Browse(startNodeId).Cast<ReferenceDescription>().ToList();
        }

        public void AddItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                return;

            lock (_syncRoot)
            {
                if (_attributes.Any(x => string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase)))
                    return;
            }

            if (_subscription == null)
                throw new InvalidOperationException("No active subscription.");

            var nodeId = NodeId.Parse(nodeIdText);
            var attribute = new aaAttribute
            {
                ItemHandle = Environment.TickCount,
                TagName = nodeIdText,
                NodeId = nodeIdText
            };

            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                StartNodeId = nodeId,
                DisplayName = nodeIdText,
                SamplingInterval = 1000,
                QueueSize = 1,
                DiscardOldest = true,
                AttributeId = Opc.Ua.Attributes.Value,
                Handle = nodeIdText
            };

            monitoredItem.Notification += MonitoredItem_Notification;

            lock (_syncRoot)
            {
                _subscription.AddItem(monitoredItem);
                _subscription.ApplyChanges();

                _monitoredItemsByNodeId[nodeIdText] = monitoredItem;
                _attributes.Add(attribute);
            }
        }

        public bool TryAddItemByName(string nodeName, out string resolvedNodeId, out string errorMessage)
        {
            ThrowIfDisposed();
            EnsureConnected();

            resolvedNodeId = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(nodeName))
            {
                errorMessage = "Node name is required.";
                return false;
            }

            var matches = FindMatchingNodesByName(nodeName.Trim());
            if (matches.Count == 0)
            {
                errorMessage = "No OPC UA node found with name '" + nodeName + "'.";
                return false;
            }

            if (matches.Count > 1)
            {
                errorMessage = "Multiple OPC UA nodes found with name '" + nodeName + "': " + string.Join(", ", matches.Select(x => x.NodeIdText));
                return false;
            }

            resolvedNodeId = matches[0].NodeIdText;
            AddItem(resolvedNodeId);
            return true;
        }

        public void RemoveItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                return;

            aaAttribute attribute;
            MonitoredItem monitoredItem = null;

            lock (_syncRoot)
            {
                attribute = _attributes.FirstOrDefault(x =>
                    string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.TagName, nodeIdText, StringComparison.OrdinalIgnoreCase));

                if (attribute == null)
                    return;

                if (_subscription != null && _monitoredItemsByNodeId.ContainsKey(attribute.NodeId))
                {
                    monitoredItem = _monitoredItemsByNodeId[attribute.NodeId];
                    monitoredItem.Notification -= MonitoredItem_Notification;
                    _subscription.RemoveItem(monitoredItem);
                    _subscription.ApplyChanges();
                    _monitoredItemsByNodeId.Remove(attribute.NodeId);
                }

                _attributes.Remove(attribute);
            }
        }

        public aaAttribute ReadItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                throw new ArgumentException("NodeId is required.", nameof(nodeIdText));

            var dataValue = _session.ReadValue(NodeId.Parse(nodeIdText));
            if (StatusCode.IsBad(dataValue.StatusCode))
                throw new ServiceResultException(dataValue.StatusCode, "Read failed for node '" + nodeIdText + "'.");

            aaAttribute attribute;
            lock (_syncRoot)
            {
                attribute = _attributes.FirstOrDefault(x =>
                    string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase));
            }

            if (attribute == null)
            {
                attribute = new aaAttribute
                {
                    ItemHandle = 0,
                    TagName = nodeIdText,
                    NodeId = nodeIdText
                };
            }

            ApplyValue(attribute, dataValue.Value, dataValue.StatusCode.ToString(), FormatTimestamp(dataValue.SourceTimestamp));
            return attribute;
        }

        public void WriteItem(string nodeIdText, object value)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                throw new ArgumentException("NodeId is required.", nameof(nodeIdText));

            var writeValues = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = NodeId.Parse(nodeIdText),
                    AttributeId = Opc.Ua.Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };

            StatusCodeCollection results;
            DiagnosticInfoCollection diagnosticInfos;
            _session.Write(null, writeValues, out results, out diagnosticInfos);

            if (results == null || results.Count == 0)
                throw new ServiceResultException(StatusCodes.BadUnexpectedError, "Write did not return a status code.");

            if (StatusCode.IsBad(results[0]))
                throw new ServiceResultException(results[0], "Write failed for node '" + nodeIdText + "'.");
        }

        private List<NodeMatch> FindMatchingNodesByName(string nodeName)
        {
            var matches = new List<NodeMatch>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FindMatchingNodesByNameRecursive(ObjectIds.ObjectsFolder, nodeName, matches, visited);
            return matches;
        }

        private void FindMatchingNodesByNameRecursive(NodeId parentNodeId, string nodeName, List<NodeMatch> matches, HashSet<string> visited)
        {
            var parentKey = parentNodeId.ToString();
            if (!visited.Add(parentKey))
                return;

            var children = BrowseChildren(parentKey);
            foreach (var reference in children)
            {
                var childNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, _session.NamespaceUris);
                if (childNodeId == null)
                    continue;

                var displayName = reference.DisplayName != null ? reference.DisplayName.Text : string.Empty;
                var browseName = reference.BrowseName != null ? reference.BrowseName.Name : string.Empty;
                var childNodeIdText = childNodeId.ToString();

                if (string.Equals(displayName, nodeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(browseName, nodeName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new NodeMatch
                    {
                        NodeIdText = childNodeIdText,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? browseName : displayName
                    });
                }

                if (CanHaveChildren(reference.NodeClass))
                    FindMatchingNodesByNameRecursive(childNodeId, nodeName, matches, visited);
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

        private void MonitoredItem_Notification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            var nodeIdText = monitoredItem.StartNodeId != null ? monitoredItem.StartNodeId.ToString() : monitoredItem.DisplayName;
            aaAttribute attribute;

            lock (_syncRoot)
            {
                attribute = _attributes.FirstOrDefault(x => string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase));
            }

            if (attribute == null)
                return;

            var lastValue = monitoredItem.DequeueValues().LastOrDefault();
            if (lastValue == null)
                return;

            ApplyValue(attribute, lastValue.Value, lastValue.StatusCode.ToString(), FormatTimestamp(lastValue.SourceTimestamp));
        }

        private void ApplyValue(aaAttribute attribute, object value, string quality, string timeStamp)
        {
            Action apply = delegate
            {
                attribute.Value = value;
                attribute.Quality = quality;
                attribute.TimeStamp = timeStamp;
                OnAttributeUpdated(attribute);
            };

            if (_syncContext != null)
                _syncContext.Post(delegate { apply(); }, null);
            else
                apply();
        }

        private void OnAttributeUpdated(aaAttribute attribute)
        {
            var handler = AttributeUpdated;
            if (handler != null)
                handler(this, new AttributeUpdatedEventArgs(attribute));
        }

        private static string FormatTimestamp(DateTime dt)
        {
            if (dt == DateTime.MinValue)
                return string.Empty;

            return dt.ToString("o", CultureInfo.InvariantCulture);
        }

        private static ApplicationConfiguration CreateConfiguration(string applicationName, bool allowUntrustedCertificates)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;

            var configuration = new ApplicationConfiguration
            {
                ApplicationName = applicationName,
                ApplicationUri = "urn:" + Utils.GetHostName() + ":" + applicationName,
                ApplicationType = ApplicationType.Client,

                SecurityConfiguration = new SecurityConfiguration
                {
                    AutoAcceptUntrustedCertificates = allowUntrustedCertificates,
                    RejectSHA1SignedCertificates = true,
                    MinimumCertificateKeySize = 2048,

                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = System.IO.Path.Combine(basePath, "pki", "own")
                    },

                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = System.IO.Path.Combine(basePath, "pki", "trusted")
                    },

                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = System.IO.Path.Combine(basePath, "pki", "issuers")
                    },

                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = System.IO.Path.Combine(basePath, "pki", "rejected")
                    }
                },

                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 15000,
                    SecurityTokenLifetime = 60000
                },

                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = 60000,
                    MinSubscriptionLifetime = 10000
                }
            };

            System.IO.Directory.CreateDirectory(configuration.SecurityConfiguration.ApplicationCertificate.StorePath);
            System.IO.Directory.CreateDirectory(configuration.SecurityConfiguration.TrustedPeerCertificates.StorePath);
            System.IO.Directory.CreateDirectory(configuration.SecurityConfiguration.TrustedIssuerCertificates.StorePath);
            System.IO.Directory.CreateDirectory(configuration.SecurityConfiguration.RejectedCertificateStore.StorePath);

            configuration.CertificateValidator.CertificateValidation += delegate(CertificateValidator sender, CertificateValidationEventArgs e)
            {
                e.Accept = allowUntrustedCertificates;
            };

            configuration.Validate(ApplicationType.Client).GetAwaiter().GetResult();
            return configuration;
        }

        private void SetConnectionState(OpcUaConnectionState state)
        {
            if (ConnectionState == state)
                return;

            ConnectionState = state;
            var handler = ConnectionStateChanged;
            if (handler == null)
                return;

            Action raise = delegate
            {
                handler(this, new ConnectionStateChangedEventArgs(state));
            };

            if (_syncContext != null)
                _syncContext.Post(delegate { raise(); }, null);
            else
                raise();
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to OPC UA server.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("OpcUaClient");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Disconnect();
            _disposed = true;
        }

        private sealed class NodeMatch
        {
            public string NodeIdText { get; set; }
            public string DisplayName { get; set; }
        }
    }
}
