namespace HomePodCast.Audio;

/// <summary>采集线程写、发送线程读的 PCM 环形缓冲（16 位立体声，按帧计数）。</summary>
sealed class AudioRing(int capacityFrames)
{
    readonly short[] _buf = new short[capacityFrames * 2];
    readonly object _lock = new();
    int _read, _count; // 单位：帧

    public int Available { get { lock (_lock) return _count; } }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            int frames = samples.Length / 2;
            int cap = _buf.Length / 2;
            // 写满时丢弃最旧的数据，保证延迟不会无限增长
            if (frames > cap) { samples = samples[^(cap * 2)..]; frames = cap; }
            int overflow = _count + frames - cap;
            if (overflow > 0) { _read = (_read + overflow) % cap; _count -= overflow; }

            int write = (_read + _count) % cap;
            int first = Math.Min(frames, cap - write);
            samples[..(first * 2)].CopyTo(_buf.AsSpan(write * 2));
            samples[(first * 2)..].CopyTo(_buf.AsSpan(0));
            _count += frames;
        }
    }

    public void WriteSilence(int frames)
    {
        Span<short> zero = stackalloc short[Math.Min(frames, 2048) * 2];
        zero.Clear();
        while (frames > 0)
        {
            int n = Math.Min(frames, zero.Length / 2);
            Write(zero[..(n * 2)]);
            frames -= n;
        }
    }

    public bool Read(Span<short> dest)
    {
        lock (_lock)
        {
            int frames = dest.Length / 2;
            if (_count < frames) return false;
            int cap = _buf.Length / 2;
            int first = Math.Min(frames, cap - _read);
            _buf.AsSpan(_read * 2, first * 2).CopyTo(dest);
            _buf.AsSpan(0, (frames - first) * 2).CopyTo(dest[(first * 2)..]);
            _read = (_read + frames) % cap;
            _count -= frames;
            return true;
        }
    }

    /// <summary>丢弃多余的帧，只保留最新的 keepFrames 帧。</summary>
    public void Trim(int keepFrames)
    {
        lock (_lock)
        {
            if (_count <= keepFrames) return;
            int drop = _count - keepFrames;
            _read = (_read + drop) % (_buf.Length / 2);
            _count = keepFrames;
        }
    }

    public void Clear() { lock (_lock) { _read = 0; _count = 0; } }
}
