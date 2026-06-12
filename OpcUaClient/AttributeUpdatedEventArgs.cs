using System;

namespace OpcUaClient
{
    public sealed class AttributeUpdatedEventArgs : EventArgs
    {
        public aaAttribute Attribute { get; private set; }

        public AttributeUpdatedEventArgs(aaAttribute attribute)
        {
            if (attribute == null)
                throw new ArgumentNullException("attribute");

            Attribute = attribute;
        }
    }
}
