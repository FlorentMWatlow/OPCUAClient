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
        private Session _session;
        private Subscription _subscription;
        private bool _disposed;
        private readonly string _applicationName;

        public BindingList<aaAttribute> Attributes
        {
            get { return _attributes; }
        }

        public OpcUaConnectionState ConnectionState { get; private set; }
        public int AttributeCount { get { return _attributes.Count; } }
        public bool IsConnected { get { return _session != null && _session.Connected; } }
        public string LastError { get; private set; }

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
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(endpointUrl))
                throw new ArgumentException("Endpoint URL is required.", "endpointUrl");

            Disconnect();
            SetConnectionState(OpcUaConnectionState.Connecting);

            try
            {
                var configuration = CreateConfiguration(_applicationName);

                // Standard/basic endpoint selection for OPC UA .NET Standard 1.5.x
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
                    // Some SDK builds expose the password token as byte[]
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
            ThrowIfDisposed();

            try
            {
                if (_subscription != null && _session != null)
                {
                    foreach (var item in _monitoredItemsByNodeId.Values.ToList())
                        _subscription.RemoveItem(item);

                    _subscription.Delete(true);
                    _session.RemoveSubscription(_subscription);
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
            finally
            {
                _monitoredItemsByNodeId.Clear();
                _subscription = null;
                _attributes.Clear();

                if (_session != null)
                {
                    try
                    {
                        _session.Close();
                        _session.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup errors.
                    }
                }

                _session = null;
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
                //IgnoreLocalReferences = true
            };

            return browser.Browse(startNodeId).Cast<ReferenceDescription>().ToList();
        }

        public void AddItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                return;

            if (_attributes.Any(x => string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase)))
                return;

            if (_subscription == null)
                throw new InvalidOperationException("No active subscription.");

            var nodeId = NodeId.Parse(nodeIdText);
            var attribute = new aaAttribute
            {
                ItemHandle = _attributes.Count + 1,
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

            _subscription.AddItem(monitoredItem);
            _subscription.ApplyChanges();

            _monitoredItemsByNodeId[nodeIdText] = monitoredItem;
            _attributes.Add(attribute);
        }

        public void RemoveItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                return;

            var attribute = _attributes.FirstOrDefault(x =>
                string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.TagName, nodeIdText, StringComparison.OrdinalIgnoreCase));

            if (attribute == null)
                return;

            if (_subscription != null && _monitoredItemsByNodeId.ContainsKey(attribute.NodeId))
            {
                var monitoredItem = _monitoredItemsByNodeId[attribute.NodeId];
                _subscription.RemoveItem(monitoredItem);
                _subscription.ApplyChanges();
                _monitoredItemsByNodeId.Remove(attribute.NodeId);
            }

            _attributes.Remove(attribute);
        }

        public aaAttribute ReadItem(string nodeIdText)
        {
            ThrowIfDisposed();
            EnsureConnected();

            if (string.IsNullOrWhiteSpace(nodeIdText))
                throw new ArgumentException("NodeId is required.", "nodeIdText");

            var dataValue = _session.ReadValue(NodeId.Parse(nodeIdText));
            var attribute = _attributes.FirstOrDefault(x =>
                string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase));

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
                throw new ArgumentException("NodeId is required.", "nodeIdText");

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
        }

        private void MonitoredItem_Notification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            var nodeIdText = monitoredItem.StartNodeId != null ? monitoredItem.StartNodeId.ToString() : monitoredItem.DisplayName;
            var attribute = _attributes.FirstOrDefault(x => string.Equals(x.NodeId, nodeIdText, StringComparison.OrdinalIgnoreCase));
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

        private static ApplicationConfiguration CreateConfiguration(string applicationName)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;

            var configuration = new ApplicationConfiguration
            {
                ApplicationName = applicationName,
                ApplicationUri = "urn:" + Utils.GetHostName() + ":" + applicationName,
                ApplicationType = ApplicationType.Client,

                SecurityConfiguration = new SecurityConfiguration
                {
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024,

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

            configuration.CertificateValidator.CertificateValidation +=
                CertificateValidator_CertificateValidation;

            configuration.Validate(ApplicationType.Client).GetAwaiter().GetResult();
            return configuration;
        }

        private static void CertificateValidator_CertificateValidation(
            CertificateValidator sender,
            CertificateValidationEventArgs e)
        {
            e.Accept = true;
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
    }
}
