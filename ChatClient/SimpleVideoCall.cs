using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChatClient
{
    public sealed class SimpleVideoCall : IDisposable
    {
        private const byte Magic = 0x02;
        private const int HeaderSize = 22;
        private const int MaxPayload = 1100;
        private const int CaptureWidth = 320;
        private const int CaptureHeight = 240;
        private const int TargetFpsMs = 100;

        private UdpClient _udpClient;
        private VideoCaptureDevice _camera;
        private IPEndPoint _remoteEndPoint;
        private GostVideoFrameCrypto _crypto;
        private readonly object _cryptoLock = new object();
        private readonly object _sendLock = new object();
        private long _frameSequence;
        private bool _active;
        private DateTime _lastCaptureUtc;
        private readonly Dictionary<long, FrameAssembly> _assemblies = new Dictionary<long, FrameAssembly>();
        private readonly Dispatcher _ui;

        public event Action<BitmapSource> RemoteFrameReceived;
        public event Action<BitmapSource> LocalFrameCaptured;
        public event Action<string> StatusChanged;

        public SimpleVideoCall(Dispatcher uiDispatcher)
        {
            _ui = uiDispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public int BindLocalPort(int preferredPort)
        {
            CloseUdp();
            for (int port = preferredPort; port < preferredPort + 30; port++)
            {
                try
                {
                    _udpClient = new UdpClient(port);
                    _ = ReceiveLoopAsync();
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            _udpClient = new UdpClient(0);
            _ = ReceiveLoopAsync();
            return ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;
        }

        public void SetRemote(string ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0)
                return;

            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
        }

        public void SetSessionKeys(byte[] sessionKey, byte[] iv, byte[] macKey)
        {
            lock (_cryptoLock)
            {
                _crypto?.Dispose();
                _crypto = new GostVideoFrameCrypto(sessionKey, iv, macKey);
            }
        }

        public void Start()
        {
            if (_active)
                return;

            _active = true;
            _lastCaptureUtc = DateTime.MinValue;
            EnsureCamera();
            if (_camera != null)
            {
                _camera.NewFrame += OnNewFrame;
                _camera.Start();
                StatusChanged?.Invoke("Видео: камера включена (ГОСТ Kuznechik + Streebog)");
            }
            else
            {
                StatusChanged?.Invoke("Видео: камера не найдена");
            }
        }

        public void Stop()
        {
            _active = false;
            if (_camera != null)
            {
                _camera.NewFrame -= OnNewFrame;
                try { _camera.SignalToStop(); _camera.WaitForStop(); } catch { }
            }

            lock (_assemblies)
                _assemblies.Clear();
        }

        public void Dispose()
        {
            Stop();
            CloseUdp();
            lock (_cryptoLock)
            {
                _crypto?.Dispose();
                _crypto = null;
            }
        }

        private void EnsureCamera()
        {
            if (_camera != null)
                return;

            var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            if (devices.Count == 0)
                return;

            _camera = new VideoCaptureDevice(devices[0].MonikerString);
            if (_camera.VideoCapabilities != null && _camera.VideoCapabilities.Length > 0)
                _camera.VideoResolution = _camera.VideoCapabilities[0];
        }

        private void OnNewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            if (!_active || _remoteEndPoint == null || _udpClient == null)
                return;

            if ((DateTime.UtcNow - _lastCaptureUtc).TotalMilliseconds < TargetFpsMs)
                return;

            _lastCaptureUtc = DateTime.UtcNow;

            try
            {
                using (Bitmap frame = (Bitmap)eventArgs.Frame.Clone())
                using (Bitmap scaled = new Bitmap(frame, CaptureWidth, CaptureHeight))
                using (MemoryStream jpegStream = new MemoryStream())
                {
                    scaled.Save(jpegStream, ImageFormat.Jpeg);
                    byte[] jpeg = jpegStream.ToArray();
                    PostLocalPreview(jpeg);
                    SendFrame(jpeg);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Видео: ошибка захвата — " + ex.Message);
            }
        }

        private void SendFrame(byte[] jpeg)
        {
            long sequence = Interlocked.Increment(ref _frameSequence);
            int fragmentCount = (jpeg.Length + MaxPayload - 1) / MaxPayload;

            for (int i = 0; i < fragmentCount; i++)
            {
                int offset = i * MaxPayload;
                int length = Math.Min(MaxPayload, jpeg.Length - offset);
                byte[] payload = new byte[length];
                Array.Copy(jpeg, offset, payload, 0, length);

                byte[] mac;
                lock (_cryptoLock)
                {
                    if (_crypto == null)
                        return;
                    _crypto.Protect(sequence, payload, 0, payload.Length);
                    mac = _crypto.ComputeMac(sequence, payload, 0, payload.Length);
                }

                byte[] packet = new byte[HeaderSize + length];
                packet[0] = Magic;
                packet[1] = 0x01;
                Array.Copy(BitConverter.GetBytes(sequence), 0, packet, 2, 8);
                Array.Copy(BitConverter.GetBytes((ushort)i), 0, packet, 10, 2);
                Array.Copy(BitConverter.GetBytes((ushort)fragmentCount), 0, packet, 12, 2);
                Array.Copy(mac, 0, packet, 14, 8);
                Array.Copy(payload, 0, packet, HeaderSize, length);

                lock (_sendLock)
                    _udpClient?.Send(packet, packet.Length, _remoteEndPoint);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            UdpClient client = _udpClient;
            while (client != null)
            {
                try
                {
                    UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
                    ProcessPacket(result.Buffer);
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
            }
        }

        private void ProcessPacket(byte[] packet)
        {
            if (packet == null || packet.Length < HeaderSize || packet[0] != Magic)
                return;

            long sequence = BitConverter.ToInt64(packet, 2);
            int fragmentIndex = BitConverter.ToUInt16(packet, 10);
            int fragmentCount = BitConverter.ToUInt16(packet, 12);
            if (fragmentCount <= 0 || fragmentIndex >= fragmentCount)
                return;

            int payloadLength = packet.Length - HeaderSize;
            if (payloadLength <= 0)
                return;

            byte[] mac = new byte[8];
            Array.Copy(packet, 14, mac, 0, 8);
            byte[] payload = new byte[payloadLength];
            Array.Copy(packet, HeaderSize, payload, 0, payloadLength);

            lock (_cryptoLock)
            {
                if (_crypto == null || !_crypto.VerifyMac(sequence, payload, 0, payload.Length, mac))
                    return;
                _crypto.Unprotect(sequence, payload, 0, payload.Length);
            }

            byte[] frame;
            lock (_assemblies)
            {
                if (!_assemblies.TryGetValue(sequence, out FrameAssembly assembly))
                {
                    assembly = new FrameAssembly(fragmentCount);
                    _assemblies[sequence] = assembly;
                }

                assembly.SetFragment(fragmentIndex, payload);
                if (!assembly.IsComplete)
                    return;

                frame = assembly.Build();
                _assemblies.Remove(sequence);
                TrimAssemblies();
            }

            PostRemotePreview(frame);
        }

        private void TrimAssemblies()
        {
            if (_assemblies.Count <= 8)
                return;

            var remove = new List<long>();
            foreach (long key in _assemblies.Keys)
            {
                if (remove.Count >= _assemblies.Count - 8)
                    remove.Add(key);
            }

            foreach (long key in remove)
                _assemblies.Remove(key);
        }

        private void PostLocalPreview(byte[] jpeg)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                BitmapSource src = LoadJpeg(jpeg);
                if (src != null)
                    LocalFrameCaptured?.Invoke(src);
            }));
        }

        private void PostRemotePreview(byte[] jpeg)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                BitmapSource src = LoadJpeg(jpeg);
                if (src != null)
                    RemoteFrameReceived?.Invoke(src);
            }));
        }

        private static BitmapSource LoadJpeg(byte[] jpeg)
        {
            try
            {
                using (var stream = new MemoryStream(jpeg))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }

        private void CloseUdp()
        {
            UdpClient client = _udpClient;
            _udpClient = null;
            try { client?.Close(); } catch { }
            try { client?.Dispose(); } catch { }
        }

        private sealed class FrameAssembly
        {
            private readonly byte[][] _fragments;
            private int _received;

            public FrameAssembly(int count)
            {
                _fragments = new byte[count][];
            }

            public bool IsComplete => _received >= _fragments.Length;

            public void SetFragment(int index, byte[] data)
            {
                if (index < 0 || index >= _fragments.Length || _fragments[index] != null)
                    return;
                _fragments[index] = data;
                _received++;
            }

            public byte[] Build()
            {
                using (var ms = new MemoryStream())
                {
                    for (int i = 0; i < _fragments.Length; i++)
                        ms.Write(_fragments[i], 0, _fragments[i].Length);
                    return ms.ToArray();
                }
            }
        }
    }
}
