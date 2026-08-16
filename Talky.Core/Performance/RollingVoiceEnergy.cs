using System;

namespace Talky.Performance;

/// <summary>
/// Calculates rolling RMS over a fixed, zero-initialized PCM window.
/// </summary>
public sealed class RollingVoiceEnergy
{
    public const int DefaultCapacity = 4800;

    private readonly float[] _squaredSamples;
    private int _writeIndex;
    private double _sumSquares;

    public RollingVoiceEnergy(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _squaredSamples = new float[capacity];
    }

    public int Capacity => _squaredSamples.Length;
    public double SumSquares => _sumSquares > 0d ? _sumSquares : 0d;
    public double RootMeanSquare => Math.Sqrt(SumSquares / _squaredSamples.Length);
    public double Decibels
    {
        get
        {
            double rms = RootMeanSquare;
            return rms > 0d ? 20d * Math.Log10(rms) : double.NegativeInfinity;
        }
    }

    public void Write(float[] samples, int count)
    {
        Write(samples, 0, count);
    }

    public void Write(float[] samples, int offset, int count)
    {
        if (samples == null)
            throw new ArgumentNullException(nameof(samples));
        if (offset < 0 || count < 0 || offset > samples.Length - count)
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(count));

        int capacity = _squaredSamples.Length;
        int writeIndex = _writeIndex;
        double sum = _sumSquares;

        for (int i = 0; i < count; i++)
        {
            float sample = samples[offset + i];
            float square = sample * sample;
            sum += square - _squaredSamples[writeIndex];
            _squaredSamples[writeIndex] = square;

            writeIndex++;
            if (writeIndex == capacity)
                writeIndex = 0;
        }

        _writeIndex = writeIndex;
        _sumSquares = sum > 0d ? sum : 0d;
    }

    public void Reset()
    {
        Array.Clear(_squaredSamples, 0, _squaredSamples.Length);
        _writeIndex = 0;
        _sumSquares = 0d;
    }
}
