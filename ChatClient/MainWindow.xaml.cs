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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private DirectPeerLink _directPeer;
        private CancellationTokenSource _directLinkCts;
        private int _peerId = -1;
        private bool _directP2PActive;
        private int _directPort;
        private const string DirectPortFile = "chat_direct_port.txt";
        private const int DirectPortMin = 7200;
        private const int DirectPortMax = 8200;
        private SimpleVideoCall _videoCall;
        private byte[] _videoSessionKey;
        private byte[] _videoIV;
        private byte[] _videoMacKey;
        private int _videoPort;
        private bool _isVideoActive;
        private string _pendingVideoHost;
        private int _pendingVideoPort;
        private const string VideoPortFile = "video_port.txt";
        private const int VideoPortMin = 8300;
        private const int VideoPortMax = 9300;
        private string _pendingDirectHost;
        private int _pendingDirectPort;
        private int _directReconnectVersion;
        private bool _directConnectInProgress;
        private int _activeDirectListenPort;

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

            _hubUrl = ConfigurationManager.AppSettings["ChatHubUrl"] ?? "http://179.66.184.184:8080/chat";
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
            client.EstablishDirectLink += OnEstablishDirectLink;

            _voicePort = GetPortFromFile();
            _directPort = GetDirectPortFromFile();
            _videoPort = GetVideoPortFromFile();
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
            EndVideoInternal(showMessage: false);
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

            EndVideoInternal(showMessage: false);
            TearDownDirectLink(notifyPeer: true);

            if (!resetUi)
                return;

            TextBoxMessage.Clear();
            _viewModel.ResetToIdle();
            _messageDict.Clear();
            state = State.NoSearch;
            _peerId = -1;
            _directP2PActive = false;
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

        public void GetConnectionProtocol(bool work, int peerId)
        {
            if (!work)
            {
                state = State.NoSearch;
                _ = DisconnectUserAsync(resetUi: false);
                _viewModel.ShowProtocolFailed();
                return;
            }

            _peerId = peerId;
            _viewModel.ShowConnected();
            _myIP = GetLocalIPAddress();
            state = State.Found;

            SignalProtocolExample.DeriveKeys(aliceSharedSecret, out aesKey, out hmacKey);
            kuznechik = new Kuznechik();
            _ = BeginDirectPeerSessionAsync();
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

        private void TextBoxMessage_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = (TextBox)sender;
            textBox.UpdateLayout();
            bool useTop = textBox.Text.Contains('\n')
                || textBox.Text.Contains('\r')
                || textBox.ActualHeight > textBox.MinHeight + 2;
            textBox.VerticalContentAlignment = useTop ? VerticalAlignment.Top : VerticalAlignment.Center;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (client == null || string.IsNullOrWhiteSpace(TextBoxMessage.Text) || kuznechik == null)
                return;

            await SendCurrentMessageAsync();
        }

        private async Task SendCurrentMessageAsync()
        {
            string text = TextBoxMessage.Text.Trim();
            if (string.IsNullOrEmpty(text) || state != State.Found || kuznechik == null)
                return;
            if (_directPeer?.IsConnected != true && !CanTalkToServer)
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
                await SendSignedMessageRouteAsync(hmac, encrypted, signature);
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

            PeerVolumeSlider.Value = _peerVolume * 100;
            UpdatePeerVolumePopupUi();
            PeerVolumePopup.PlacementTarget = PeerParticipantTile;
            PeerVolumePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
            PeerVolumePopup.IsOpen = true;
            e.Handled = true;
        }

        private void UpdatePeerVolumePopupUi()
        {
            PeerVolumeHeader.Text = _isPeerMuted
                ? "Собеседник заглушен"
                : $"Громкость: {_peerVolume * 100:F0}%";
            PeerVolumeMuteButton.Content = _isPeerMuted
                ? "Включить звук собеседника"
                : "Заглушить собеседника";
        }

        private void PeerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!PeerVolumePopup.IsOpen)
                return;

            _peerVolume = (float)(PeerVolumeSlider.Value / 100);
            if (!_isPeerMuted)
                _voiceCall?.SetSpeakerVolume(_peerVolume);

            UpdatePeerVolumePopupUi();
        }

        private void PeerVolumeMuteButton_Click(object sender, RoutedEventArgs e)
        {
            _isPeerMuted = !_isPeerMuted;
            _voiceCall?.SetSpeakerVolume(_isPeerMuted ? 0 : _peerVolume);
            PeerVoiceStatus.Text = _isPeerMuted ? "Заглушен" : "В голосовом чате";
            PeerVoiceStatus.Foreground = new SolidColorBrush(_isPeerMuted
                ? Color.FromRgb(237, 66, 69)
                : Color.FromRgb(148, 155, 164));
            UpdatePeerVolumePopupUi();
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await GenerateVoiceKeysAsync();
                _myIP = GetLocalIPAddress();
                _viewModel.ShowActiveCall();
                ApplyCameraButtonVisuals();

                _voiceCall = CreateVoiceCall(microphoneEnabled: true);
                ApplyVoiceKeysIfReady();
                _voicePort = _voiceCall.BindLocalPort(_voicePort);
                _myIP = GetLocalIPAddress();

                await SendCallRequestRouteAsync(_myIP, _voicePort);
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
            ApplyMuteButtonVisuals();
            if (_isMuted)
            {
                MyGlowBorder.Opacity = 0;
                SetMyVoiceStatus("Микрофон выключен", muted: true);
            }
            else
            {
                SetMyVoiceStatus(null);
            }
        }

        private void SetMyVoiceStatus(string message, bool muted = false)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                MyStatusText.Visibility = Visibility.Collapsed;
                MyStatusText.Text = string.Empty;
                return;
            }

            MyStatusText.Visibility = Visibility.Visible;
            MyStatusText.Text = message;
            MyStatusText.Foreground = new SolidColorBrush(muted
                ? Color.FromRgb(237, 66, 69)
                : Color.FromRgb(148, 155, 164));
        }

        private void ApplyMuteButtonVisuals()
        {
            const string glyph = "\uE720";
            var normal = new SolidColorBrush(Color.FromRgb(78, 80, 88));
            var muted = new SolidColorBrush(Color.FromRgb(237, 66, 69));

            MuteButton.Content = glyph;
            MuteButton.Background = _isMuted ? muted : normal;

            if (VideoMuteButton != null)
            {
                VideoMuteButton.Content = glyph;
                VideoMuteButton.Background = _isMuted ? muted : normal;
            }
        }

        private void ApplyCameraButtonVisuals()
        {
            if (CallStageCameraButton == null)
                return;

            var normal = new SolidColorBrush(Color.FromRgb(78, 80, 88));
            var active = new SolidColorBrush(Color.FromRgb(35, 165, 89));

            CallStageCameraButton.Content = "\uE714";
            CallStageCameraButton.Background = _isVideoActive ? active : normal;
            CallStageCameraButton.ToolTip = _isVideoActive ? "Выключить камеру" : "Включить камеру";
        }

        private async void CallStageCameraButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleCameraAsync();
        }

        private async Task ToggleCameraAsync()
        {
            if (_isVideoActive)
            {
                EndVideoInternal(showMessage: true);
                return;
            }

            if (state != State.Found || kuznechik == null)
            {
                _viewModel.AddSystemMessage("Сначала найдите собеседника.");
                return;
            }

            if (_directPeer?.IsConnected != true)
            {
                _viewModel.AddSystemMessage("Видео доступно после установки прямого канала (P2P).");
                return;
            }

            await StartVideoInviteAsync();
        }

        private async void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isInCall)
                return;

            try
            {
                await SendCallEndRouteAsync();
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
            ApplyCameraButtonVisuals();

            _voiceCall = CreateVoiceCall(microphoneEnabled: true);
            ApplyVoiceKeysIfReady();
            _voicePort = _voiceCall.BindLocalPort(_voicePort);
            _voiceCall.SetRemote(_incomingCallerIP, _incomingCallerPort);
            _myIP = GetLocalIPAddress();

            try
            {
                await SendCallAnswerRouteAsync(true, _myIP, _voicePort);
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
                await SendCallAnswerRouteAsync(false, "", 0);
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
            if (_isVideoActive)
                EndVideoInternal(showMessage: false);

            _callTones.Stop();
            _voiceCall?.HangUp();
            _voiceCall?.Dispose();
            _voiceCall = null;
            _isInCall = false;
            _isMuted = false;
            _isPeerMuted = false;
            _viewModel.IsIncomingCallVisible = false;
            _viewModel.ShowIdleCall();

            ApplyMuteButtonVisuals();
            ApplyCameraButtonVisuals();
            MyGlowBorder.Opacity = 0;
            PeerGlowBorder.Opacity = 0;
            SetMyVoiceStatus(null);
            PeerVoiceStatus.Text = "В голосовом чате";
            PeerVoiceStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 155, 164));
            UpdateVideoTilePresentation();

            if (_voiceSessionKey != null)
                Array.Clear(_voiceSessionKey, 0, _voiceSessionKey.Length);
            if (_voiceIV != null)
                Array.Clear(_voiceIV, 0, _voiceIV.Length);

            _voiceSessionKey = null;
            _voiceIV = null;
        }

        private byte[] ProtectVoiceKeys(byte[] sessionKey, byte[] iv)
        {
            if (kuznechik == null || aesKey == null)
                throw new InvalidOperationException("Сеансовый ключ шифрования ещё не выведен.");

            byte[] payload = new byte[48];
            Array.Copy(sessionKey, 0, payload, 0, 32);
            Array.Copy(iv, 0, payload, 32, 16);
            return kuznechik.KuzEncript(payload, aesKey);
        }

        private bool TryUnprotectVoiceKeys(byte[] blob, out byte[] sessionKey, out byte[] iv)
        {
            sessionKey = null;
            iv = null;
            if (kuznechik == null || aesKey == null || blob == null || blob.Length < 56)
                return false;

            byte[] plain = kuznechik.KuzDecript(blob, aesKey);
            if (plain == null || plain.Length != 48)
                return false;

            sessionKey = new byte[32];
            iv = new byte[16];
            Array.Copy(plain, 0, sessionKey, 0, 32);
            Array.Copy(plain, 32, iv, 0, 16);
            return true;
        }

        private async Task BeginDirectPeerSessionAsync()
        {
            TearDownDirectLink(notifyPeer: false);
            _directP2PActive = false;
            _directPeer = new DirectPeerLink();
            WireDirectPeerEvents(_directPeer);
            _directLinkCts = new CancellationTokenSource();

            string ip = GetDirectLinkAdvertiseAddress();
            bool loopbackListen = ip == "127.0.0.1";

            if (ID < _peerId)
            {
                int listenPort = AllocateDirectListenPort();
                _activeDirectListenPort = listenPort;
                try
                {
                    _directPeer.StartListening(listenPort, loopbackListen);
                }
                catch (Exception ex)
                {
                    NotifyTransportStatus("Не удалось открыть порт прямого канала: " + ex.Message);
                    _ = ScheduleDirectReconnectAsync();
                    return;
                }

                try
                {
                    await client.RegisterDirectEndpointAsync(ID, ip, listenPort);
                    await client.RefreshDirectLinkAsync(ID);
                }
                catch (Exception ex)
                {
                    NotifyTransportStatus("Не удалось зарегистрировать прямой канал: " + ex.Message);
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _directPeer.AcceptIncomingAsync(_directLinkCts.Token).ConfigureAwait(false);
                        await client.NotifyDirectLinkReadyAsync(ID).ConfigureAwait(false);
                        Dispatcher.BeginInvoke(new Action(OnDirectLinkReady));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                            NotifyTransportStatus("Прямой канал: " + ex.Message)));
                        if (state == State.Found && !_isClosing)
                            _ = ScheduleDirectReconnectAsync();
                    }
                });
            }
            else
            {
                try
                {
                    await client.RegisterDirectEndpointAsync(ID, ip, 0);
                    await client.RefreshDirectLinkAsync(ID);
                }
                catch (Exception ex)
                {
                    NotifyTransportStatus("Не удалось зарегистрировать прямой канал: " + ex.Message);
                }

                TryConnectDirectPeer();
            }
        }

        private int AllocateDirectListenPort()
        {
            return GetDirectPortFromFile();
        }

        private void OnEstablishDirectLink(string host, int port)
        {
            _pendingDirectHost = host;
            _pendingDirectPort = port;
            TryConnectDirectPeer();
        }

        private void TryConnectDirectPeer()
        {
            if (_directPeer == null || _peerId < 0 || ID < _peerId)
                return;
            if (string.IsNullOrWhiteSpace(_pendingDirectHost) || _pendingDirectPort <= 0)
                return;
            if (_directP2PActive || _directConnectInProgress)
                return;

            _ = ConnectDirectClientAsync(_pendingDirectHost, _pendingDirectPort);
        }

        private async Task ConnectDirectClientAsync(string host, int port)
        {
            if (_directConnectInProgress || _directP2PActive)
                return;

            _directConnectInProgress = true;
            try
            {
                bool connected = false;
                for (int attempt = 0; attempt < 8 && !connected; attempt++)
                {
                    if (attempt > 0)
                    {
                        try
                        {
                            await client.RefreshDirectLinkAsync(ID).ConfigureAwait(false);
                        }
                        catch
                        {
                        }

                        await Task.Delay(250 + attempt * 150).ConfigureAwait(false);
                    }

                    if (_directLinkCts == null || _directPeer == null || state != State.Found)
                        return;

                    int targetPort = _pendingDirectPort > 0 ? _pendingDirectPort : port;
                    string targetHost = string.IsNullOrWhiteSpace(_pendingDirectHost) ? host : _pendingDirectHost;

                    connected = await TryConnectDirectAsync("127.0.0.1", targetPort).ConfigureAwait(false);
                    if (!connected && !string.IsNullOrWhiteSpace(targetHost)
                        && !string.Equals(targetHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        connected = await TryConnectDirectAsync(targetHost, targetPort).ConfigureAwait(false);
                    }
                }

                if (!connected)
                    throw new InvalidOperationException("Не удалось установить прямое TCP-соединение.");

                await client.NotifyDirectLinkReadyAsync(ID);
                OnDirectLinkReady();
            }
            catch (Exception ex)
            {
                NotifyTransportStatus("Прямой канал: " + ex.Message);
                if (state == State.Found && !_isClosing)
                    _ = ScheduleDirectReconnectAsync();
            }
            finally
            {
                _directConnectInProgress = false;
            }
        }

        private async Task<bool> TryConnectDirectAsync(string host, int port)
        {
            try
            {
                await _directPeer.ConnectAsync(host, port, _directLinkCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private void OnDirectLinkReady()
        {
            _directP2PActive = true;
            if (state == State.Found)
            {
                _viewModel.IsInputEnabled = true;
                _viewModel.StatusText = "Собеседник на связи";
            }

            _viewModel.SetDirectLinkStatus(
                "Прямой канал активен: сообщения и сигнализация звонков идут между вами, сервер их не передаёт.");
        }

        private void NotifyTransportStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            _viewModel.SetDirectLinkStatus(message.Trim());
        }

        private void WireDirectPeerEvents(DirectPeerLink link)
        {
            link.SignedMessageReceived += (hmac, bytes, signature) =>
                Dispatcher.BeginInvoke(new Action(() => MessageCallBackSigned(hmac, string.Empty, bytes, signature)));
            link.CallRequestReceived += (callerIp, callerPort) =>
                Dispatcher.BeginInvoke(new Action(() => IncomingCall(_peerId, callerIp, callerPort)));
            link.CallAnswerReceived += (accept, ip, port) =>
                Dispatcher.BeginInvoke(new Action(() => CallAnswered(_peerId, accept, ip, port)));
            link.CallEndReceived += () =>
                Dispatcher.BeginInvoke(new Action(() => CallEnded(_peerId)));
            link.VoiceKeysReceived += (sessionKey, iv) =>
                Dispatcher.BeginInvoke(new Action(() => ReceiveVoiceKeys(_peerId, sessionKey, iv)));
            link.VideoKeysReceived += blob =>
                Dispatcher.BeginInvoke(new Action(() => OnDirectVideoKeys(blob)));
            link.VideoInviteReceived += (host, port) =>
                Dispatcher.BeginInvoke(new Action(() => OnDirectVideoInvite(host, port)));
            link.VideoAcceptReceived += (accept, host, port) =>
                Dispatcher.BeginInvoke(new Action(() => OnDirectVideoAccept(accept, host, port)));
            link.PeerLeftReceived += () =>
                Dispatcher.BeginInvoke(new Action(OnDirectPeerLeft));
            link.LinkStatusChanged += msg =>
                Dispatcher.BeginInvoke(new Action(() => NotifyTransportStatus(msg)));

            TryConnectDirectPeer();
        }

        private void OnDirectPeerLeft()
        {
            if (state != State.Found || _isClosing)
                return;

            _directP2PActive = false;
            NotifyTransportStatus("Прямой канал закрыт. Переподключение…");
            _ = ScheduleDirectReconnectAsync();
        }

        private async Task ScheduleDirectReconnectAsync()
        {
            int version = Interlocked.Increment(ref _directReconnectVersion);
            await Task.Delay(1000).ConfigureAwait(false);
            if (version != _directReconnectVersion || state != State.Found || _isClosing || _directP2PActive)
                return;

            await Dispatcher.InvokeAsync(async () =>
            {
                if (state == State.Found && !_directP2PActive)
                    await BeginDirectPeerSessionAsync();
            });
        }

        private string GetDirectLinkAdvertiseAddress()
        {
            if (_hubUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
                || _hubUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "127.0.0.1";
            }

            return GetLocalIPAddress();
        }

        private void TearDownDirectLink(bool notifyPeer)
        {
            Interlocked.Increment(ref _directReconnectVersion);

            try
            {
                if (notifyPeer && _directPeer?.IsConnected == true)
                    _directPeer.SendPeerLeft();
            }
            catch
            {
            }

            try { _directLinkCts?.Cancel(); } catch { }
            _directLinkCts?.Dispose();
            _directLinkCts = null;
            _directPeer?.Dispose();
            _directPeer = null;
            _directP2PActive = false;

            if (notifyPeer)
            {
                _pendingDirectHost = null;
                _pendingDirectPort = 0;
                _viewModel.ClearDirectLinkStatus();
            }
        }

        private async Task SendSignedMessageRouteAsync(byte[] hmac, byte[] encrypted, byte[] signature)
        {
            if (_directPeer?.IsConnected == true)
            {
                _directPeer.SendSignedMessage(hmac, encrypted, signature);
                return;
            }

            if (_directP2PActive)
            {
                _directP2PActive = false;
                await ResetServerDirectRelayAsync().ConfigureAwait(false);
            }

            if (CanTalkToServer)
                await client.SendSignedMessageAsync(hmac, encrypted, signature, ID).ConfigureAwait(false);
        }

        private async Task ResetServerDirectRelayAsync()
        {
            if (!CanTalkToServer || _peerId < 0)
                return;

            try
            {
                string ip = GetDirectLinkAdvertiseAddress();
                int port = ID < _peerId ? _activeDirectListenPort : 0;
                await client.RegisterDirectEndpointAsync(ID, ip, port).ConfigureAwait(false);
                await client.RefreshDirectLinkAsync(ID).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private Task SendCallRequestRouteAsync(string callerIp, int callerPort)
        {
            if (_directPeer?.IsConnected == true)
            {
                _directPeer.SendCallRequest(callerIp, callerPort);
                return Task.CompletedTask;
            }

            if (CanTalkToServer)
                return client.SendCallRequestAsync(ID, callerIp, callerPort);

            return Task.CompletedTask;
        }

        private Task SendCallAnswerRouteAsync(bool accept, string answererIp, int answererPort)
        {
            if (_directPeer?.IsConnected == true)
            {
                _directPeer.SendCallAnswer(accept, answererIp, answererPort);
                return Task.CompletedTask;
            }

            if (CanTalkToServer)
                return client.SendCallAnswerAsync(ID, accept, answererIp, answererPort);

            return Task.CompletedTask;
        }

        private Task SendCallEndRouteAsync()
        {
            if (_directPeer?.IsConnected == true)
            {
                _directPeer.SendCallEnd();
                return Task.CompletedTask;
            }

            if (CanTalkToServer)
                return client.SendCallEndAsync(ID);

            return Task.CompletedTask;
        }

        private Task SendVoiceKeysRouteAsync(byte[] sessionKey, byte[] iv)
        {
            if (_directPeer?.IsConnected == true)
            {
                _directPeer.SendVoiceKeys(sessionKey, iv);
                return Task.CompletedTask;
            }

            if (CanTalkToServer)
                return client.SendVoiceKeysAsync(ID, sessionKey, iv);

            return Task.CompletedTask;
        }

        private async void VideoButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleCameraAsync();
        }

        private void EndVideoButton_Click(object sender, RoutedEventArgs e)
        {
            EndVideoInternal(showMessage: true);
        }

        private async Task StartVideoInviteAsync()
        {
            try
            {
                await GenerateVideoSessionKeysAsync();
                EnsureVideoCall();
                _videoPort = _videoCall.BindLocalPort(_videoPort);
                string ip = GetLocalIPAddress();

                SendVideoKeysRoute(ProtectVideoSession(_videoSessionKey, _videoIV, _videoMacKey));
                SendVideoInviteRoute(ip, _videoPort);
                ApplyVideoSessionKeys();
                _videoCall.Start();
                _isVideoActive = true;
                ShowVideoOverlay(true);
                ApplyCameraButtonVisuals();
                NotifyTransportStatus("Видеозвонок: приглашение отправлено (ГОСТ).");
            }
            catch (Exception ex)
            {
                _viewModel.AddSystemMessage("Видео: " + ex.Message);
                EndVideoInternal(showMessage: false);
            }
        }

        private void OnDirectVideoKeys(byte[] blob)
        {
            if (!TryUnprotectVideoSession(blob, out byte[] key, out byte[] iv, out byte[] macKey))
            {
                _viewModel.AddSystemMessage("Видео: не удалось принять ключи сессии.");
                return;
            }

            _videoSessionKey = key;
            _videoIV = iv;
            _videoMacKey = macKey;
            ApplyVideoSessionKeys();

            if (!string.IsNullOrEmpty(_pendingVideoHost) && _pendingVideoPort > 0 && !_isVideoActive)
                _ = AcceptVideoInviteAsync();
        }

        private void OnDirectVideoInvite(string host, int port)
        {
            _pendingVideoHost = host;
            _pendingVideoPort = port;

            if (_videoSessionKey == null)
            {
                NotifyTransportStatus("Видео: ожидание ключей ГОСТ…");
                return;
            }

            _ = AcceptVideoInviteAsync();
        }

        private async Task AcceptVideoInviteAsync()
        {
            try
            {
                EnsureVideoCall();
                _videoPort = _videoCall.BindLocalPort(_videoPort);
                _videoCall.SetRemote(_pendingVideoHost, _pendingVideoPort);
                ApplyVideoSessionKeys();
                _videoCall.Start();
                _isVideoActive = true;
                ShowVideoOverlay(true);
                ApplyCameraButtonVisuals();

                string ip = GetLocalIPAddress();
                SendVideoAcceptRoute(true, ip, _videoPort);
                NotifyTransportStatus("Видео: соединение P2P (Kuznechik + Streebog).");
            }
            catch (Exception ex)
            {
                _viewModel.AddSystemMessage("Видео: " + ex.Message);
                SendVideoAcceptRoute(false, string.Empty, 0);
                EndVideoInternal(showMessage: false);
            }
        }

        private void OnDirectVideoAccept(bool accept, string host, int port)
        {
            if (!accept)
            {
                _viewModel.AddSystemMessage("Собеседник отклонил видео.");
                EndVideoInternal(showMessage: false);
                return;
            }

            _videoCall?.SetRemote(host, port);
        }

        private async Task GenerateVideoSessionKeysAsync()
        {
            _videoSessionKey = new byte[32];
            _videoIV = new byte[16];
            _videoMacKey = new byte[32];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_videoSessionKey);
                rng.GetBytes(_videoIV);
                rng.GetBytes(_videoMacKey);
            }

            await Task.CompletedTask;
        }

        private void EnsureVideoCall()
        {
            if (_videoCall != null)
                return;

            _videoCall = new SimpleVideoCall(Dispatcher);
            _videoCall.LocalFrameCaptured += src =>
                Dispatcher.BeginInvoke(new Action(() => ApplyLocalVideoFrame(src)));
            _videoCall.RemoteFrameReceived += src =>
                Dispatcher.BeginInvoke(new Action(() => ApplyRemoteVideoFrame(src)));
            _videoCall.StatusChanged += NotifyTransportStatus;
        }

        private void ApplyLocalVideoFrame(ImageSource source)
        {
            LocalVideoImage.Source = source;
            OverlayLocalVideoImage.Source = source;
            UpdateVideoTilePresentation();
        }

        private void ApplyRemoteVideoFrame(ImageSource source)
        {
            RemoteVideoImage.Source = source;
            OverlayRemoteVideoImage.Source = source;
            UpdateVideoTilePresentation();
        }

        private void UpdateVideoTilePresentation()
        {
            bool showTiles = _isVideoActive && _viewModel.IsCallActive;
            var visible = showTiles ? Visibility.Visible : Visibility.Collapsed;
            var hidden = showTiles ? Visibility.Collapsed : Visibility.Visible;

            LocalVideoImage.Visibility = LocalVideoImage.Source != null && showTiles
                ? Visibility.Visible
                : Visibility.Collapsed;
            RemoteVideoImage.Visibility = RemoteVideoImage.Source != null && showTiles
                ? Visibility.Visible
                : Visibility.Collapsed;

            MyAvatarBorder.Visibility = LocalVideoImage.Visibility == Visibility.Visible ? hidden : visible;
            PeerAvatarBorder.Visibility = RemoteVideoImage.Visibility == Visibility.Visible ? hidden : visible;
        }

        private void ApplyVideoSessionKeys()
        {
            if (_videoCall == null || _videoSessionKey == null || _videoIV == null || _videoMacKey == null)
                return;

            _videoCall.SetSessionKeys(_videoSessionKey, _videoIV, _videoMacKey);
        }

        private void ShowVideoOverlay(bool visible)
        {
            if (_viewModel.IsCallActive)
            {
                VideoOverlay.Visibility = Visibility.Collapsed;
                UpdateVideoTilePresentation();
                ApplyCameraButtonVisuals();
                return;
            }

            VideoOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                LocalVideoImage.Source = null;
                RemoteVideoImage.Source = null;
                OverlayLocalVideoImage.Source = null;
                OverlayRemoteVideoImage.Source = null;
            }

            UpdateVideoTilePresentation();
            ApplyCameraButtonVisuals();
        }

        private void EndVideoInternal(bool showMessage)
        {
            _videoCall?.Stop();
            _videoCall?.Dispose();
            _videoCall = null;
            _isVideoActive = false;
            ShowVideoOverlay(false);

            if (_videoSessionKey != null)
                Array.Clear(_videoSessionKey, 0, _videoSessionKey.Length);
            if (_videoIV != null)
                Array.Clear(_videoIV, 0, _videoIV.Length);
            if (_videoMacKey != null)
                Array.Clear(_videoMacKey, 0, _videoMacKey.Length);

            _videoSessionKey = null;
            _videoIV = null;
            _videoMacKey = null;

            if (showMessage)
                _viewModel.AddSystemMessage("Видеозвонок завершён.");
        }

        private byte[] ProtectVideoSession(byte[] sessionKey, byte[] iv, byte[] macKey)
        {
            if (kuznechik == null || aesKey == null)
                throw new InvalidOperationException("Сеансовый ключ чата ещё не выведен.");

            byte[] payload = new byte[80];
            Array.Copy(sessionKey, 0, payload, 0, 32);
            Array.Copy(iv, 0, payload, 32, 16);
            Array.Copy(macKey, 0, payload, 48, 32);
            return kuznechik.KuzEncript(payload, aesKey);
        }

        private bool TryUnprotectVideoSession(byte[] blob, out byte[] sessionKey, out byte[] iv, out byte[] macKey)
        {
            sessionKey = null;
            iv = null;
            macKey = null;
            if (blob == null || kuznechik == null || aesKey == null)
                return false;

            byte[] plain = kuznechik.KuzDecript(blob, aesKey);
            if (plain == null || plain.Length != 80)
                return false;

            sessionKey = new byte[32];
            iv = new byte[16];
            macKey = new byte[32];
            Array.Copy(plain, 0, sessionKey, 0, 32);
            Array.Copy(plain, 32, iv, 0, 16);
            Array.Copy(plain, 48, macKey, 0, 32);
            return true;
        }

        private void SendVideoKeysRoute(byte[] protectedBlob)
        {
            if (_directPeer?.IsConnected == true)
                _directPeer.SendVideoKeys(protectedBlob);
        }

        private void SendVideoInviteRoute(string host, int port)
        {
            if (_directPeer?.IsConnected == true)
                _directPeer.SendVideoInvite(host, port);
        }

        private void SendVideoAcceptRoute(bool accept, string host, int port)
        {
            if (_directPeer?.IsConnected == true)
                _directPeer.SendVideoAccept(accept, host, port);
        }

        private int GetVideoPortFromFile()
        {
            try
            {
                if (File.Exists(VideoPortFile))
                {
                    string content = File.ReadAllText(VideoPortFile).Trim();
                    if (int.TryParse(content, out int port) && port >= VideoPortMin && port <= VideoPortMax)
                    {
                        int nextPort = port >= VideoPortMax ? VideoPortMin : port + 1;
                        File.WriteAllText(VideoPortFile, nextPort.ToString());
                        return port;
                    }
                }

                int newPort = new Random().Next(VideoPortMin, VideoPortMax);
                File.WriteAllText(VideoPortFile, (newPort + 1).ToString());
                return newPort;
            }
            catch
            {
                return new Random().Next(VideoPortMin, VideoPortMax);
            }
        }

        private int GetDirectPortFromFile()
        {
            try
            {
                if (File.Exists(DirectPortFile))
                {
                    string content = File.ReadAllText(DirectPortFile).Trim();
                    if (int.TryParse(content, out int port) && port >= DirectPortMin && port <= DirectPortMax)
                    {
                        int nextPort = port >= DirectPortMax ? DirectPortMin : port + 1;
                        File.WriteAllText(DirectPortFile, nextPort.ToString());
                        return port;
                    }
                }

                int newPort = new Random().Next(DirectPortMin, DirectPortMax);
                File.WriteAllText(DirectPortFile, (newPort + 1).ToString());
                return newPort;
            }
            catch
            {
                return new Random().Next(DirectPortMin, DirectPortMax);
            }
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

            await SendVoiceKeysRouteAsync(ProtectVoiceKeys(_voiceSessionKey, _voiceIV), Array.Empty<byte>());
        }

        public void ReceiveVoiceKeys(int fromUserId, byte[] sessionKey, byte[] iv)
        {
            if (!TryUnprotectVoiceKeys(sessionKey, out byte[] key, out byte[] voiceIv))
            {
                _viewModel.AddSystemMessage("Не удалось принять ключи голоса");
                return;
            }

            _voiceSessionKey = key;
            _voiceIV = voiceIv;
            ApplyVoiceKeysIfReady();
            if (_isInCall)
                _voiceCall?.BeginLocalAudio();
        }

        private SimpleVoiceCall CreateVoiceCall(bool microphoneEnabled)
        {
            var call = new SimpleVoiceCall(_directP2PActive ? null : client, ID);
            call.SetMicrophoneEnabled(microphoneEnabled);
            call.SetEncryptionEnabled(true);
            call.OnStatusChanged += msg => Dispatcher.BeginInvoke(new Action(() => NotifyTransportStatus(msg)));
            call.OnVolumeChanged += level =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MyGlowBorder.Opacity = level > 0.05f && !_isMuted ? 1 : 0;
                    if (level > 0.08f && !_isMuted)
                        SetMyVoiceStatus("Говорите…");
                    else if (!_isMuted)
                        SetMyVoiceStatus(null);
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
