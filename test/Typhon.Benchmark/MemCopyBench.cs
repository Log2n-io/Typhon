// unset

using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Benchmark;

[SimpleJob(warmupCount: 3, iterationCount: 3)]
[BenchmarkCategory("Memory")]
public unsafe class MemCopyBench
{
    private const int BufferSize = 1024 * 1024 * 128;
    private const int PageSize = 8 * 1024;
    private const int PageCount = BufferSize / PageSize;

    private byte[] _srcBuffer;
    private byte* _srcBufferAddr;
    private byte[] _dstBuffer;
    private byte* _dstBufferAddr;

    private List<int> _copyOrder;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _srcBuffer = GC.AllocateUninitializedArray<byte>(BufferSize, true);
        _srcBufferAddr = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_srcBuffer, 0);
            
        _dstBuffer = GC.AllocateUninitializedArray<byte>(BufferSize, true);
        _dstBufferAddr = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_srcBuffer, 0);

        var r = new Random(DateTime.UtcNow.Millisecond);
        var span = new Span<int>(_srcBufferAddr, BufferSize / 4);
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = r.Next();
        }

        _copyOrder = new List<int>();
        for (int i = 0; i < PageCount; i++)
        {
            _copyOrder.Add(r.Next(0, PageCount));
        }
    }


    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _srcBuffer = null;
        _dstBuffer = null;
    }

    [Benchmark(Baseline = true)]
    public void SpanCopy()
    {


        for (int i = 0; i < PageCount; i++)
        {
            var index = _copyOrder[i];

            var src = new Span<byte>(_srcBufferAddr + (index * PageSize), BufferSize);
            var dst = new Span<byte>(_dstBufferAddr + (index * PageSize), BufferSize);

            src.CopyTo(dst);

        }

    }
}