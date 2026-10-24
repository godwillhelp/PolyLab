using NAudio.Wave;
using NVorbis;

namespace Pfx.App.Preview;

// Plays the project's .ogg sounds (spec 3.8: "sound preview"): the panel's play button and the sounds of the states
// while the preview plays them. NVorbis decodes, NAudio's wave-out plays on its own thread; one output per playing
// sound. Distance attenuation of the game's sound sets is not modelled: a sound plays at its volume.
public sealed class SoundPreview : IDisposable
{
    private readonly Dictionary<string, (WaveOutEvent Out, VorbisReader Reader)> _playing = new();

    public bool IsPlaying(string key) => _playing.ContainsKey(key);

    public void Play(string key, string file, float volume, bool loop)
    {
        Stop(key);
        var reader = new VorbisReader(file);
        var output = new WaveOutEvent();
        output.Init(new VorbisSamples(reader, loop));
        output.Volume = Math.Clamp(volume, 0, 1);
        output.PlaybackStopped += (_, _) =>
        {
            if (_playing.TryGetValue(key, out var p) && p.Out == output)
                Stop(key);
        };
        _playing[key] = (output, reader);
        output.Play();
    }

    public void Stop(string key)
    {
        if (!_playing.Remove(key, out var p))
            return;
        p.Out.Dispose();
        p.Reader.Dispose();
    }

    public void StopAll()
    {
        foreach (var key in _playing.Keys.ToList())
            Stop(key);
    }

    public void Dispose() => StopAll();

    private sealed class VorbisSamples : ISampleProvider
    {
        private readonly VorbisReader _reader;
        private readonly bool _loop;

        public VorbisSamples(VorbisReader reader, bool loop)
        {
            _reader = reader;
            _loop = loop;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _reader.ReadSamples(buffer, offset, count);
            if (read < count && _loop && _reader.TotalSamples > 0)
            {
                _reader.SamplePosition = 0;
                read += _reader.ReadSamples(buffer, offset + read, count - read);
            }
            return read;
        }
    }
}
