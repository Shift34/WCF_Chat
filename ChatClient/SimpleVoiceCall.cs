using ChatClient.ProtocolSignal;
using NAudio.Wave;
using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    public class SimpleVoiceCall : IDisposable
    {
        private const int SampleRate = 16000;
        private const int BytesPerMillisecond = SampleRate * 2 / 1000;
        private const int MaxBufferMs = 120;
        private const int PrerollMs = 40;

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
        private bool _sameMachine;
        private readonly object _cryptoLock = new object();
        private readonly VoiceCleanup _cleanup = new VoiceCleanup();
        private readonly byte[] _captureCopy = new byte[SampleRate];

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
                BufferMilliseconds = 20,
                NumberOfBuffers = 4
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
                DesiredLatency = 60,
                NumberOfBuffers = 3
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
            _sameMachine = IsThisMachine(address);
            if (_sameMachine)
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
            _cleanup.Reset();
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
            if (_udpPathAnnounced)
                return;
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
            _udpPathAnnounced = false;
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
            if (!_isActive || e.BytesRecorded <= 0 || (e.BytesRecorded & 1) != 0)
                return;

            try
            {
                if (_encryptionEnabled && _sendCrypto == null)
                    return;

                if (e.BytesRecorded > _captureCopy.Length)
                    return;

                Array.Copy(e.Buffer, 0, _captureCopy, 0, e.BytesRecorded);
                _cleanup.ProcessCapture(_captureCopy, e.BytesRecorded, _sameMachine);

                if ((_volumeTick++ & 3) == 0)
                    ReportVolume(_captureCopy, e.BytesRecorded);

                long packetNumber = Interlocked.Increment(ref _packetCounter) - 1;
                byte[] packet = new byte[e.BytesRecorded + 10];
                packet[0] = 0x01;
                bool encrypt = _encryptionEnabled && _sendCrypto != null;
                packet[1] = (byte)(encrypt ? 0x01 : 0x00);
                Array.Copy(BitConverter.GetBytes(packetNumber), 0, packet, 2, 8);
                Array.Copy(_captureCopy, 0, packet, 10, e.BytesRecorded);

                if (encrypt)
                {
                    lock (_cryptoLock)
                        _sendCrypto.XorPacket(packet, 10, e.BytesRecorded, packetNumber);
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

                if (_lastPlayedPacket >= 0 && packetNumber > _lastPlayedPacket + 1)
                    FillGap(packetNumber - _lastPlayedPacket - 1, payloadLength);

                _lastPlayedPacket = packetNumber;

                _cleanup.ProcessPlayback(audio, audio.Length, _sameMachine);

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

        private void FillGap(long missingPackets, int frameBytes)
        {
            int packets = (int)Math.Min(missingPackets, 4);
            if (packets <= 0 || frameBytes <= 0)
                return;

            int bytes = packets * frameBytes;
            int room = MaxBufferMs * BytesPerMillisecond - _waveProvider.BufferedBytes;
            if (room <= 0)
                return;

            bytes = Math.Min(bytes, room);
            bytes &= ~1;
            if (bytes <= 0)
                return;

            _waveProvider.AddSamples(new byte[bytes], 0, bytes);
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

    internal sealed class VoiceCleanup
    {
        private const float DcR = 0.996f;
        private const float EnvAttack = 0.40f;
        private const float EnvRelease = 0.12f;
        private const float GateOpen = 0.032f;
        private const float GateClose = 0.016f;
        private const float GainAttack = 0.22f;
        private const float GainRelease = 0.05f;
        private const float PlayDecay = 0.86f;

        private float _capX;
        private float _capY;
        private float _playX;
        private float _playY;
        private float _env;
        private float _gain;
        private float _playbackEnv;
        private bool _open;
        private readonly object _lock = new object();

        public void Reset()
        {
            lock (_lock)
            {
                _capX = 0;
                _capY = 0;
                _playX = 0;
                _playY = 0;
                _env = 0;
                _gain = 0;
                _playbackEnv = 0;
                _open = false;
            }
        }

        public void ProcessCapture(byte[] buffer, int length, bool sameMachine)
        {
            lock (_lock)
            {
            float extra = _playbackEnv * (sameMachine ? 1.15f : 0.55f);
            if (sameMachine)
                extra += 0.018f;

            float openAt = GateOpen + extra;
            float closeAt = Math.Max(GateClose, openAt * 0.5f);
            float peak = 0f;

            for (int i = 0; i + 1 < length; i += 2)
            {
                float x = BitConverter.ToInt16(buffer, i) / 32768f;
                float y = DcR * (_capY + x - _capX);
                _capX = x;
                _capY = y;
                float a = Math.Abs(y);
                if (a > peak)
                    peak = a;

                float target = _open ? 1f : 0f;
                float speed = target > _gain ? GainAttack : GainRelease;
                _gain += (target - _gain) * speed;
                y *= _gain;
                y = SoftLimit(y);
                WriteSample(buffer, i, y);
            }

            if (peak > _env)
                _env += (peak - _env) * EnvAttack;
            else
                _env += (peak - _env) * EnvRelease;

            if (_open)
            {
                if (_env < closeAt)
                    _open = false;
            }
            else if (_env > openAt)
            {
                _open = true;
            }

            _playbackEnv *= PlayDecay;
            }
        }

        public void ProcessPlayback(byte[] buffer, int length, bool sameMachine)
        {
            lock (_lock)
            {
            float peak = 0f;
            float duck = sameMachine ? 0.82f : 1f;

            for (int i = 0; i + 1 < length; i += 2)
            {
                float x = BitConverter.ToInt16(buffer, i) / 32768f;
                float y = DcR * (_playY + x - _playX);
                _playX = x;
                _playY = y;
                y *= duck;
                y = SoftLimit(y);
                float a = Math.Abs(y);
                if (a > peak)
                    peak = a;
                WriteSample(buffer, i, y);
            }

            if (peak > _playbackEnv)
                _playbackEnv = peak;
            else
                _playbackEnv = Math.Max(peak, _playbackEnv * PlayDecay);
            }
        }

        private static float SoftLimit(float x)
        {
            const float t = 0.88f;
            float a = Math.Abs(x);
            if (a <= t)
                return x;

            float sign = x < 0 ? -1f : 1f;
            return sign * (t + (1f - t) * (a - t) / (a - t + 0.35f));
        }

        private static void WriteSample(byte[] buffer, int offset, float sample)
        {
            if (sample > 1f)
                sample = 1f;
            else if (sample < -1f)
                sample = -1f;

            short value = (short)(sample * 32767f);
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }

    internal sealed class VoicePacketCrypto : IDisposable
    {
        private readonly KuznechikStreaming _stream;

        public VoicePacketCrypto(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("Ключ должен быть 32 байта.", nameof(key));
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV должен быть 16 байт.", nameof(iv));

            _stream = new KuznechikStreaming(key);
            _stream.InitSession(iv);
        }

        public void XorPacket(byte[] data, int offset, int length, long packetNumber)
        {
            _stream.XorPacket(data, offset, length, packetNumber);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}
