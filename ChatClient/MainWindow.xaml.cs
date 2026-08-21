using ChatClient.Protocol_Signal;
using ChatClient.ProtocolSignal;
using ChatClient.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private ChatHubClient client;
        private readonly string _hubUrl;
        private readonly MainViewModel _viewModel = new MainViewModel();
        private ECDiffieHellman alice;
        private byte[] aliceSharedSecret;
        private byte[] aesKey;
        private byte[] hmacKey;
        private byte[] _publicKey;
        private byte[] _signPublicKey;
        private Kuznechik kuznechik;
        private GostSignature _signature;
        private readonly Dictionary<string, MessageModel> _messageDict = new Dictionary<string, MessageModel>();
        private State state = State.NoSearch;
        private int ID = -1;

        private SimpleVoiceCall _voiceCall;
        private bool _isInCall;
        private bool _isMuted;
        private string _myIP;
        private int _voicePort;
        private const string PortFile = "voice_port.txt";
        private const int VoicePortMin = 6100;
        private const int VoicePortMax = 7100;
        private float _peerVolume = 1.0f;
        private bool _isPeerMuted;
        private byte[] _voiceSessionKey;
        private byte[] _voiceIV;
        private readonly CallTonePlayer _callTones = new CallTonePlayer();
        private string _incomingCallerIP;
        private int _incomingCallerPort;

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
            DataContext = _viewModel;
            _viewModel.Messages.CollectionChanged += Messages_CollectionChanged;

            alice = ECDiffieHellman.Create(GostCurve.GetGost3410Curve());
            _publicKey = alice.PublicKey.ToByteArray();
            _signature = new GostSignature();
            _signPublicKey = _signature.GetPublicKey();

            _hubUrl = ConfigurationManager.AppSettings["ChatHubUrl"] ?? "http://localhost:5000/chat";
            client = new ChatHubClient(_hubUrl);
            client.GetConnectionAndPublicKey += GetConnectionAndPublicKey;
            client.GetConnectionProtocol += GetConnectionProtocol;
            client.CompareHMAC += CompareHMAC;
            client.LeftChat += LeftChat;
            client.MessageNotification += MessageNotification;
            client.MessageCallBack += MessageCallBack;
            client.MessageCallBackSigned += MessageCallBackSigned;
            client.IncomingCall += IncomingCall;
            client.CallAnswered += CallAnswered;
            client.CallEnded += CallEnded;
            client.ReceiveVoice += ReceiveVoice;
            client.ReceiveVoiceKeys += ReceiveVoiceKeys;

            _voicePort = GetPortFromFile();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await client.StartAsync();
                ID = await client.CreateUserAsync(_publicKey, _signPublicKey);
                _viewModel.IsFindEnabled = true;
            }
            catch (Exception ex)
            {
                _viewModel.IsFindEnabled = false;
                MessageBox.Show(
                    "Не удалось подключиться к серверу чата.\n" +
                    "Адрес: " + _hubUrl + "\n\n" +
                    ex.Message,
                    "Нет соединения",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private bool _isClosing;

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosing)
                return;

            e.Cancel = true;
            _isClosing = true;
            await DisconnectUserAsync();
            _callTones.Dispose();
            client?.Dispose();
            Close();
        }

        private bool CanTalkToServer => client != null && client.IsConnected && ID != -1;

        private async Task FindAsync()
        {
            if (!CanTalkToServer)
                return;

            await client.ConnectAsync(ID);
        }

        private async Task DisconnectUserAsync(bool resetUi = true)
        {
            if (_isInCall)
            {
                try
                {
                    if (CanTalkToServer)
                        await client.SendCallEndAsync(ID);
                }
                catch
                {
                }

                EndCall();
            }

            if (CanTalkToServer)
            {
                try
                {
                    await client.DisconnectAsync(ID);
                }
                catch
                {
                }
            }

            if (!resetUi)
                return;

            TextBoxMessage.Clear();
            _viewModel.ResetToIdle();
            _messageDict.Clear();
            state = State.NoSearch;
        }

        public void GetConnectionAndPublicKey(byte[] publickey, byte[] signPublicKey)
        {
            if (publickey != null && signPublicKey != null)
            {
                _signature.SetPeerKey(signPublicKey);
                state = State.Found;
                aliceSharedSecret = SignalProtocolExample.GenerateSharedSecret(
                    ECDiffieHellmanCngPublicKey.FromByteArray(publickey, CngKeyBlobFormat.EccFullPublicBlob),
                    alice);
                TestProtocol();
                return;
            }

            state = State.Search;
            _viewModel.ShowSearching();
        }

        public async void TestProtocol()
        {
            byte[] nonce = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);

            byte[] hmac = SignalProtocolExample.ComputeHmac(nonce, aliceSharedSecret);
            await client.SendHashProtocolAsync(nonce, hmac, ID);
        }

        public void GetConnectionProtocol(bool work)
        {
            if (!work)
            {
                state = State.NoSearch;
                _ = DisconnectUserAsync(resetUi: false);
                _viewModel.ShowProtocolFailed();
                return;
            }

            _viewModel.ShowConnected();
            _myIP = GetLocalIPAddress();
            state = State.Found;

            SignalProtocolExample.DeriveKeys(aliceSharedSecret, out aesKey, out hmacKey);
            kuznechik = new Kuznechik();
        }

        public async void CompareHMAC(byte[] key, byte[] hmac)
        {
            byte[] test = SignalProtocolExample.ComputeHmac(key, aliceSharedSecret);
            bool matches = test.SequenceEqual(hmac);
            await client.SendHashEqualsAsync(matches, ID);
        }

        public void LeftChat()
        {
            EndCall();
            _viewModel.ShowPeerLeft();
            TextBoxMessage.Clear();
            state = State.FoundNoClient;
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void ButtonMinizime_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void WindowButtonState_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void ButtonFindAndCancelAndDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (state == State.NoSearch)
                await FindAsync();
            else if (state == State.Search)
                await CancelSearchAsync();
            else
                await DisconnectUserAsync();
        }

        private async Task CancelSearchAsync()
        {
            if (CanTalkToServer)
                await client.RemoveUserSearchAsync(ID);

            _viewModel.ResetToIdle();
            state = State.NoSearch;
        }

        public void MessageNotification(string text)
        {
            _viewModel.AddSystemMessage(text);
            ScrollToLast();
        }

        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                var textBox = (TextBox)sender;
                int caretPos = textBox.CaretIndex;
                textBox.Text = textBox.Text.Insert(caretPos, Environment.NewLine);
                textBox.CaretIndex = caretPos + Environment.NewLine.Length;
                e.Handled = true;
                return;
            }

            if (client == null || string.IsNullOrWhiteSpace(TextBoxMessage.Text) || kuznechik == null)
                return;

            e.Handled = true;
            _ = SendCurrentMessageAsync();
        }

        private async Task SendCurrentMessageAsync()
        {
            string text = TextBoxMessage.Text.Trim();
            if (string.IsNullOrEmpty(text) || !CanTalkToServer)
                return;

            var message = _viewModel.AddChatMessage(text, true, MessageStatus.Sent);
            _messageDict[message.Id] = message;
            ScrollToLast();
            TextBoxMessage.Text = string.Empty;

            try
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(text);
                byte[] signature = _signature.Sign(messageBytes);
                byte[] encrypted = kuznechik.KuzEncript(messageBytes, aesKey);
                byte[] hmac = SignalProtocolExample.ComputeHmac(hmacKey, encrypted);
                await client.SendSignedMessageAsync(hmac, encrypted, signature, ID);
                message.Status = MessageStatus.Delivered;
            }
            catch (Exception ex)
            {
                _viewModel.AddSystemMessage("Не удалось отправить сообщение: " + ex.Message);
            }
        }

        public void MessageCallBack(byte[] hmac, string message, byte[] bytes)
        {
            string text = message ?? string.Empty;
            if (bytes != null && hmac != null && kuznechik != null && hmacKey != null)
            {
                byte[] newHmac = SignalProtocolExample.ComputeHmac(hmacKey, bytes);
                if (newHmac.SequenceEqual(hmac))
                {
                    byte[] decryptedMessage = kuznechik.KuzDecript(bytes, aesKey);
                    text += Encoding.UTF8.GetString(decryptedMessage);
                }
                else
                {
                    text += "⚠ ПОДДЕЛЬНОЕ СООБЩЕНИЕ";
                }
            }

            _viewModel.AddChatMessage(text, false, MessageStatus.Delivered);
            ScrollToLast();
        }

        public void MessageCallBackSigned(byte[] hmac, string message, byte[] bytes, byte[] signature)
        {
            if (hmac == null || bytes == null ||
                !SignalProtocolExample.ComputeHmac(hmacKey, bytes).SequenceEqual(hmac))
            {
                _viewModel.AddChatMessage("⚠ ПОДДЕЛЬНОЕ СООБЩЕНИЕ", false, MessageStatus.Delivered);
                ScrollToLast();
                return;
            }

            byte[] decrypted = kuznechik.KuzDecript(bytes, aesKey);
            bool signatureValid = _signature.Verify(decrypted, signature);
            string messageText = Encoding.UTF8.GetString(decrypted);

            if (!signatureValid)
                messageText = "⚠ ПОДДЕЛЬНОЕ: " + messageText;

            _viewModel.AddChatMessage(messageText, false, MessageStatus.Delivered);
            ScrollToLast();
        }

        private void Messages_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                ScrollToLast();
        }

        private void ScrollToLast()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ListViewMessage.UpdateLayout();
                ScrollViewer viewer = FindScrollViewer(ListViewMessage);
                if (viewer != null)
                    viewer.ScrollToEnd();
                else if (ListViewMessage.Items.Count > 0)
                    ListViewMessage.ScrollIntoView(ListViewMessage.Items[ListViewMessage.Items.Count - 1]);
            }), DispatcherPriority.Loaded);
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer)
                return viewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ScrollViewer child = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (child != null)
                    return child;
            }

            return null;
        }

        private void MyAvatar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isInCall)
            {
                _viewModel.AddSystemMessage("Нет активного звонка");
                return;
            }

            MuteButton_Click(sender, e);
        }

        private void PeerAvatar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isInCall)
            {
                _viewModel.AddSystemMessage("Нет активного звонка");
                return;
            }

            var contextMenu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(47, 49, 54)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(32, 34, 37)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 221, 222))
            };

            var header = new MenuItem
            {
                Header = _isPeerMuted ? "Собеседник заглушен" : $"Громкость: {_peerVolume * 100:F0}%",
                IsEnabled = false,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 187, 190))
            };
            contextMenu.Items.Add(header);
            contextMenu.Items.Add(new Separator());

            var slider = new Slider
            {
                Style = (Style)FindResource("ModernSliderStyle"),
                Width = 200,
                Value = _peerVolume * 100,
                Margin = new Thickness(10, 8, 10, 8)
            };

            slider.ValueChanged += (s, args) =>
            {
                _peerVolume = (float)(slider.Value / 100);
                if (!_isPeerMuted)
                    _voiceCall?.SetSpeakerVolume(_peerVolume);

                header.Header = _isPeerMuted ? "Собеседник заглушен" : $"Громкость: {slider.Value:F0}%";
            };

            contextMenu.Items.Add(new MenuItem { Header = slider, StaysOpenOnClick = true });
            contextMenu.Items.Add(new Separator());

            var muteItem = new MenuItem
            {
                Header = _isPeerMuted ? "Включить звук" : "Заглушить"
            };
            muteItem.Click += (s, args) =>
            {
                _isPeerMuted = !_isPeerMuted;
                _voiceCall?.SetSpeakerVolume(_isPeerMuted ? 0 : _peerVolume);
                PeerVoiceStatus.Text = _isPeerMuted ? "Заглушен" : "";
                PeerVoiceStatus.Foreground = new SolidColorBrush(_isPeerMuted
                    ? Color.FromRgb(218, 55, 60)
                    : Color.FromRgb(35, 165, 89));
            };
            contextMenu.Items.Add(muteItem);

            contextMenu.IsOpen = true;
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await GenerateVoiceKeysAsync();
                _myIP = GetLocalIPAddress();
                _viewModel.ShowActiveCall();

                _voiceCall = CreateVoiceCall(microphoneEnabled: true);
                ApplyVoiceKeysIfReady();
                _voicePort = _voiceCall.BindLocalPort(_voicePort);
                _myIP = GetLocalIPAddress();

                await client.SendCallRequestAsync(ID, _myIP, _voicePort);
                _isInCall = true;
                _callTones.PlayOutgoing();
                _viewModel.AddSystemMessage("Звонок...");
            }
            catch (Exception ex)
            {
                _viewModel.AddSystemMessage("Ошибка звонка: " + ex.Message);
                EndCall();
            }
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInCall)
                return;

            _isMuted = !_isMuted;
            _voiceCall?.SetMute(_isMuted);
            MuteButton.Content = _isMuted ? "🔇" : "🎤";
            MuteButton.Background = new SolidColorBrush(_isMuted
                ? Color.FromRgb(218, 55, 60)
                : Color.FromRgb(79, 84, 92));
            if (_isMuted)
                MyGlowBorder.Opacity = 0;
        }

        private async void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInCall)
                return;

            try
            {
                if (CanTalkToServer)
                    await client.SendCallEndAsync(ID);
            }
            catch
            {
            }

            EndCall();
            _viewModel.AddSystemMessage("Звонок завершен");
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                        return endPoint.Address.ToString();
                }
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        public void IncomingCall(int fromUserId, string callerIP, int callerPort)
        {
            _incomingCallerIP = callerIP;
            _incomingCallerPort = callerPort;
            _viewModel.IsIncomingCallVisible = true;
            _callTones.PlayIncoming();
        }

        private async void AcceptIncomingCall_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.IsIncomingCallVisible = false;
            _callTones.Stop();
            _viewModel.ShowActiveCall();

            _voiceCall = CreateVoiceCall(microphoneEnabled: true);
            ApplyVoiceKeysIfReady();
            _voicePort = _voiceCall.BindLocalPort(_voicePort);
            _voiceCall.SetRemote(_incomingCallerIP, _incomingCallerPort);
            _myIP = GetLocalIPAddress();

            try
            {
                await client.SendCallAnswerAsync(ID, true, _myIP, _voicePort);
                _isInCall = true;
                _voiceCall.BeginLocalAudio();
                _viewModel.AddSystemMessage("Звонок принят");
            }
            catch (Exception ex)
            {
                _viewModel.AddSystemMessage("Не удалось принять звонок: " + ex.Message);
                EndCall();
            }
        }

        private async void DeclineIncomingCall_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.IsIncomingCallVisible = false;
            _callTones.Stop();
            try
            {
                await client.SendCallAnswerAsync(ID, false, "", 0);
            }
            catch
            {
            }
        }

        public void CallAnswered(int fromUserId, bool accept, string answererIP, int answererPort)
        {
            if (accept)
            {
                _callTones.Stop();
                _isInCall = true;
                ApplyVoiceKeysIfReady();
                _voiceCall?.SetRemote(answererIP, answererPort);
                _voiceCall?.BeginLocalAudio();
                _viewModel.AddSystemMessage("Собеседник ответил");
                return;
            }

            _viewModel.AddSystemMessage("Собеседник отклонил звонок");
            EndCall();
        }

        public void CallEnded(int fromUserId)
        {
            _viewModel.AddSystemMessage("Собеседник завершил звонок");
            EndCall();
        }

        private void EndCall()
        {
            _callTones.Stop();
            _voiceCall?.HangUp();
            _voiceCall?.Dispose();
            _voiceCall = null;
            _isInCall = false;
            _isMuted = false;
            _viewModel.IsIncomingCallVisible = false;
            _viewModel.ShowIdleCall();

            MuteButton.Content = "🎤";
            MuteButton.Background = new SolidColorBrush(Color.FromRgb(79, 84, 92));
            MyGlowBorder.Opacity = 0;
            PeerGlowBorder.Opacity = 0;
            PeerVoiceStatus.Text = "";

            if (_voiceSessionKey != null)
                Array.Clear(_voiceSessionKey, 0, _voiceSessionKey.Length);
            if (_voiceIV != null)
                Array.Clear(_voiceIV, 0, _voiceIV.Length);

            _voiceSessionKey = null;
            _voiceIV = null;
        }

        private int GetPortFromFile()
        {
            try
            {
                if (File.Exists(PortFile))
                {
                    string content = File.ReadAllText(PortFile).Trim();
                    if (int.TryParse(content, out int port) && port >= VoicePortMin && port <= VoicePortMax)
                    {
                        int nextPort = port >= VoicePortMax ? VoicePortMin : port + 1;
                        File.WriteAllText(PortFile, nextPort.ToString());
                        return port;
                    }
                }

                int newPort = new Random().Next(VoicePortMin, VoicePortMax);
                File.WriteAllText(PortFile, (newPort + 1).ToString());
                return newPort;
            }
            catch
            {
                return new Random().Next(VoicePortMin, VoicePortMax);
            }
        }

        public void ReceiveVoice(int fromUserId, byte[] voiceData)
        {
            _voiceCall?.ReceiveVoice(voiceData);
        }

        private async Task GenerateVoiceKeysAsync()
        {
            _voiceSessionKey = new byte[32];
            _voiceIV = new byte[16];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_voiceSessionKey);
                rng.GetBytes(_voiceIV);
            }

            if (CanTalkToServer)
                await client.SendVoiceKeysAsync(ID, _voiceSessionKey, _voiceIV);
        }

        public void ReceiveVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            _voiceSessionKey = sessionKey;
            _voiceIV = iv;
            ApplyVoiceKeysIfReady();
            if (_isInCall)
                _voiceCall?.BeginLocalAudio();
        }

        private SimpleVoiceCall CreateVoiceCall(bool microphoneEnabled)
        {
            var call = new SimpleVoiceCall(client, ID);
            call.SetMicrophoneEnabled(microphoneEnabled);
            call.SetEncryptionEnabled(true);
            call.OnStatusChanged += msg => Dispatcher.BeginInvoke(new Action(() => _viewModel.AddSystemMessage(msg)));
            call.OnVolumeChanged += level =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MyGlowBorder.Opacity = level > 0.05f && !_isMuted ? 0.5 + (level * 0.5) : 0;
                }));
            };
            return call;
        }

        private void ApplyVoiceKeysIfReady()
        {
            if (_voiceCall == null || _voiceSessionKey == null || _voiceIV == null)
                return;

            _voiceCall.SetEncryptionKey(_voiceSessionKey, _voiceIV);
        }
    }
}
