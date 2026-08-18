using NAudio.Wave;
using System;

namespace ChatClient
{
    public sealed class CallTonePlayer : IDisposable
    {
        private readonly object _sync = new object();
        private WaveOutEvent _output;

        public void PlayOutgoing()
        {
            Start(new[] { 425.0 }, new[] { 1.0, 3.0 }, 0.16);
        }

        public void PlayIncoming()
        {
            Start(new[] { 440.0, 480.0 }, new[] { 0.4, 0.2, 0.4, 2.0 }, 0.2);
        }

        public void Stop()
        {
            lock (_sync)
                StopUnlocked();
        }

        public void Dispose() => Stop();

        private void Start(double[] frequencies, double[] cadenceSeconds, double gain)
        {
            lock (_sync)
            {
                StopUnlocked();
                _output = new WaveOutEvent { DesiredLatency = 80 };
                _output.Init(new CadenceToneProvider(frequencies, cadenceSeconds, gain));
                _output.Play();
            }
        }

        private void StopUnlocked()
        {
            if (_output == null)
                return;

            try
            {
                _output.Stop();
            }
            catch
            {
            }

            _output.Dispose();
            _output = null;
        }
    }

    internal sealed class CadenceToneProvider : ISampleProvider
    {
        private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        private readonly double[] _frequencies;
        private readonly double[] _cadence;
        private readonly double[] _phase;
        private readonly double _gain;
        private int _cadenceIndex;
        private int _cadenceSamplesLeft;

        public CadenceToneProvider(double[] frequencies, double[] cadenceSeconds, double gain)
        {
            _frequencies = frequencies;
            _cadence = cadenceSeconds;
            _gain = gain;
            _phase = new double[frequencies.Length];
            StartSegment(0);
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int sampleRate = _format.SampleRate;
            for (int n = 0; n < count; n++)
            {
                if (_cadenceSamplesLeft <= 0)
                {
                    _cadenceIndex = (_cadenceIndex + 1) % _cadence.Length;
                    StartSegment(_cadenceIndex);
                }

                float sample = 0f;
                if ((_cadenceIndex % 2) == 0)
                {
                    for (int i = 0; i < _frequencies.Length; i++)
                    {
                        sample += (float)(Math.Sin(_phase[i]) * _gain / _frequencies.Length);
                        _phase[i] += 2.0 * Math.PI * _frequencies[i] / sampleRate;
                        if (_phase[i] > 2.0 * Math.PI)
                            _phase[i] -= 2.0 * Math.PI;
                    }
                }

                buffer[offset + n] = sample;
                _cadenceSamplesLeft--;
            }

            return count;
        }

        private void StartSegment(int index)
        {
            _cadenceSamplesLeft = Math.Max(1, (int)(_cadence[index] * _format.SampleRate));
        }
    }
}
