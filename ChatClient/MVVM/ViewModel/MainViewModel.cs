using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ChatClient.ViewModel
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private string _statusText = "Готов к поиску";
        private string _findButtonText = "Найти собеседника";
        private bool _isChatVisible;
        private bool _isCallPanelVisible;
        private bool _isCallIdle = true;
        private bool _isCallActive;
        private bool _isIncomingCallVisible;
        private bool _isInputEnabled;
        private bool _isFindEnabled;
        private double _callPanelHeight = 60;
        private string _directLinkToolTip = string.Empty;
        private bool _showDirectLinkStatus;

        public ObservableCollection<MessageModel> Messages { get; } = new ObservableCollection<MessageModel>();

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string FindButtonText
        {
            get => _findButtonText;
            set { _findButtonText = value; OnPropertyChanged(nameof(FindButtonText)); }
        }

        public bool IsChatVisible
        {
            get => _isChatVisible;
            set { _isChatVisible = value; OnPropertyChanged(nameof(IsChatVisible)); }
        }

        public bool IsCallPanelVisible
        {
            get => _isCallPanelVisible;
            set { _isCallPanelVisible = value; OnPropertyChanged(nameof(IsCallPanelVisible)); }
        }

        public bool IsCallIdle
        {
            get => _isCallIdle;
            set { _isCallIdle = value; OnPropertyChanged(nameof(IsCallIdle)); }
        }

        public bool IsCallActive
        {
            get => _isCallActive;
            set { _isCallActive = value; OnPropertyChanged(nameof(IsCallActive)); }
        }

        public bool IsIncomingCallVisible
        {
            get => _isIncomingCallVisible;
            set { _isIncomingCallVisible = value; OnPropertyChanged(nameof(IsIncomingCallVisible)); }
        }

        public bool IsInputEnabled
        {
            get => _isInputEnabled;
            set { _isInputEnabled = value; OnPropertyChanged(nameof(IsInputEnabled)); }
        }

        public bool IsFindEnabled
        {
            get => _isFindEnabled;
            set { _isFindEnabled = value; OnPropertyChanged(nameof(IsFindEnabled)); }
        }

        public double CallPanelHeight
        {
            get => _callPanelHeight;
            set { _callPanelHeight = value; OnPropertyChanged(nameof(CallPanelHeight)); }
        }

        public string DirectLinkToolTip
        {
            get => _directLinkToolTip;
            set { _directLinkToolTip = value ?? string.Empty; OnPropertyChanged(nameof(DirectLinkToolTip)); }
        }

        public bool ShowDirectLinkStatus
        {
            get => _showDirectLinkStatus;
            set { _showDirectLinkStatus = value; OnPropertyChanged(nameof(ShowDirectLinkStatus)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void ResetToIdle()
        {
            StatusText = "Готов к поиску";
            FindButtonText = "Найти собеседника";
            IsChatVisible = false;
            IsCallPanelVisible = false;
            IsInputEnabled = false;
            IsIncomingCallVisible = false;
            ShowIdleCall();
            ClearDirectLinkStatus();
            Messages.Clear();
        }

        public void ShowSearching()
        {
            StatusText = "Ищем собеседника…";
            FindButtonText = "Отменить поиск";
        }

        public void ShowConnected()
        {
            StatusText = "Собеседник на связи";
            FindButtonText = "Отключиться";
            IsChatVisible = true;
            IsCallPanelVisible = false;
            IsInputEnabled = true;
            ShowIdleCall();
        }

        public void ShowPeerLeft()
        {
            StatusText = "Собеседник отключился";
            IsInputEnabled = false;
            IsCallPanelVisible = false;
            IsIncomingCallVisible = false;
            ShowIdleCall();
            ClearDirectLinkStatus();
        }

        public void ShowProtocolFailed()
        {
            StatusText = "Не удалось установить защищённый канал";
            FindButtonText = "Найти собеседника";
            IsChatVisible = false;
            IsCallPanelVisible = false;
            IsInputEnabled = false;
            IsIncomingCallVisible = false;
            ShowIdleCall();
            ClearDirectLinkStatus();
            Messages.Clear();
        }

        public void ShowIdleCall()
        {
            IsCallIdle = true;
            IsCallActive = false;
            IsCallPanelVisible = false;
            CallPanelHeight = 0;
        }

        public void ShowActiveCall()
        {
            IsCallIdle = false;
            IsCallActive = true;
            IsCallPanelVisible = true;
            CallPanelHeight = 0;
        }

        public void AddSystemMessage(string text)
        {
            Messages.Add(new MessageModel
            {
                Text = text,
                Timestamp = DateTime.Now,
                IsOwnMessage = false,
                Type = MessageType.System,
                Status = MessageStatus.Sent
            });
        }

        public MessageModel AddChatMessage(string text, bool isOwn, MessageStatus status)
        {
            var message = new MessageModel
            {
                Text = text,
                Timestamp = DateTime.Now,
                IsOwnMessage = isOwn,
                Type = MessageType.Normal,
                Status = status
            };
            Messages.Add(message);
            return message;
        }

        public void SetDirectLinkStatus(string toolTip, bool visible = true)
        {
            DirectLinkToolTip = toolTip ?? string.Empty;
            ShowDirectLinkStatus = visible && DirectLinkToolTip.Length > 0;
        }

        public void ClearDirectLinkStatus()
        {
            DirectLinkToolTip = string.Empty;
            ShowDirectLinkStatus = false;
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
