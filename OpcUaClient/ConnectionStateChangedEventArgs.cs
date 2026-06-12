using System;

namespace OpcUaClient
{
    public sealed class ConnectionStateChangedEventArgs : EventArgs
    {
        public OpcUaConnectionState State { get; private set; }

        public ConnectionStateChangedEventArgs(OpcUaConnectionState state)
        {
            State = state;
        }
    }
}
