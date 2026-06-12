using System;
using System.ComponentModel;

namespace OpcUaClient
{
    public class aaAttribute : INotifyPropertyChanged
    {
        private object _value;
        public object Value
        {
            get { return _value; }
            set
            {
                if (!Equals(_value, value))
                {
                    _value = value;
                    OnPropertyChanged("Value");
                }
            }
        }

        private string _quality = string.Empty;
        public string Quality
        {
            get { return _quality; }
            set
            {
                if (!string.Equals(_quality, value, StringComparison.Ordinal))
                {
                    _quality = value ?? string.Empty;
                    OnPropertyChanged("Quality");
                }
            }
        }

        private string _timeStamp = string.Empty;
        public string TimeStamp
        {
            get { return _timeStamp; }
            set
            {
                if (!string.Equals(_timeStamp, value, StringComparison.Ordinal))
                {
                    _timeStamp = value ?? string.Empty;
                    OnPropertyChanged("TimeStamp");
                }
            }
        }

        public int ItemHandle { get; set; }
        public string TagName { get; set; }
        public string NodeId { get; set; }

        public aaAttribute()
        {
            TagName = string.Empty;
            NodeId = string.Empty;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
