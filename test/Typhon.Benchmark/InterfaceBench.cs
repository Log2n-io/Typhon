// unset

using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Benchmark;

public interface IAddition
{
    int A { get; set; }
    int B { get; set; }
    int R { get; set; }

    void Add();
}

public struct Accessor
{
    public void Add(ref TestInterface o)
    {
        o.R = o.A + o.B;
    }
}

public struct TestInterface : IAddition
{
    public Accessor Accessor;
    private int _a;
    private int _b;
    private int _r;

    public int A
    {
        get => _a;
        set => _a = value;
    }

    public int B
    {
        get => _b;
        set => _b = value;
    }

    public int R
    {
        get => _r;
        set => _r = value;
    }

    public void Add()
    {
        R = A + B;
    }

    public void AddBacking()
    {
        _r = _a + _b;
    }
}

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[BenchmarkCategory("Memory")]
public unsafe class InterfaceBench
{
    private const int ElementCount = 1024 * 1024;

    private byte[] _buffer;
    private byte* _address;
    private int _elementSize;


    public ref TestInterface GetElement(int index) => ref Unsafe.AsRef<TestInterface>(_address + index * _elementSize);

    [GlobalSetup]
    public void GlobalSetup()
    {
        _buffer = GC.AllocateArray<byte>(sizeof(TestInterface) * ElementCount);
        fixed (byte* addr = _buffer) { _address = addr; }
        _elementSize = sizeof(TestInterface);

        var r = new Random(DateTime.UtcNow.Millisecond);
        for (int i = 0; i < ElementCount; i++)
        {
            ref var e = ref GetElement(i);
            e.A = r.Next();
            e.B = r.Next();
        }
    }


    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _buffer = null;
    }

    [Benchmark(Baseline = true)]
    public int BenchmarkDirectAdd()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 100; j++)
            {
                t0.Add();
                t1.Add();
                t2.Add();
                t3.Add();
            }
        }

        return default;
    }


    [Benchmark]
    public int BenchmarkDirectAddBacking()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 100; j++)
            {
                t0.AddBacking();
                t1.AddBacking();
                t2.AddBacking();
                t3.AddBacking();
            }
        }

        return default;
    }

    [Benchmark]
    public int BenchmarkInterfaceCall()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            var t0 = (IAddition)GetElement(i + 0);
            var t1 = (IAddition)GetElement(i + 1);
            var t2 = (IAddition)GetElement(i + 2);
            var t3 = (IAddition)GetElement(i + 3);

            for (int j = 0; j < 100; j++)
            {
                t0.Add();
                t1.Add();
                t2.Add();
                t3.Add();
            }
        }

        return default;
    }

    public static void Add<T>(T o) where T : struct, IAddition
    {
        o.R = o.A + o.B;
    }

    [Benchmark]
    public int BenchmarkGenericMethod()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 100; j++)
            {
                Add(t0);
                Add(t1);
                Add(t2);
                Add(t3);
            }
        }

        return default;
    }

    [Benchmark]
    public int BenchmarkAccessor()
    {
        for (int i = 0; i < ElementCount; i += 4)
        {
            ref var t0 = ref GetElement(i + 0);
            ref var t1 = ref GetElement(i + 1);
            ref var t2 = ref GetElement(i + 2);
            ref var t3 = ref GetElement(i + 3);

            for (int j = 0; j < 100; j++)
            {
                t0.Accessor.Add(ref t0);
                t1.Accessor.Add(ref t1);
                t2.Accessor.Add(ref t2);
                t3.Accessor.Add(ref t3);
            }
        }

        return default;
    }
}