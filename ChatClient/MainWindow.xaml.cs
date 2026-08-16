using ChatClient.Protocol_Signal;
using ChatClient.ProtocolSignal;
using ChatClient.ServiceChat;
using ChatClient.ViewModel;
using Org.BouncyCastle.Asn1.X9;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Policy;
using System.ServiceModel;
using System.ServiceModel.Channels;
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
namespace ChatClient
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple)]
    public partial class MainWindow : Window, IServiceChatCallback
    {
        private ServiceChat.ServiceChatClient client;
        private readonly MainViewModel _viewModel;
        private ECDiffieHellman alice;
        private byte[] aliceSharedSecret;
        private byte[] aesKey;
        private byte[] hmacKey;
        private Kuznechik kuznechik;
        private GostSignature _signature;
        private ObservableCollection<MessageModel> _messages = new ObservableCollection<MessageModel>();
        private Dictionary<string, MessageModel> _messageDict = new Dictionary<string, MessageModel>();
        private State state {get; set;}
        private int ID = -1;

        private SimpleVoiceCall _voiceCall;
        private bool _isInCall = false;
        private bool _isMuted = false;
        private string _myIP;
        private int _voicePort = 5000;
        private string _portFile = "voice_port.txt";
        private float _peerVolume = 1.0f;
        private bool _isPeerMuted = false; // Заглушен ли собеседник
        private int _peerId;
        private string _peerIP;
        private Random _random = new Random();
        private byte[] _voiceSessionKey;  // Сессионный ключ для голоса
        private byte[] _voiceIV;          // Вектор инициализации для голоса
        private bool _voiceEncryptionEnabled = true;


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
            ListViewMessage.ItemsSource = _messages;
            _viewModel = new MainViewModel();
            alice = ECDiffieHellman.Create(GostCurve.GetGost3410Curve());
            byte[] publicKey = alice.PublicKey.ToByteArray();
            _signature = new GostSignature();
            byte[] mySignPublicKey = _signature.GetPublicKey();
            client = new ServiceChatClient(new System.ServiceModel.InstanceContext(this));           
            ID = client.CreateUser(publicKey, mySignPublicKey);
            state = State.NoSearch;
            ListViewMessage.Visibility = Visibility.Hidden;
            TextBoxMessage.Visibility = Visibility.Hidden;
            _voicePort = GetPortFromFile();
        }
        private void Find()
        {
            client.Connect(ID);
        }

        private void DisconnectUser()
        {
            Dispatcher.Invoke(() =>
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
                CallPanel.Visibility = Visibility.Collapsed;
                TextBoxMessage.IsEnabled = false;
                state = State.NoSearch;

                _voiceCall?.HangUp();
                _isInCall = false;

                // ✅ Очищаем коллекцию, а не напрямую Items
                _messages.Clear();
                _messageDict.Clear();
            });
        }


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DisconnectUser();
        }

        public void GetConnectionAndPublicKey(byte[] publickey, byte[] signPublicKey)
        {
            if (publickey != null && signPublicKey != null)
            {
                _signature.SetPeerKey(signPublicKey);
                state = State.Found;
                aliceSharedSecret = SignalProtocolExample.GenerateSharedSecret(ECDiffieHellmanCngPublicKey.FromByteArray(publickey, CngKeyBlobFormat.EccFullPublicBlob), alice);
            }
            else
            {
                state = State.Search;
            }

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
                CallPanel.Visibility = Visibility.Visible;
                CallButton.Visibility = Visibility.Visible;
                ListViewMessage.Visibility = Visibility.Visible;
                TextBoxMessage.Visibility = Visibility.Visible;
                TextBoxMessage.IsEnabled = true;

                _myIP = GetLocalIPAddress();

                state = State.Found;

                SignalProtocolExample.DeriveKeys(aliceSharedSecret, out aesKey, out hmacKey);
                kuznechik = new Kuznechik();
            }
        }

        public void CompareHMAC(byte[] key, byte[] hmac)
        {
            byte[] test = SignalProtocolExample.ComputeHmac(key, aliceSharedSecret);
            if (test.SequenceEqual(hmac))
            {
                client.SendHashEquals(true, ID);
            }

        }
        public void LeftChat()
        {
            LabelState.Content = "Состояние: Чат без собеседника";
            TextBoxMessage.IsEnabled = false;
            TextBoxMessage.Clear();
            CallPanel.Visibility = Visibility.Collapsed;

            _voiceCall?.HangUp();
            _isInCall = false;

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

        public void MessageNotification(string text)
        {
            Dispatcher.Invoke(() =>
            {
                // Создаем системное сообщение (по центру)
                var systemMessage = new MessageModel
                {
                    Id = Guid.NewGuid().ToString(),
                    Text = text,
                    Timestamp = DateTime.Now,
                    IsOwnMessage = false,
                    Type = MessageType.System,  // ← Системное сообщение
                    Status = MessageStatus.Sent
                };

                _messages.Add(systemMessage);

                // Прокручиваем к новому сообщению
                if (ListViewMessage.Items.Count > 0)
                {
                    ListViewMessage.ScrollIntoView(systemMessage);
                }
            });
        }

        private void TextBoxMessage_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Автоматическое увеличение высоты
            if (TextBoxMessage.LineCount > 1 && TextBoxMessage.Height < 150)
            {
                TextBoxMessage.Height = TextBoxMessage.LineCount * 20;
            }
            else if (TextBoxMessage.LineCount <= 1)
            {
                TextBoxMessage.Height = 44;
            }

            // Можно добавить проверку на максимальную длину сообщения
        }

        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    var textBox = (TextBox)sender;
                    int caretPos = textBox.CaretIndex;
                    textBox.Text = textBox.Text.Insert(caretPos, Environment.NewLine);
                    textBox.CaretIndex = caretPos + 1;
                    e.Handled = true;
                }
                else
                {
                    if (client != null && !string.IsNullOrWhiteSpace(TextBoxMessage.Text))
                    {
                        e.Handled = true;
                        string text = TextBoxMessage.Text.Trim();

                        // Создаем визуальное сообщение
                        var message = new MessageModel
                        {
                            Text = text,
                            Timestamp = DateTime.Now,
                            IsOwnMessage = true,
                            Status = MessageStatus.Sent
                        };

                        // Добавляем в UI
                        _messages.Add(message);
                        _messageDict[message.Id] = message;

                        // Прокручиваем к новому сообщению
                        ListViewMessage.ScrollIntoView(message);

                        // Отправляем (ваш существующий код)
                        byte[] messageBytes = Encoding.UTF8.GetBytes(text);
                        byte[] signature = _signature.Sign(messageBytes);
                        byte[] encrypted = kuznechik.KuzEncript(messageBytes, aesKey);
                        byte[] hmac = SignalProtocolExample.ComputeHmac(hmacKey, messageBytes);
                        client.SendSignedMessage(hmac, encrypted, signature, ID);

                        // Очищаем поле
                        TextBoxMessage.Text = string.Empty;
                    }
                }
            }
        }

        public void MessageCallBack(string message, byte[] bytes = null)
        {
            string text = message;
            if (bytes != null)
            {
                byte[] decryptedMessage = kuznechik.KuzDecript(bytes, aesKey);
                string decryptedText = Encoding.UTF8.GetString(decryptedMessage);
                text += decryptedText;
            }
            ListViewMessage.Items.Add(text);
            ListViewMessage.ScrollIntoView(ListViewMessage.Items[ListViewMessage.Items.Count - 1]);
        }

        public void MessageCallBack(byte[] hmac, string message, byte[] bytes)
        {
            string text = message;
            if (bytes != null)
            {
                byte[] decryptedMessage = kuznechik.KuzDecript(bytes, aesKey);
                byte[] newHmac = SignalProtocolExample.ComputeHmac(hmacKey, decryptedMessage);

                if (newHmac.SequenceEqual(hmac))
                {
                    string decryptedText = Encoding.UTF8.GetString(decryptedMessage);
                    text += decryptedText;
                }

            }
            ListViewMessage.Items.Add(text);
            ListViewMessage.ScrollIntoView(ListViewMessage.Items[ListViewMessage.Items.Count - 1]);
        }

        public void MessageCallBackSigned(byte[] hmac, string message, byte[] bytes, byte[] signature)
        {
            byte[] decrypted = kuznechik.KuzDecript(bytes, aesKey);

            // Проверка HMAC
            byte[] computedHmac = SignalProtocolExample.ComputeHmac(hmacKey, decrypted);
            bool hmacValid = computedHmac.SequenceEqual(hmac);

            // Проверка подписи
            bool signatureValid = _signature.Verify(decrypted, signature);

            // ВАЖНО: переменная messageText, а не text
            string messageText = Encoding.UTF8.GetString(decrypted);

            Dispatcher.Invoke(() =>
            {
                var newMessage = new MessageModel  // Переименовал в newMessage
                {
                    Text = messageText,  // Используем messageText
                    Timestamp = DateTime.Now,
                    IsOwnMessage = false,
                    Status = MessageStatus.Delivered
                };

                // Если проверка не прошла - помечаем
                if (!hmacValid || !signatureValid)
                {
                    newMessage.Text = $"⚠ ПОДДЕЛЬНОЕ: {messageText}";
                }
                _messages.Add(newMessage);
                ListViewMessage.ScrollIntoView(newMessage);
            });
        }

        /// <summary>
        /// Заглушка для правого клика на свой аватар (регулировка микрофона)
        /// </summary>
        private void MyAvatar_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // TODO: Реализовать после добавления голосовой связи
            MessageNotification("🎤 Регулировка микрофона (будет доступна после реализации голосовой связи)");
        }

        /// <summary>
        /// Заглушка для правого клика на аватар собеседника (регулировка громкости)
        /// </summary>
        private void PeerAvatar_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isInCall)
            {
                MessageNotification("🔇 Нет активного звонка");
                return;
            }

            // Создаем стилизованное контекстное меню
            var contextMenu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(47, 49, 54)), // #2f3136
                BorderBrush = new SolidColorBrush(Color.FromRgb(32, 34, 37)), // #202225
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 221, 222)) // #DCDDDE
            };

            // Заголовок
            var header = new MenuItem
            {
                Header = _isPeerMuted ? "🔇 Собеседник заглушен" : $"🔊 Громкость: {_peerVolume * 100:F0}%",
                IsEnabled = false,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 187, 190)) // #B9BBBE
            };
            contextMenu.Items.Add(header);
            contextMenu.Items.Add(new Separator());

            // Панель с ползунком
            var sliderPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

            // ✅ ИСПОЛЬЗУЕМ КРАСИВЫЙ СТИЛЬ
            var slider = new Slider
            {
                Style = (Style)FindResource("ModernSliderStyle"),
                Width = 200,
                Value = _peerVolume * 100,
                Margin = new Thickness(0, 5, 0, 5)
            };

            // Текст значения (в стиле уже есть ValueText, но добавим отдельно для надежности)
            var valueText = new TextBlock
            {
                Text = $"{_peerVolume * 100:F0}%",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 187, 190)),
                Margin = new Thickness(0, 5, 0, 0)
            };

            slider.ValueChanged += (s, args) =>
            {
                float volume = (float)(slider.Value / 100);
                _peerVolume = volume;

                if (!_isPeerMuted)
                {
                    _voiceCall?.SetSpeakerVolume(volume);
                }

                valueText.Text = $"{slider.Value:F0}%";
                header.Header = _isPeerMuted ? "🔇 Собеседник заглушен" : $"🔊 Громкость: {slider.Value:F0}%";

                // Обновляем текст в стиле (если нужно)
                if (slider.Template?.FindName("ValueText", slider) is TextBlock styleValueText)
                {
                    styleValueText.Text = $"{slider.Value:F0}%";
                }
            };

            sliderPanel.Children.Add(slider);
            sliderPanel.Children.Add(valueText);

            var sliderItem = new MenuItem { Header = sliderPanel, StaysOpenOnClick = true };
            contextMenu.Items.Add(sliderItem);
            contextMenu.Items.Add(new Separator());

            // Кнопка заглушить/включить
            var muteItem = new MenuItem
            {
                Header = _isPeerMuted ? "🎤 Включить звук" : "🔇 Заглушить",
                Icon = new TextBlock { Text = _isPeerMuted ? "🔊" : "🔇", Margin = new Thickness(0, 0, 5, 0) }
            };
            muteItem.Click += (s, args) =>
            {
                _isPeerMuted = !_isPeerMuted;

                if (_isPeerMuted)
                {
                    _voiceCall?.SetSpeakerVolume(0);
                    muteItem.Header = "🎤 Включить звук";
                    muteItem.Icon = new TextBlock { Text = "🔊", Margin = new Thickness(0, 0, 5, 0) };
                    slider.Value = 0;
                    PeerVoiceStatus.Text = "🔇 Заглушен";
                    PeerVoiceStatus.Foreground = new SolidColorBrush(Color.FromRgb(218, 55, 60));
                    MessageNotification("🔇 Звук собеседника отключен");
                }
                else
                {
                    _voiceCall?.SetSpeakerVolume(_peerVolume);
                    muteItem.Header = "🔇 Заглушить";
                    muteItem.Icon = new TextBlock { Text = "🔇", Margin = new Thickness(0, 0, 5, 0) };
                    slider.Value = _peerVolume * 100;
                    PeerVoiceStatus.Text = "";
                    MessageNotification($"🔊 Звук собеседника включен ({_peerVolume * 100:F0}%)");
                }

                header.Header = _isPeerMuted ? "🔇 Собеседник заглушен" : $"🔊 Громкость: {slider.Value:F0}%";
            };
            contextMenu.Items.Add(muteItem);

            contextMenu.Items.Add(new Separator());

            // Кнопка сброса
            var resetItem = new MenuItem
            {
                Header = "↺ Сбросить (100%)",
                Icon = new TextBlock { Text = "↺", Margin = new Thickness(0, 0, 5, 0) }
            };
            resetItem.Click += (s, args) =>
            {
                slider.Value = 100;
                if (!_isPeerMuted)
                {
                    _voiceCall?.SetSpeakerVolume(1.0f);
                }
                MessageNotification("🔊 Громкость собеседника сброшена до 100%");
            };
            contextMenu.Items.Add(resetItem);

            // Стиль для MenuItem
            foreach (var item in contextMenu.Items)
            {
                if (item is MenuItem menuItem && menuItem != header && menuItem != sliderItem)
                {
                    menuItem.Background = Brushes.Transparent;
                    menuItem.Foreground = new SolidColorBrush(Color.FromRgb(220, 221, 222));

                    var template = new ControlTemplate(typeof(MenuItem));
                    var border = new FrameworkElementFactory(typeof(Border));
                    border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
                    border.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
                    border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(MenuItem.BackgroundProperty));

                    var content = new FrameworkElementFactory(typeof(StackPanel), "Stack");
                    content.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

                    var icon = new FrameworkElementFactory(typeof(ContentPresenter), "Icon");
                    icon.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.IconProperty));
                    icon.SetValue(ContentPresenter.MarginProperty, new Thickness(0, 0, 5, 0));
                    icon.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

                    var headerText = new FrameworkElementFactory(typeof(ContentPresenter), "Header");
                    headerText.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.HeaderProperty));
                    headerText.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

                    content.AppendChild(icon);
                    content.AppendChild(headerText);
                    border.AppendChild(content);

                    template.VisualTree = border;

                    var trigger = new Trigger { Property = MenuItem.IsMouseOverProperty, Value = true };
                    trigger.Setters.Add(new Setter { Property = MenuItem.BackgroundProperty, Value = new SolidColorBrush(Color.FromRgb(64, 68, 75)) });
                    template.Triggers.Add(trigger);

                    menuItem.Template = template;
                }
            }

            contextMenu.IsOpen = true;
        }

        /// <summary>
        /// Кнопка звонка
        /// </summary>
        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GenerateVoiceKeys();

                // Получаем свой IP
                _myIP = GetLocalIPAddress();

                // Переключаем в режим активного звонка
                IdleMode.Visibility = Visibility.Collapsed;
                ActiveMode.Visibility = Visibility.Visible;
                CallPanel.Height = 100;

                // Инициализируем голос
                _voiceCall = new SimpleVoiceCall(client, ID);
                _voiceCall.SetMicrophoneEnabled(true);

                _voiceCall.SetEncryptionKey(_voiceSessionKey, _voiceIV);
                _voiceCall.SetEncryptionEnabled(_voiceEncryptionEnabled);

                _voiceCall.OnStatusChanged += (msg) => MessageNotification(msg);
                _voiceCall.OnVolumeChanged += (level) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (level > 0.05f && !_isMuted)
                        {
                            MyGlowBorder.Opacity = 0.5 + (level * 0.5);
                        }
                        else
                        {
                            MyGlowBorder.Opacity = 0;
                        }
                    });
                };

                // Отправляем запрос на звонок через сервер
                // Нужно добавить метод в WCF сервис
                client.SendCallRequest(ID, _myIP, _voicePort);

                MessageNotification("📞 Звонок...");

                // Запускаем прием звонка
                await _voiceCall.AcceptCall(_voicePort);
                _isInCall = true;
            }
            catch (Exception ex)
            {
                MessageNotification($"❌ Ошибка: {ex.Message}");
            }
        }

        /// <summary>
        /// Кнопка Mute (отключить микрофон)
        /// </summary>
        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInCall) return;

            _isMuted = !_isMuted;

            if (_isMuted)
            {
                _voiceCall?.SetMute(true);
                MuteButton.Content = "🔇";
                MuteButton.Background = new SolidColorBrush(Color.FromRgb(218, 55, 60));
                MyGlowBorder.Opacity = 0;
                MessageNotification("🔇 Микрофон отключен");
            }
            else
            {
                _voiceCall?.SetMute(false);
                MuteButton.Content = "🎤";
                MuteButton.Background = new SolidColorBrush(Color.FromRgb(79, 84, 92));
                MessageNotification("🎤 Микрофон включен");
            }
        }

        /// <summary>
        /// Кнопка завершения звонка
        /// </summary>
        private void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInCall) return;

            _voiceCall?.HangUp();
            _isInCall = false;
            _isMuted = false;

            // Возвращаем в режим ожидания
            IdleMode.Visibility = Visibility.Visible;
            ActiveMode.Visibility = Visibility.Collapsed;
            CallPanel.Height = 60;

            MuteButton.Content = "🎤";
            MuteButton.Background = new SolidColorBrush(Color.FromRgb(79, 84, 92));
            MyGlowBorder.Opacity = 0;
            PeerGlowBorder.Opacity = 0;

            MessageNotification("📞 Звонок завершен");
        }

        private string GetLocalIPAddress()
        {
            // var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            // foreach (var ip in host.AddressList)
            // {
            //     if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            //     {
            //         return ip.ToString();
            //     }
            // }

            // Для теста на одном ПК используем localhost
            return "127.0.0.1";
        }

        public void IncomingCall(int fromUserId, string callerIP, int callerPort)
        {
            Dispatcher.Invoke(async () =>
            {
                _peerId = fromUserId;
                _peerIP = callerIP;

                var result = MessageBox.Show($"Входящий звонок!\n\nПринять?",
                    "Голосовой звонок",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    GenerateVoiceKeys();

                    IdleMode.Visibility = Visibility.Collapsed;
                    ActiveMode.Visibility = Visibility.Visible;
                    CallPanel.Height = 100;

                    _voiceCall = new SimpleVoiceCall(client, ID);

                    _voiceCall.SetEncryptionKey(_voiceSessionKey, _voiceIV);

                    // ✅ Синхронизируем счетчик (начинаем с 0)
                    _voiceCall.SyncEncryptionCounter(0);

                    _voiceCall.SetEncryptionKey(_voiceSessionKey, _voiceIV);
                    _voiceCall.SetEncryptionEnabled(_voiceEncryptionEnabled);

                    _voiceCall.SetMicrophoneEnabled(false);
                    _voiceCall.OnStatusChanged += (msg) => MessageNotification(msg);
                    _voiceCall.OnVolumeChanged += (level) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (level > 0.05f && !_isMuted)
                                MyGlowBorder.Opacity = 0.5 + (level * 0.5);
                            else
                                MyGlowBorder.Opacity = 0;
                        });
                    };

                    client.SendCallAnswer(ID, true, _myIP, _voicePort);
                    await _voiceCall.StartCall(callerIP, callerPort);
                    _isInCall = true;
                    MessageNotification("✅ Звонок принят");
                }
                else
                {
                    client.SendCallAnswer(ID, false, "", 0);
                }
            });
        }

        public void CallAnswered(int fromUserId, bool accept, string answererIP, int answererPort)
        {
            Dispatcher.Invoke(async () =>
            {
                if (accept)
                {
                    _peerIP = answererIP;
                    if (_voiceCall != null)
                    {
                        _voiceCall.SyncEncryptionCounter(0);
                    }
                    await _voiceCall.StartCall(answererIP, answererPort);
                    _isInCall = true;
                    MessageNotification("✅ Собеседник ответил");
                }
                else
                {
                    MessageNotification("❌ Собеседник отклонил звонок");
                    EndCall();
                }
            });
        }

        public void CallEnded(int fromUserId)
        {
            Dispatcher.Invoke(() =>
            {
                MessageNotification("📞 Собеседник завершил звонок");
                EndCall();
            });
        }

        private void EndCall()
        {
            _voiceCall?.HangUp();
            _isInCall = false;
            _isMuted = false;

            IdleMode.Visibility = Visibility.Visible;
            ActiveMode.Visibility = Visibility.Collapsed;
            CallPanel.Height = 60;

            MuteButton.Content = "🎤";
            MuteButton.Background = new SolidColorBrush(Color.FromRgb(79, 84, 92));
            MyGlowBorder.Opacity = 0;
            PeerGlowBorder.Opacity = 0;

            if (_voiceSessionKey != null)
                Array.Clear(_voiceSessionKey, 0, _voiceSessionKey.Length);
            if (_voiceIV != null)
                Array.Clear(_voiceIV, 0, _voiceIV.Length);
        }

        private int GetPortFromFile()
        {
            try
            {
                // Если файл существует, читаем порт
                if (File.Exists(_portFile))
                {
                    string content = File.ReadAllText(_portFile).Trim();
                    if (int.TryParse(content, out int port))
                    {
                        // Если порт в диапазоне 5000-6000, используем его
                        if (port >= 5000 && port <= 6000)
                        {
                            // ✅ Увеличиваем порт на 1 для следующего запуска
                            int nextPort = port + 1;

                            // Если порт вышел за диапазон, начинаем с 5000
                            if (nextPort > 6000)
                            {
                                nextPort = 5000;
                            }

                            // Сохраняем следующий порт в файл
                            File.WriteAllText(_portFile, nextPort.ToString());

                            Console.WriteLine($"📖 Прочитан порт из файла: {port}");
                            Console.WriteLine($"📝 Следующий порт будет: {nextPort}");

                            return port;
                        }
                    }
                }

                // Если файла нет или порт невалидный, создаем новый
                int newPort = new Random().Next(5000, 6000);
                int nextNewPort = newPort + 1;
                if (nextNewPort > 6000) nextNewPort = 5000;

                File.WriteAllText(_portFile, nextNewPort.ToString());
                Console.WriteLine($"📝 Создан новый порт: {newPort}");
                Console.WriteLine($"📝 Следующий порт будет: {nextNewPort}");
                return newPort;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка чтения порта: {ex.Message}");
                // Если ошибка, возвращаем случайный порт
                return new Random().Next(5000, 6000);
            }
        }
        /// <summary>
        /// Получение голоса от сервера (ретрансляция)
        /// </summary>
        public void ReceiveVoice(int fromUserId, byte[] voiceData)
        {
            // Получаем голос от сервера и воспроизводим
            _voiceCall?.ReceiveVoice(voiceData);
        }

        /// <summary>
        /// Генерация ключей для шифрования голоса
        /// </summary>
        private void GenerateVoiceKeys()
        {
            _voiceSessionKey = new byte[32]; // 256 бит для Кузнечика
            _voiceIV = new byte[16];         // 128 бит для IV

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_voiceSessionKey);
                rng.GetBytes(_voiceIV);
            }

            // Отправляем ключи собеседнику через сервер
            client.SendVoiceKeys(ID, _voiceSessionKey, _voiceIV);
        }

        /// <summary>
        /// Получение ключей шифрования голоса от собеседника
        /// </summary>
        public void ReceiveVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            Dispatcher.Invoke(() =>
            {
                _voiceSessionKey = sessionKey;
                _voiceIV = iv;

                // Если звонок уже активен, обновляем ключи
                if (_voiceCall != null && _isInCall)
                {
                    _voiceCall.SetEncryptionKey(sessionKey, iv);
                    MessageNotification("🔐 Ключи шифрования голоса обновлены");
                }
                else
                {
                    MessageNotification("🔐 Ключи шифрования голоса получены");
                }
            });
        }
    }

}
