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
using ChatClient.ProtocolSignal;
using System.Security.Cryptography;
using System.Security.Policy;
namespace ChatClient
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    public partial class MainWindow : Window, IServiceChatCallback
    {
        private ServiceChat.ServiceChatClient client;
        private ECDiffieHellman alice;
        private byte[] aliceSharedSecret;
        private State state {get; set;}
        private int ID = -1;

        private enum State
        {
            Search,
            Found,
            FoundNoClient,
            NoSearch
        }
        public MainWindow()
        {
            InitializeComponent();
            alice = ECDiffieHellman.Create(GostCurve.GetGost3410Curve());
            client = new ServiceChatClient(new System.ServiceModel.InstanceContext(this));
            ID = client.CreateUser(alice.PublicKey);
            state = State.NoSearch;
            ListViewMessage.Visibility = Visibility.Hidden;
            TextBoxMessage.Visibility = Visibility.Hidden;
        }
        private void Find()
        {
            client.Connect(ID);
        }

        private void DisconnectUser()
        {
            if (state == State.Found)
            {
                client.Disconnect(ID);
                state = State.NoSearch;
            }
            else
            {
                client.Disconnect(ID);
            }
            LabelState.Content = "Состояние: Стандартное";
            Button1.Content = "Найти собеседника";
            ListViewMessage.Visibility = Visibility.Hidden;
            TextBoxMessage.Visibility = Visibility.Hidden;
            state = State.NoSearch;
            ListViewMessage.Items.Clear();
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

        public void GetConnectionAndPublicKey(ECDiffieHellmanPublicKey publickey)
        {
            if (publickey != null)
            {
                state = State.Found;
            }
            else
            {
                state = State.Search;
            }

            aliceSharedSecret = SignalProtocolExample.GenerateSharedSecret(publickey, alice);

            if (state == State.Found)
            {
                TestProtocol();
            }
            else
            {
                LabelState.Content = "Состояние: Поиск собеседника";
                Button1.Content = "Отменить поиск";
                state = State.Search;
            }
        }
        public void TestProtocol()
        {
            byte[] nonce = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonce);
            }
            // Вычисляем HMAC
            byte[] hmac = SignalProtocolExample.ComputeHmac(nonce, aliceSharedSecret);

            // Отправляем nonce и hmac Клиенту 2 (но не секрет!)
            client.SendHashProtocol(nonce, hmac, ID);
        }
        public void GetConnectionProtocol(bool work)
        {
            if (work)
            {
                LabelState.Content = "Состояние: Ваш собеседник найден";
                Button1.Content = "Отключиться";
                ListViewMessage.Visibility = Visibility.Visible;
                TextBoxMessage.Visibility = Visibility.Visible;
                state = State.Found;
                TextBoxMessage.IsEnabled = true;
            }
        }

        public void CompareHMAC(byte[] key, byte[] hmac)
        {
            if(SignalProtocolExample.ComputeHmac(key, aliceSharedSecret) == hmac)
            {
                client.SendHashEquals(true, ID);
            }

        }
        public void LeftChat()
        {
            LabelState.Content = "Состояние: Чат без собеседника";
            TextBoxMessage.IsEnabled = false;
            TextBoxMessage.Clear();
            state = State.FoundNoClient;
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
            client.RemoveUserSearch(ID);
            LabelState.Content = "Состояние: Стандартное";
            Button1.Content = "Найти собеседника";
            state = State.NoSearch;
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
                    client.SendMessage(result, ID);
                    TextBoxMessage.Text = string.Empty;
                }
            }
        }


    }
}
