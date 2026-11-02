using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #1394, design 07 §12 (d): the cost of the WAL slot-translation hook on an open that migrates no archetype set.
//
// RecoveryDriver builds one Rec per WAL record — a class instance, with the Slot value's bytes copied out of the reader's span — and, since #1394, reads
// DatabaseEngine.TranslatesWalSlots once per scan and calls TranslateRecord per record only when it is set. This is that loop over synthetic Slot and
// Lifecycle records, with the hook (production; the flag is false because nothing migrates) against the same loop without it. Gate: within 1 % or within
// the error.
//
// The Rec and the payload copy are part of the loop on purpose: a loop that only copies a few fields into a preallocated array makes the never-taken branch
// look like 15-30 % (measured while writing this), which says nothing about the replay the gate is about.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[InProcess(true)]
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[BenchmarkCategory("Durability", "Recovery")]
public class WalSlotTranslateBenchmarks : IDisposable
{
    [Params(1_000_000)]
    public int Records;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private View[] _views;
    private byte[] _payload;

    /// <summary>The fields of a WAL record view the Rec build reads.</summary>
    private struct View
    {
        public long Lsn;
        public long Tsn;
        public byte Kind;
        public byte Op;
        public long EntityId;
        public ushort ArchetypeId;
        public ushort SlotIndex;
        public ushort EnabledBits;
    }

    /// <summary>RecoveryDriver's Rec, field for field.</summary>
    private sealed class Rec
    {
        public long Lsn;
        public long Tsn;
        public RecordKind Kind;
        public byte Op;
        public long EntityId;
        public ushort ArchetypeId;
        public ushort SlotIndex;
        public ushort FieldId;
        public ushort EnabledBits;
        public int Index;
        public bool IsFence;
        public bool IsCommitDiscipline;
        public byte[] Payload;
    }

    [GlobalSetup]
    public void Setup()
    {
        _sp = BenchmarkEngine.BuildEcsEngine(16 * 1024, "WalSlotTranslate");
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.InitializeArchetypes();
        if (_dbe.TranslatesWalSlots)
        {
            throw new InvalidOperationException("premise: no archetype set migrates, so the hook is the one flag check");
        }

        var rnd = new Random(1394);
        _payload = new byte[16];
        _views = new View[Records];
        for (var i = 0; i < Records; i++)
        {
            var lifecycle = i % 16 == 0;
            _views[i] = new View
            {
                Lsn = i + 1,
                Tsn = i / 8 + 1,
                Kind = (byte)(lifecycle ? RecordKind.Lifecycle : RecordKind.Slot),
                Op = lifecycle ? (byte)LifecycleOp.SetEnabledBits : (byte)SlotOp.Upsert,
                EntityId = ((long)rnd.Next(1, 1 << 30) << 16) | 7,
                ArchetypeId = 7,
                SlotIndex = (ushort)rnd.Next(0, 12),
                EnabledBits = (ushort)rnd.Next(0, 1 << 12),
            };
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sp?.Dispose();
        _sp = null;
    }

    /// <summary>The Rec build as it was before #1394: no translation hook.</summary>
    [Benchmark(Baseline = true)]
    public int WithoutHook()
    {
        var views = _views;
        var payload = _payload.AsSpan();
        var records = new List<Rec>(views.Length);
        for (var i = 0; i < views.Length; i++)
        {
            ref readonly var view = ref views[i];
            records.Add(new Rec
            {
                Lsn = view.Lsn, Tsn = view.Tsn, Kind = (RecordKind)view.Kind, Op = view.Op, EntityId = view.EntityId, ArchetypeId = view.ArchetypeId,
                SlotIndex = view.SlotIndex, FieldId = 0, EnabledBits = view.EnabledBits, Index = 0, IsFence = false, IsCommitDiscipline = false,
                Payload = view.Kind == (byte)RecordKind.Slot ? payload.ToArray() : null,
            });
        }

        return records.Count;
    }

    /// <summary>The Rec build with the hook, on an open that migrates no set: the flag read once, a never-taken branch per record.</summary>
    [Benchmark]
    public int WithHook()
    {
        var views = _views;
        var payload = _payload.AsSpan();
        var records = new List<Rec>(views.Length);
        var translate = _dbe.TranslatesWalSlots;
        for (var i = 0; i < views.Length; i++)
        {
            ref readonly var view = ref views[i];
            var slotIndex = view.SlotIndex;
            var enabledBits = view.EnabledBits;
            if (translate)
            {
                RecoveryDriver.TranslateRecord(_dbe, (RecordKind)view.Kind, view.Op, view.EntityId, payload.Length, ref slotIndex, ref enabledBits);
            }

            records.Add(new Rec
            {
                Lsn = view.Lsn, Tsn = view.Tsn, Kind = (RecordKind)view.Kind, Op = view.Op, EntityId = view.EntityId, ArchetypeId = view.ArchetypeId,
                SlotIndex = slotIndex, FieldId = 0, EnabledBits = enabledBits, Index = 0, IsFence = false, IsCommitDiscipline = false,
                Payload = view.Kind == (byte)RecordKind.Slot ? payload.ToArray() : null,
            });
        }

        return records.Count;
    }
}
