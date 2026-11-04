using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ChatCore;
using System.Collections.ObjectModel;

namespace ChatClient.Cross
{
    public partial class MainView : UserControl
    {
        private readonly ChatSession _session = new ChatSession();
        private readonly ObservableCollection<ChatLine> _lines = new ObservableCollection<ChatLine>();

        public MainView()
        {
            InitializeComponent();
            Messages.ItemsSource = _lines;
            _session.StatusChanged += text => Dispatcher.UIThread.Post(() => StatusText.Text = text);
            _session.LineAdded += line => Dispatcher.UIThread.Post(() => _lines.Add(line));
        }

        private async void Connect_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                FindButton.IsEnabled = false;
                await _session.ConnectAsync(HubUrl.Text?.Trim());
                FindButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void Find_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (FindButton.Content?.ToString() == "Выйти")
            {
                await _session.LeaveAsync();
                FindButton.Content = "Найти";
                return;
            }

            await _session.FindAsync();
            FindButton.Content = "Выйти";
        }

        private async void Send_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await SendCurrent();
        }

        private async void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendCurrent();
            }
        }

        private async Task SendCurrent()
        {
            string text = Input.Text;
            Input.Text = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                await _session.SendAsync(text);
            }
            catch (Exception ex)
            {
                _lines.Add(new ChatLine { Text = ex.Message, IsSystem = true });
            }
        }
    }
}
