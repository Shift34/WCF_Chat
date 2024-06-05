using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ChatClient.ServiceChat;
namespace ChatClient
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    public partial class MainWindow : Window, IServiceChatCallback
    {
        ServiceChat.ServiceChatClient client;
        State state {get; set;}
        int ID = -1;
        int ID1 = -1;
        public MainWindow()
        {
            InitializeComponent();
            state = State.NoSearch;
            ListViewMessage.Visibility = Visibility.Hidden;
            TextBoxMessage.Visibility = Visibility.Hidden;
        }
        void DisconnectUser()
        {
            if (ID1 != -1)
            {
                client.Disconnect(ID, ID1);
                ID = -1;
                ID1 = -1;
            }
            else
            {
                ID = -1;
                if (client != null)
                {
                    client.RemoveUser(ID);
                }
            }
            client = null;
            LabelState.Content = "Состояние: Стандартное";
            Button1.Content = "Найти собеседника";
            ListViewMessage.Visibility = Visibility.Hidden;
            TextBoxMessage.Visibility = Visibility.Hidden;
            state = State.NoSearch;
            ListViewMessage.Items.Clear();
        }

        private void Find()
        {
            client = new ServiceChatClient(new System.ServiceModel.InstanceContext(this));
            (ID, ID1) = client.Connect();
            if (ID1 != -1)
            {
                LabelState.Content = "Состояние: Ваш собеседник найден";
                Button1.Content = "Отключиться";
                ListViewMessage.Visibility = Visibility.Visible;
                TextBoxMessage.Visibility = Visibility.Visible;
                state = State.Found;
                TextBoxMessage.IsEnabled = true;
            }
            else
            {
                LabelState.Content = "Состояние: Поиск собеседника";
                Button1.Content = "Отменить поиск";
                state = State.Search;
            }
        }

        public void MessageCallBack(string message, byte[] bytes = null)
        {
            string text = message;
            if (bytes != null)
            {
                byte[] key = ASCIIEncoding.UTF8.GetBytes("Ключ");
                RC4 decoder = new RC4(key);
                byte[] decryptedBytes = decoder.Decode(bytes, bytes.Length);
                string decryptedString = ASCIIEncoding.UTF8.GetString(decryptedBytes);
                text += decryptedString;
            }
            ListViewMessage.Items.Add(text);
            ListViewMessage.ScrollIntoView(ListViewMessage.Items[ListViewMessage.Items.Count - 1]);
        }


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DisconnectUser();
        }

        public void FoundByIp(int ID, int ID1)
        {
            this.ID = ID;
            this.ID1 = ID1;
            LabelState.Content = "Состояние: Ваш собеседник найден";
            Button1.Content = "Отключиться";
            TextBoxMessage.IsEnabled = true;
            ListViewMessage.Visibility = Visibility.Visible;
            TextBoxMessage.Visibility = Visibility.Visible;
            state = State.Found;
        }

        public void LeftChat()
        {
            LabelState.Content = "Состояние: Чат без собеседника";
            TextBoxMessage.IsEnabled = false;
            TextBoxMessage.Clear();
            ID1 = -1;
        }

        private void Button2_Click_1(object sender, RoutedEventArgs e)
        {
            DisconnectUser();
        }
        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if(e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void ButtonMinizime_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }

        private void WindowButtonState_Click(object sender, RoutedEventArgs e)
        {
            if(Application.Current.MainWindow.WindowState != WindowState.Maximized)
            {
                Application.Current.MainWindow.WindowState = WindowState.Maximized;
            }
            else
            {
                Application.Current.MainWindow.WindowState = WindowState.Normal;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();   
        }

        private void ButtonFindAndCancelAndDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (state == State.NoSearch)
            {
                Find();
            }
            else if (state == State.Search)
            {
                CancelSearch();
            }
            else
            {
                DisconnectUser();
            }
        }
        private void CancelSearch()
        {
            client.RemoveUser(ID);
            LabelState.Content = "Состояние: Стандартное";
            Button1.Content = "Найти собеседника";
            state = State.NoSearch;
        }
        public enum State
        {
            Search,
            Found,
            NoSearch
        }


        private void TextBoxMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (client != null)
                {
                    byte[] key = ASCIIEncoding.UTF8.GetBytes("Ключ");
                    RC4 encoder = new RC4(key);
                    string testString = TextBoxMessage.Text;
                    byte[] testBytes = ASCIIEncoding.UTF8.GetBytes(testString);
                    byte[] result = encoder.Encode(testBytes, testBytes.Length);
                    client.SendMessage(result, ID, ID1);
                    TextBoxMessage.Text = string.Empty;
                }
            }
        }

    }
}
