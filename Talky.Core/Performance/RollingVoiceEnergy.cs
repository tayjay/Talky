using System;

namespace Talky.Performance;

/// <summary>
/// Calculates rolling RMS over a fixed, zero-initialized PCM window.
/// </summary>
public sealed class RollingVoiceEnergy
{
    public const int DefaultCapacity = 4800;
    private const int FastBlockSize = 480;

    private readonly float[] _squaredSamples;
    private readonly double[] _blockSums;
    private readonly int[] _blockGenerations;
    private int _writeIndex;
    private int _validSampleCount;
    private int _generation = 1;
    private double _sumSquares;
    private double _sumCompensation;

    public RollingVoiceEnergy(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _squaredSamples = new float[capacity];
        if (capacity % FastBlockSize == 0)
        {
            _blockSums = new double[capacity / FastBlockSize];
            _blockGenerations = new int[_blockSums.Length];
        }
    }

    public int Capacity => _squaredSamples.Length;
    public int ValidSampleCount => _validSampleCount;
    public bool IsWindowFull => _validSampleCount == _squaredSamples.Length;
    public double SumSquares => _sumSquares > 0d ? _sumSquares : 0d;
    public double MeanSquare => SumSquares / _squaredSamples.Length;
    public double RootMeanSquare => Math.Sqrt(MeanSquare);
    public double PresentationMeanSquare =>
        _validSampleCount > 0 ? SumSquares / _validSampleCount : 0d;
    public double PresentationRootMeanSquare => Math.Sqrt(PresentationMeanSquare);
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

        if (count == FastBlockSize &&
            _blockSums != null &&
            _writeIndex % FastBlockSize == 0)
        {
            WriteAlignedBlock(samples, offset);
            return;
        }

        int capacity = _squaredSamples.Length;
        int writeIndex = _writeIndex;
        int validSampleCount = _validSampleCount;
        double sum = _sumSquares;
        double compensation = _sumCompensation;

        for (int i = 0; i < count; i++)
        {
            float sample = samples[offset + i];
            float square = sample * sample;
            bool replacingValidSample = validSampleCount == capacity;
            float oldSquare = replacingValidSample
                ? _squaredSamples[writeIndex]
                : 0f;
            AddCompensated(
                ref sum,
                ref compensation,
                (double)square - oldSquare);
            _squaredSamples[writeIndex] = square;

            if (_blockSums != null)
            {
                int blockIndex = writeIndex / FastBlockSize;
                double blockSum = _blockGenerations[blockIndex] == _generation
                    ? _blockSums[blockIndex]
                    : 0d;
                _blockSums[blockIndex] = blockSum + square - oldSquare;
                _blockGenerations[blockIndex] = _generation;
            }

            if (!replacingValidSample)
                validSampleCount++;

            writeIndex++;
            if (writeIndex == capacity)
                writeIndex = 0;
        }

        _writeIndex = writeIndex;
        _validSampleCount = validSampleCount;
        _sumSquares = sum > 0d ? sum : 0d;
        _sumCompensation = _sumSquares > 0d ? compensation : 0d;
    }

    public void Reset()
    {
        _writeIndex = 0;
        _validSampleCount = 0;
        _sumSquares = 0d;
        _sumCompensation = 0d;

        if (_blockGenerations == null)
            return;

        if (_generation == int.MaxValue)
        {
            Array.Clear(_blockGenerations, 0, _blockGenerations.Length);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }

    private void WriteAlignedBlock(float[] samples, int offset)
    {
        int blockIndex = _writeIndex / FastBlockSize;
        bool replacingValidBlock = _validSampleCount == _squaredSamples.Length;
        double oldBlockSum = replacingValidBlock &&
                             _blockGenerations[blockIndex] == _generation
            ? _blockSums[blockIndex]
            : 0d;
        double newBlockSum = 0d;
        int writeIndex = _writeIndex;
        for (int i = 0; i < FastBlockSize; i++)
        {
            float sample = samples[offset + i];
            float square = sample * sample;
            _squaredSamples[writeIndex + i] = square;
            newBlockSum += square;
        }

        _blockSums[blockIndex] = newBlockSum;
        _blockGenerations[blockIndex] = _generation;
        double sum = _sumSquares;
        double compensation = _sumCompensation;
        AddCompensated(
            ref sum,
            ref compensation,
            newBlockSum - oldBlockSum);
        _sumSquares = sum > 0d ? sum : 0d;
        _sumCompensation = _sumSquares > 0d ? compensation : 0d;

        _writeIndex += FastBlockSize;
        if (_writeIndex == _squaredSamples.Length)
            _writeIndex = 0;

        if (!replacingValidBlock)
        {
            _validSampleCount = Math.Min(
                _squaredSamples.Length,
                _validSampleCount + FastBlockSize);
        }
    }

    private static void AddCompensated(
        ref double sum,
        ref double compensation,
        double value)
    {
        double adjusted = value - compensation;
        double next = sum + adjusted;
        compensation = next - sum - adjusted;
        sum = next;
    }
}
