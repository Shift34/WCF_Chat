using NAudio.Wave;
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    public class SimpleVoiceCall : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BytesPerMillisecond = SampleRate * 2 / 1000;
        private const int MaxBufferMs = 80;
        private const int PrerollMs = 30;

        private UdpClient _udpClient;
        private WaveInEvent _microphone;
        private IWavePlayer _speaker;
        private BufferedWaveProvider _waveProvider;
        private VoicePacketCrypto _sendCrypto;
        private VoicePacketCrypto _receiveCrypto;
        private ChatHubClient _serviceClient;
        private readonly int _userId;
        private IPEndPoint _remoteEndPoint;
        private bool _isActive;
        private bool _playbackStarted;
        private bool _useMicrophone = true;
        private bool _encryptionEnabled = true;
        private long _packetCounter;
        private long _lastPlayedPacket = -1;
        private int _volumeTick;
        private int _lastUdpReceiveTicks;
        private bool _udpPathAnnounced;
        private readonly object _cryptoLock = new object();

        public event Action<string> OnStatusChanged;
        public event Action<float> OnVolumeChanged;

        public SimpleVoiceCall(ChatHubClient serviceClient, int userId)
        {
            _serviceClient = serviceClient;
            _userId = userId;

            var format = new WaveFormat(SampleRate, 16, 1);

            _microphone = new WaveInEvent
            {
                WaveFormat = format,
                BufferMilliseconds = 10,
                NumberOfBuffers = 3
            };
            _microphone.DataAvailable += OnMicrophoneData;

            _waveProvider = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false,
                BufferLength = MaxBufferMs * BytesPerMillisecond * 2
            };

            var waveOut = new WaveOutEvent
            {
                DesiredLatency = 40,
                NumberOfBuffers = 2
            };
            waveOut.Init(_waveProvider);
            _speaker = waveOut;
        }

        public void SetMicrophoneEnabled(bool enabled)
        {
            _useMicrophone = enabled;
        }

        public void SetEncryptionEnabled(bool enabled)
        {
            _encryptionEnabled = enabled;
        }

        public void SetEncryptionKey(byte[] sessionKey, byte[] iv)
        {
            lock (_cryptoLock)
            {
                _sendCrypto?.Dispose();
                _receiveCrypto?.Dispose();
                _sendCrypto = new VoicePacketCrypto(sessionKey, iv);
                _receiveCrypto = new VoicePacketCrypto(sessionKey, iv);
            }
        }

        public int BindLocalPort(int preferredPort)
        {
            CloseUdp();
            for (int port = preferredPort; port < preferredPort + 30; port++)
            {
                try
                {
                    _udpClient = CreateUdp(port);
                    _ = ReceiveUdpAsync();
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            _udpClient = CreateUdp(0);
            _ = ReceiveUdpAsync();
            return ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;
        }

        public void SetRemote(string ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0)
                return;

            IPAddress address = IPAddress.Parse(ip);
            if (IsThisMachine(address))
                address = IPAddress.Loopback;

            _remoteEndPoint = new IPEndPoint(address, port);
            PunchHole();
        }

        public void BeginLocalAudio()
        {
            if (_isActive)
                return;

            _isActive = true;
            _waveProvider.ClearBuffer();
            _playbackStarted = false;
            _lastPlayedPacket = -1;
            if (_useMicrophone)
                _microphone.StartRecording();
        }

        public Task StartCall(string remoteIP, int port)
        {
            SetRemote(remoteIP, port);
            BeginLocalAudio();
            return Task.CompletedTask;
        }

        public Task AcceptCall(int listenPort)
        {
            BeginLocalAudio();
            return Task.CompletedTask;
        }

        public void ReceiveVoice(byte[] data)
        {
            if (unchecked(Environment.TickCount - _lastUdpReceiveTicks) < 400)
                return;

            PlayPacket(data);
        }

        public void SetMute(bool mute)
        {
            if (mute)
                _microphone?.StopRecording();
            else if (_useMicrophone)
                _microphone?.StartRecording();
        }

        public void SetSpeakerVolume(float volume)
        {
            volume = Math.Max(0, Math.Min(1, volume));
            if (_speaker is WaveOutEvent waveOut)
                waveOut.Volume = volume;
        }

        public void HangUp()
        {
            _isActive = false;
            _playbackStarted = false;
            try { _microphone?.StopRecording(); } catch { }
            try { _speaker?.Stop(); } catch { }
            try { _waveProvider?.ClearBuffer(); } catch { }
            CloseUdp();
        }

        public void Dispose()
        {
            HangUp();
            _microphone?.Dispose();
            _speaker?.Dispose();
            _sendCrypto?.Dispose();
            _receiveCrypto?.Dispose();
        }

        private static UdpClient CreateUdp(int port)
        {
            var client = port == 0 ? new UdpClient(0) : new UdpClient(port);
            client.Client.SendBufferSize = 65536;
            client.Client.ReceiveBufferSize = 65536;
            return client;
        }

        private void PunchHole()
        {
            if (_udpClient == null || _remoteEndPoint == null)
                return;

            try
            {
                _udpClient.Send(new byte[] { 0x00 }, 1, _remoteEndPoint);
            }
            catch
            {
            }
        }

        private void OnMicrophoneData(object sender, WaveInEventArgs e)
        {
            if (!_isActive || e.BytesRecorded <= 0)
                return;

            try
            {
                if ((_volumeTick++ & 3) == 0)
                    ReportVolume(e.Buffer, e.BytesRecorded);

                long packetNumber = Interlocked.Increment(ref _packetCounter) - 1;
                byte[] packet = new byte[e.BytesRecorded + 10];
                packet[0] = 0x01;
                packet[1] = (byte)(_encryptionEnabled ? 0x01 : 0x00);
                Array.Copy(BitConverter.GetBytes(packetNumber), 0, packet, 2, 8);
                Array.Copy(e.Buffer, 0, packet, 10, e.BytesRecorded);

                if (_encryptionEnabled)
                {
                    lock (_cryptoLock)
                        _sendCrypto?.XorPacket(packet, 10, e.BytesRecorded, packetNumber);
                }

                if (_udpClient != null && _remoteEndPoint != null)
                    _udpClient.Send(packet, packet.Length, _remoteEndPoint);

                if (!_udpPathAnnounced)
                    _serviceClient?.RelayVoice(_userId, packet);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка отправки голоса: " + ex.Message);
            }
        }

        private void ReportVolume(byte[] buffer, int length)
        {
            float maxLevel = 0;
            for (int i = 0; i + 1 < length; i += 32)
            {
                float level = Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768f;
                if (level > maxLevel)
                    maxLevel = level;
            }
            OnVolumeChanged?.Invoke(maxLevel);
        }

        private async Task ReceiveUdpAsync()
        {
            UdpClient client = _udpClient;
            while (client != null)
            {
                try
                {
                    UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
                    if (result.Buffer == null || result.Buffer.Length < 12)
                        continue;

                    if (_remoteEndPoint == null)
                        _remoteEndPoint = result.RemoteEndPoint;

                    _lastUdpReceiveTicks = Environment.TickCount;
                    if (!_udpPathAnnounced)
                    {
                        _udpPathAnnounced = true;
                        OnStatusChanged?.Invoke("Голос: прямой UDP");
                    }

                    PlayPacket(result.Buffer);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_udpClient == null)
                        break;
                }
                catch
                {
                    if (_udpClient == null)
                        break;
                }
            }
        }

        private void PlayPacket(byte[] packet)
        {
            if (packet == null || packet.Length < 12 || _waveProvider == null)
                return;

            try
            {
                byte encrypted = packet[1];
                long packetNumber = BitConverter.ToInt64(packet, 2);
                if (packetNumber <= _lastPlayedPacket)
                    return;

                _lastPlayedPacket = packetNumber;

                int payloadLength = packet.Length - 10;
                if (payloadLength <= 0 || (payloadLength & 1) != 0)
                    return;

                byte[] audio = new byte[payloadLength];
                Array.Copy(packet, 10, audio, 0, payloadLength);

                if (encrypted == 0x01)
                {
                    if (!_encryptionEnabled)
                        return;

                    lock (_cryptoLock)
                    {
                        if (_receiveCrypto == null)
                            return;
                        _receiveCrypto.XorPacket(audio, 0, audio.Length, packetNumber);
                    }
                }

                int buffered = _waveProvider.BufferedBytes;
                if (buffered > MaxBufferMs * BytesPerMillisecond)
                    return;

                _waveProvider.AddSamples(audio, 0, audio.Length);
                StartPlaybackIfReady();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка воспроизведения голоса: " + ex.Message);
            }
        }

        private void StartPlaybackIfReady()
        {
            if (_playbackStarted || _speaker == null)
                return;

            if (_waveProvider.BufferedBytes < PrerollMs * BytesPerMillisecond)
                return;

            _speaker.Play();
            _playbackStarted = true;
        }

        private void CloseUdp()
        {
            UdpClient client = _udpClient;
            _udpClient = null;
            try { client?.Close(); } catch { }
            try { client?.Dispose(); } catch { }
        }

        private static bool IsThisMachine(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return true;

            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (UnicastIPAddressInformation unicast in network.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.Equals(address))
                            return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }

    internal sealed class VoicePacketCrypto : IDisposable
    {
        private readonly byte[] _iv;
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _gamma = new byte[16];

        public VoicePacketCrypto(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("Ключ должен быть 32 байта.", nameof(key));
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV должен быть 16 байт.", nameof(iv));

            _iv = (byte[])iv.Clone();
            _aes = Aes.Create();
            _aes.KeySize = 256;
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _encryptor = _aes.CreateEncryptor();
        }

        public void XorPacket(byte[] data, int offset, int length, long packetNumber)
        {
            Array.Copy(_iv, _counter, 16);
            byte[] number = BitConverter.GetBytes(packetNumber);
            for (int i = 0; i < 8; i++)
                _counter[i] ^= number[i];

            int processed = 0;
            while (processed < length)
            {
                _encryptor.TransformBlock(_counter, 0, 16, _gamma, 0);
                int block = Math.Min(16, length - processed);
                for (int i = 0; i < block; i++)
                    data[offset + processed + i] ^= _gamma[i];

                processed += block;
                for (int j = 15; j >= 8; j--)
                {
                    if (++_counter[j] != 0)
                        break;
                }
            }
        }

        public void Dispose()
        {
            _encryptor?.Dispose();
            _aes?.Dispose();
        }
    }
}
