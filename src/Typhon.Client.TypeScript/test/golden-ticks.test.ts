import { describe, expect, it } from 'vitest';
import { BlockMask, CatalogPlan, parseCatalog, TickReader } from '../src/index.js';
import { goldenBin, goldenJson, goldenNames, RecordingSink, type LogEntry } from './golden-support.js';

/* tick-*: the decoder's call log equals the C# RecordingSink's, call for call. */

describe('golden ticks', () => {
  const names = goldenNames('tick-');

  it('finds the tick vectors', () => {
    expect(names).toEqual(expect.arrayContaining(['tick-blocks', 'tick-entities', 'tick-keepalive']));
  });

  for (const name of names) {
    it(name, () => {
      const vector = goldenJson(name) as { catalog: string; log: LogEntry[] };
      const reader = new TickReader(CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog))));
      const sink = new RecordingSink();
      reader.read(goldenBin(name), sink);
      expect(sink.log).toEqual(vector.log);
    });
  }

  it('skips unselected blocks by their length and still frames the tick', () => {
    const vector = goldenJson('tick-blocks') as { catalog: string; log: LogEntry[] };
    const reader = new TickReader(CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog))));
    const sink = new RecordingSink();
    reader.read(goldenBin('tick-blocks'), sink, BlockMask.Acks);
    expect(sink.log.map((e) => e.call)).toEqual(['beginTick', 'ack', 'ack', 'endTick']);
  });
});
