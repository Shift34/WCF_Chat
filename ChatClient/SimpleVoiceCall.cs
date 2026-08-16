using ChatClient.ProtocolSignal;
using ChatClient.ServiceChat;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;


namespace ChatClient
{
    public class SimpleVoiceCall : IDisposable
    {
        private UdpClient _udpClient;
        private WaveInEvent _microphone;
        private WaveOutEvent _speaker;
        private BufferedWaveProvider _waveProvider;
        private bool _isActive = false;
        private IPEndPoint _remoteEndPoint;

        // Криптография
        private KuznechikStreaming _crypto;
        private byte[] _sessionKey;
        private byte[] _sessionIV;
        private bool _encryptionEnabled = true;

        // Оптимизация производительности
        private byte[] _encryptBuffer;
        private readonly object _cryptoLock = new object();
        private DateTime _lastKeyRotation;
        private TimeSpan _keyRotationInterval = TimeSpan.FromMinutes(5); // Смена ключа каждые 5 минут

        // События
        public event Action<string> OnStatusChanged;
        public event Action<float> OnVolumeChanged;
        public event Action OnEncryptionKeyRotated;

        private bool _useMicrophone = true;
        private IServiceChat _serviceClient;
        private int _userId;

        // Статистика
        private int _packetsSent = 0;
        private int _packetsReceived = 0;
        private long _bytesEncrypted = 0;
        private long _bytesDecrypted = 0;
        private long _packetCounter = 0;

        public void SetMicrophoneEnabled(bool enabled)
        {
            _useMicrophone = enabled;
            OnStatusChanged?.Invoke(enabled ? "🎤 Микрофон включен" : "🔇 Микрофон отключен");
        }

        public SimpleVoiceCall(IServiceChat serviceClient, int userId)
        {
            _serviceClient = serviceClient;
            _userId = userId;

            try
            {
                // Генерация криптографических ключей
                GenerateSessionKey();
                GenerateSessionIV();

                // Инициализация шифрования Кузнечик
                _crypto = new KuznechikStreaming(_sessionKey);
                _crypto.InitSession(_sessionIV);

                // Настройка микрофона (16kHz, 16bit, mono)
                _microphone = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50 // 50 мс буфер для минимальной задержки
                };
                _microphone.DataAvailable += OnMicrophoneData;

                // Настройка динамика
                _speaker = new WaveOutEvent
                {
                    DesiredLatency = 100 // 100 мс задержка воспроизведения
                };
                _waveProvider = new BufferedWaveProvider(new WaveFormat(16000, 16, 1));
                _waveProvider.DiscardOnBufferOverflow = true;
                _waveProvider.BufferLength = 16000 * 2; // 2 секунды буфера
                _speaker.Init(_waveProvider);

                // Выделяем буфер для шифрования
                _encryptBuffer = new byte[16000]; // 1 секунда аудио

                _lastKeyRotation = DateTime.Now;

                OnStatusChanged?.Invoke("✅ Готов к звонку (ГОСТ Кузнечик, 256 бит)");
                OnStatusChanged?.Invoke($"🔐 Шифрование активно: режим CTR, смена ключа через {_keyRotationInterval.TotalMinutes} мин");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка инициализации: {ex.Message}");
                throw;
            }
        }

        #region Криптография

        /// <summary>
        /// Генерация сессионного ключа (256 бит)
        /// </summary>
        private void GenerateSessionKey()
        {
            _sessionKey = new byte[32]; // 256 бит для Кузнечика
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_sessionKey);
            }

            // Для отладки (в production удалить)
            Console.WriteLine($"🔑 Сгенерирован ключ: {BitConverter.ToString(_sessionKey).Replace("-", "")}");
        }

        /// <summary>
        /// Генерация вектора инициализации (128 бит)
        /// </summary>
        private void GenerateSessionIV()
        {
            _sessionIV = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_sessionIV);
            }

            Console.WriteLine($"🔑 IV: {BitConverter.ToString(_sessionIV).Replace("-", "")}");
        }

        /// <summary>
        /// Ротация ключа (периодическая смена для безопасности)
        /// </summary>
        private async Task RotateEncryptionKey()
        {
            if (!_encryptionEnabled) return;

            lock (_cryptoLock)
            {
                // Генерируем новый ключ
                byte[] newKey = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(newKey);
                }

                byte[] newIV = new byte[16];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(newIV);
                }

                // Обновляем шифровальщик
                _crypto.UpdateKey(newKey, newIV);

                // Сохраняем новые ключи
                Array.Copy(newKey, _sessionKey, 32);
                Array.Copy(newIV, _sessionIV, 16);

                _lastKeyRotation = DateTime.Now;

                OnStatusChanged?.Invoke($"🔄 Ключ шифрования обновлен в {_lastKeyRotation:HH:mm:ss}");
                OnEncryptionKeyRotated?.Invoke();

                Console.WriteLine($"🔑 Ключ ротирован. Отправлено пакетов: {_packetsSent}, получено: {_packetsReceived}");
                Console.WriteLine($"📊 Зашифровано: {_bytesEncrypted / 1024} KB, дешифровано: {_bytesDecrypted / 1024} KB");
            }
        }

        /// <summary>
        /// Проверка необходимости ротации ключа
        /// </summary>
        private async Task CheckKeyRotation()
        {
            if (DateTime.Now - _lastKeyRotation >= _keyRotationInterval)
            {
                await RotateEncryptionKey();
            }
        }

        /// <summary>
        /// Получить публичную часть ключа для передачи собеседнику
        /// </summary>
        public byte[] GetPublicKeyMaterial()
        {
            // В реальном приложении здесь должна быть асимметричная шифровка
            // Сейчас возвращаем IV (безопасно, ключ передается отдельно)
            return _sessionIV;
        }

        /// <summary>
        /// Установить ключ от собеседника
        /// </summary>
        public void SetPeerKey(byte[] peerIV)
        {
            if (peerIV.Length == 16)
            {
                Array.Copy(peerIV, _sessionIV, 16);
                _crypto.InitSession(_sessionIV);
                OnStatusChanged?.Invoke("🔐 Ключ собеседника получен");
            }
        }

        /// <summary>
        /// Включить/отключить шифрование (для отладки)
        /// </summary>
        public void SetEncryptionEnabled(bool enabled)
        {
            _encryptionEnabled = enabled;
            OnStatusChanged?.Invoke(enabled ? "🔒 Шифрование включено" : "⚠️ Шифрование отключено");
        }

        #endregion

        #region Аудио обработка

        /// <summary>
        /// Обработка данных с микрофона и отправка
        /// </summary>
        private async void OnMicrophoneData(object sender, WaveInEventArgs e)
        {
            if (!_isActive || _udpClient == null || _remoteEndPoint == null)
                return;

            try
            {
                // Инкрементируем счетчик пакетов
                long currentPacket;

                // Анализ уровня громкости
                float maxLevel = 0;
                for (int i = 0; i < e.BytesRecorded && i < e.Buffer.Length; i += 2)
                {
                    if (i + 1 < e.Buffer.Length)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i);
                        float level = Math.Abs(sample) / 32768f;
                        if (level > maxLevel) maxLevel = level;
                    }
                }
                OnVolumeChanged?.Invoke(maxLevel);

                byte[] dataToSend;

                if (_encryptionEnabled)
                {
                    lock (_cryptoLock)
                    {
                        currentPacket = _packetCounter++;
                        dataToSend = _crypto.EncryptVoiceFast(e.Buffer, 0, e.BytesRecorded);
                        _bytesEncrypted += dataToSend.Length;
                    }

                    // ✅ Добавляем заголовок с номером пакета для синхронизации
                    byte[] packetWithHeader = new byte[dataToSend.Length + 10]; // 2 (флаги) + 8 (counter)
                    packetWithHeader[0] = 0x01; // Версия
                    packetWithHeader[1] = 0x01; // Флаг шифрования
                                                // Добавляем номер пакета (8 байт)
                    byte[] packetBytes = BitConverter.GetBytes(currentPacket);
                    Array.Copy(packetBytes, 0, packetWithHeader, 2, 8);
                    Array.Copy(dataToSend, 0, packetWithHeader, 10, dataToSend.Length);
                    dataToSend = packetWithHeader;
                }
                else
                {
                    byte[] packetWithHeader = new byte[e.BytesRecorded + 2];
                    packetWithHeader[0] = 0x01;
                    packetWithHeader[1] = 0x00;
                    Array.Copy(e.Buffer, 0, packetWithHeader, 2, e.BytesRecorded);
                    dataToSend = packetWithHeader;
                }

                _serviceClient?.RelayVoice(_userId, dataToSend);
                _packetsSent++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка отправки: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить голос от сервера (ретрансляция)
        /// </summary>
        public void ReceiveVoice(byte[] encryptedData)
        {
            if (_speaker == null || _waveProvider == null || !_isActive)
                return;

            try
            {
                if (encryptedData.Length < 10) // Минимум: 2 флага + 8 байт счетчика
                {
                    Console.WriteLine("⚠️ Пакет слишком короткий");
                    return;
                }

                byte version = encryptedData[0];
                byte isEncrypted = encryptedData[1];

                // Извлекаем номер пакета
                long packetNumber = BitConverter.ToInt64(encryptedData, 2);
                byte[] payload = new byte[encryptedData.Length - 10];
                Array.Copy(encryptedData, 10, payload, 0, payload.Length);

                // Проверяем синхронизацию
                lock (_cryptoLock)
                {
                    if (packetNumber != _packetCounter)
                    {
                        Console.WriteLine($"⚠️ Рассинхронизация! Ожидался пакет #{_packetCounter}, получен #{packetNumber}");

                        // Если рассинхронизация больше 10 пакетов - пересинхронизируем
                        if (Math.Abs(packetNumber - _packetCounter) > 10)
                        {
                            Console.WriteLine($"🔄 Принудительная пересинхронизация...");
                            SyncEncryptionCounter(packetNumber);
                        }
                    }
                    _packetCounter = packetNumber + 1;
                }

                byte[] audioData;

                if (isEncrypted == 0x01 && _encryptionEnabled)
                {
                    lock (_cryptoLock)
                    {
                        audioData = _crypto.DecryptVoiceFast(payload);
                        _bytesDecrypted += payload.Length;
                    }
                    _packetsReceived++;
                }
                else if (isEncrypted == 0x00)
                {
                    audioData = payload;
                }
                else
                {
                    return;
                }

                if (audioData != null && audioData.Length > 0)
                {
                    _waveProvider.AddSamples(audioData, 0, audioData.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка дешифрования: {ex.Message}");
            }
        }

        /// <summary>
        /// Тестовый звук для проверки динамиков
        /// </summary>
        private void PlayTestSound()
        {
            try
            {
                byte[] testSound = new byte[16000]; // 1 секунда
                for (int i = 0; i < 8000; i++)
                {
                    short sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 10000);
                    byte[] bytes = BitConverter.GetBytes(sample);
                    testSound[i * 2] = bytes[0];
                    testSound[i * 2 + 1] = bytes[1];
                }
                _waveProvider.AddSamples(testSound, 0, testSound.Length);
                OnStatusChanged?.Invoke("🔊 Тестовый сигнал воспроизведен");
            }
            catch { }
        }

        #endregion

        #region Управление звонком

        /// <summary>
        /// Исходящий звонок
        /// </summary>
        public async Task StartCall(string remoteIP, int port)
        {
            try
            {
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(remoteIP), port);
                _udpClient = new UdpClient();
                _udpClient.Connect(_remoteEndPoint);
                _udpClient.Client.SendBufferSize = 65536; // Увеличиваем буфер
                _udpClient.Client.ReceiveBufferSize = 65536;

                _isActive = true;

                // Запускаем прием
                StartReceiving();

                // Запускаем микрофон
                if (_useMicrophone)
                {
                    _microphone.StartRecording();
                }

                // Запускаем динамик
                _speaker.Play();

                // Тестовый звук
                PlayTestSound();

                OnStatusChanged?.Invoke($"✅ Звонок установлен с {remoteIP}:{port}");
                OnStatusChanged?.Invoke($"🔐 Шифрование: ГОСТ Кузнечик (CTR), {(_encryptionEnabled ? "включено" : "отключено")}");

                // Запускаем таймер ротации ключей
                _ = Task.Run(async () =>
                {
                    while (_isActive)
                    {
                        await Task.Delay(60000); // Проверяем каждую минуту
                        await CheckKeyRotation();
                    }
                });
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка звонка: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Входящий звонок (прием)
        /// </summary>
        public async Task AcceptCall(int listenPort)
        {
            try
            {
                _udpClient = new UdpClient(listenPort);
                _udpClient.Client.SendBufferSize = 65536;
                _udpClient.Client.ReceiveBufferSize = 65536;

                _isActive = true;

                // Ждем первого пакета от собеседника
                OnStatusChanged?.Invoke("📞 Ожидание голосовых данных...");
                var result = await _udpClient.ReceiveAsync();
                _remoteEndPoint = result.RemoteEndPoint;

                OnStatusChanged?.Invoke($"✅ Соединение установлено с {_remoteEndPoint.Address}:{_remoteEndPoint.Port}");

                // Запускаем прием
                StartReceiving();

                // Запускаем микрофон
                if (_useMicrophone)
                {
                    _microphone.StartRecording();
                }

                // Запускаем динамик
                _speaker.Play();

                OnStatusChanged?.Invoke($"🔐 Шифрование активно");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"❌ Ошибка приема звонка: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Прием голосовых пакетов
        /// </summary>
        private async void StartReceiving()
        {
            int packetCount = 0;
            DateTime lastLogTime = DateTime.Now;

            while (_isActive)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    packetCount++;

                    // Дешифруем полученные данные
                    byte[] decryptedData = DecryptVoiceData(result.Buffer);

                    if (decryptedData != null && decryptedData.Length > 0)
                    {
                        _waveProvider.AddSamples(decryptedData, 0, decryptedData.Length);
                    }

                    // Логируем каждые 100 пакетов
                    if (packetCount % 100 == 0)
                    {
                        var elapsed = DateTime.Now - lastLogTime;
                        Console.WriteLine($"📥 Принято {packetCount} пакетов за {elapsed.TotalSeconds:F1} сек, скорость: {packetCount / elapsed.TotalSeconds:F0} пакетов/сек");
                        lastLogTime = DateTime.Now;
                    }
                }
                catch (SocketException ex)
                {
                    if (_isActive)
                        Console.WriteLine($"❌ Ошибка сокета: {ex.Message}");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Ошибка приема: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Дешифрование полученных данных
        /// </summary>
        private byte[] DecryptVoiceData(byte[] encryptedData)
        {
            if (encryptedData.Length < 2)
                return null;

            try
            {
                byte version = encryptedData[0];
                byte isEncrypted = encryptedData[1];
                byte[] payload = new byte[encryptedData.Length - 2];
                Array.Copy(encryptedData, 2, payload, 0, payload.Length);

                if (isEncrypted == 0x01 && _encryptionEnabled)
                {
                    lock (_cryptoLock)
                    {
                        return _crypto.DecryptVoiceFast(payload);
                    }
                }
                else if (isEncrypted == 0x00)
                {
                    return payload;
                }
                else
                {
                    Console.WriteLine($"⚠️ Неизвестный тип пакета: {isEncrypted}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка дешифрования: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Mute микрофона
        /// </summary>
        public void SetMute(bool mute)
        {
            if (mute)
            {
                _microphone?.StopRecording();
                OnStatusChanged?.Invoke("🔇 Микрофон отключен");
            }
            else
            {
                if (_useMicrophone)
                    _microphone?.StartRecording();
                OnStatusChanged?.Invoke("🎤 Микрофон включен");
            }
        }

        /// <summary>
        /// Установить громкость динамика
        /// </summary>
        public void SetSpeakerVolume(float volume)
        {
            if (_speaker != null)
            {
                volume = Math.Max(0, Math.Min(1, volume));
                _speaker.Volume = volume;
                OnStatusChanged?.Invoke($"🔊 Громкость собеседника: {volume * 100:F0}%");
            }
        }

        /// <summary>
        /// Получить статистику звонка
        /// </summary>
        public string GetStatistics()
        {
            return $"📊 Статистика звонка:\n" +
                   $"   Отправлено пакетов: {_packetsSent}\n" +
                   $"   Получено пакетов: {_packetsReceived}\n" +
                   $"   Зашифровано: {_bytesEncrypted / 1024} KB\n" +
                   $"   Дешифровано: {_bytesDecrypted / 1024} KB\n" +
                   $"   Шифрование: {(_encryptionEnabled ? "Активно" : "Отключено")}\n" +
                   $"   Ключ действителен до: {_lastKeyRotation + _keyRotationInterval:HH:mm:ss}";
        }

        /// <summary>
        /// Завершить звонок
        /// </summary>
        public void HangUp()
        {
            _isActive = false;

            _microphone?.StopRecording();
            _speaker?.Stop();
            _udpClient?.Close();

            OnStatusChanged?.Invoke("📞 Звонок завершен");

            // Логируем финальную статистику
            Console.WriteLine(GetStatistics());
        }

        /// <summary>
        /// Установка ключа шифрования (вызывается до начала звонка)
        /// </summary>
        public void SetEncryptionKey(byte[] sessionKey, byte[] iv)
        {
            if (sessionKey == null || sessionKey.Length != 32)
                throw new ArgumentException("Ключ должен быть 32 байта (256 бит)");

            if (iv == null || iv.Length != 16)
                throw new ArgumentException("IV должен быть 16 байт (128 бит)");

            lock (_cryptoLock)
            {
                // Обновляем ключи
                Array.Copy(sessionKey, _sessionKey, 32);
                Array.Copy(iv, _sessionIV, 16);

                // Пересоздаем крипто-объект с новыми ключами
                _crypto?.Dispose();
                _crypto = new KuznechikStreaming(_sessionKey);
                _crypto.InitSession(_sessionIV);

                OnStatusChanged?.Invoke("🔐 Ключи шифрования установлены");
            }
        }

        /// <summary>
        /// Получить текущий ключ шифрования (для передачи собеседнику)
        /// </summary>
        public (byte[] Key, byte[] IV) GetEncryptionKey()
        {
            byte[] keyCopy = new byte[32];
            byte[] ivCopy = new byte[16];

            lock (_cryptoLock)
            {
                Array.Copy(_sessionKey, keyCopy, 32);
                Array.Copy(_sessionIV, ivCopy, 16);
            }

            return (keyCopy, ivCopy);
        }

        /// <summary>
        /// Синхронизация счетчика шифрования (вызывается при получении ключей)
        /// </summary>
        public void SyncEncryptionCounter(long packetNumber)
        {
            lock (_cryptoLock)
            {
                // Устанавливаем счетчик в переданное значение
                _packetCounter = packetNumber;

                // Пересоздаем гамму с новым счетчиком
                _crypto?.Dispose();
                _crypto = new KuznechikStreaming(_sessionKey);

                // Устанавливаем IV + счетчик пакетов
                byte[] counterWithPacket = new byte[16];
                Array.Copy(_sessionIV, counterWithPacket, 16);

                // Добавляем номер пакета в счетчик (первые 8 байт)
                byte[] packetBytes = BitConverter.GetBytes(packetNumber);
                for (int i = 0; i < 8; i++)
                {
                    counterWithPacket[i] ^= packetBytes[i % 8];
                }

                _crypto.InitSession(counterWithPacket);

                Console.WriteLine($"🔄 Синхронизация шифрования: packet #{packetNumber}");
            }
        }

        /// <summary>
        /// Получить текущий номер пакета для синхронизации
        /// </summary>
        public long GetCurrentPacketNumber()
        {
            lock (_cryptoLock)
            {
                return _packetCounter;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            HangUp();

            _microphone?.Dispose();
            _speaker?.Dispose();
            _udpClient?.Dispose();
            _crypto?.Dispose();

            // Очищаем ключи из памяти
            if (_sessionKey != null)
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
            if (_sessionIV != null)
                Array.Clear(_sessionIV, 0, _sessionIV.Length);
        }

        #endregion
    }
}
