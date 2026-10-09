namespace Typhon.Engine;

/// <summary>
/// The prepare step of a lambda-declared chunked system (<see cref="Dag.ChunkedSystem{TChunk}"/>): sizes <paramref name="plan"/> with
/// <see cref="ChunkTable{T}.Reset"/>, fills one record per chunk, and returns the chunk count — 0 skips the system this tick, -1 keeps one chunk.
/// Runs on one thread, before any chunk is claimed.
/// </summary>
public delegate int ChunkPrepare<TChunk>(ChunkTable<TChunk> plan) where TChunk : unmanaged;

/// <summary>One chunk of a chunked system, handed the record its dispatch's prepare wrote for it — by <c>ref</c>, so no member call on it copies it.</summary>
public delegate void ChunkExecute<TChunk>(TickContext tick, ref TChunk chunk) where TChunk : unmanaged;
