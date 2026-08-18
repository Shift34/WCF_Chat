using System;
using System.ComponentModel;
using System.Windows.Media;

namespace ChatClient
{
    public enum MessageType
    {
        Normal,     // Обычное сообщение
        System      // Системное сообщение (по центру)
    }
    public class MessageModel : INotifyPropertyChanged
    {
        private string _text;
        private bool _isOwnMessage;
        private MessageStatus _status = MessageStatus.Sent;
        private MessageType _type = MessageType.Normal;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged(nameof(Text));
            }
        }
        public DateTime Timestamp { get; set; }
        public bool IsOwnMessage
        {
            get => _isOwnMessage;
            set
            {
                _isOwnMessage = value;
                OnPropertyChanged(nameof(IsOwnMessage));
            }
        }

        public MessageType Type
        {
            get => _type;
            set
            {
                _type = value;
                OnPropertyChanged(nameof(Type));
                OnPropertyChanged(nameof(IsSystemMessage));
            }
        }

        public bool IsSystemMessage => Type == MessageType.System;
        public MessageStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusColor));
            }
        }

        public string Time => Timestamp.ToString("HH:mm");

        public string StatusIcon
        {
            get
            {
                switch (Status)
                {
                    case MessageStatus.Sent:
                        return "✓";
                    case MessageStatus.Delivered:
                        return "✓✓";
                    case MessageStatus.Read:
                        return "✓✓";
                    default:
                        return "✓";
                }
            }
        }

        public Brush StatusColor
        {
            get
            {
                return Status == MessageStatus.Read
                    ? new SolidColorBrush(Color.FromRgb(0, 150, 255)) // Синие галки
                    : new SolidColorBrush(Color.FromRgb(150, 150, 150)); // Серые галки
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum MessageStatus
    {
        Sent,       // ✓ - отправлено на сервер
        Delivered,  // ✓✓ - доставлено получателю
        Read        // ✓✓ (синие) - прочитано
    }
}
