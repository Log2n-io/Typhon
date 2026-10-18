import { describe, expect, it } from 'vitest';
import {
  AckList,
  CatalogPlan,
  CommandQueue,
  GroupSubscriptions,
  parseCatalog,
  SourceList,
  type SubscribeResult,
} from '../src/index.js';
import { goldenBin, goldenJson } from './golden-support.js';

/*
 * subscribe-requests: the .NET core's script of requests, pumps, frames and a session's end, replayed through this
 * core over a real CommandQueue — whose token bucket is the command's catalog rate — at the times the steps name.
 */

interface SentJson {
  readonly seq: number;
  readonly requestId: number;
  readonly set: number;
  readonly key: string;
  readonly op: number;
}

interface AnswerJson {
  readonly requestId: number;
  readonly set: number;
  readonly key: string;
  readonly op: number;
  readonly outcome: string;
  readonly code: number;
  readonly ackReason: number;
}

interface StepJson {
  readonly do: 'request' | 'pump' | 'observe' | 'reset';
  readonly set?: number;
  readonly key?: string;
  readonly op?: number;
  readonly requestId?: number;
  readonly throws?: boolean;
  readonly atMs?: number;
  readonly sent?: readonly SentJson[];
  readonly acks?: readonly { readonly seq: number; readonly reason: number }[];
  readonly sources?: readonly { readonly requestId: number; readonly status: number; readonly code: number }[];
  readonly answers?: readonly AnswerJson[];
}

function answerJson(r: SubscribeResult): AnswerJson {
  return {
    requestId: r.requestId,
    set: r.set,
    key: r.key.toString(),
    op: r.op,
    outcome: r.outcome,
    code: r.code,
    ackReason: r.ackReason,
  };
}

describe('subscribe-requests', () => {
  it('replays the script to the committed outputs', () => {
    const plan = CatalogPlan.compile(parseCatalog(goldenBin('subscribe-requests')));
    const { steps } = goldenJson('subscribe-requests') as { steps: readonly StepJson[] };
    let nowMs = 0;
    const queue = new CommandQueue({ plan, now: () => nowMs, firstSeq: 1 });
    const answers: AnswerJson[] = [];
    const core = new GroupSubscriptions(plan, { onAnswer: (r) => answers.push(answerJson(r)) });

    steps.forEach((step, i) => {
      const where = `step ${i} (${step.do})`;
      switch (step.do) {
        case 'request':
          if (step.throws === true) {
            expect(() => core.request(step.set!, BigInt(step.key!), step.op!), where).toThrow();
          } else {
            expect(core.request(step.set!, BigInt(step.key!), step.op!), where).toBe(step.requestId);
          }

          break;
        case 'pump': {
          nowMs = step.atMs!;
          const sent: SentJson[] = [];
          core.pump((command, values) => {
            const seq = queue.enqueue(command, values);
            if (seq < 0) {
              return -1;
            }

            sent.push({
              seq,
              requestId: values.requestId as number,
              set: values.set as number,
              key: (values.key as bigint).toString(),
              op: values.op as number,
            });
            return seq;
          });
          queue.clear();
          expect(sent, where).toEqual(step.sent);
          break;
        }
        case 'observe': {
          const acks = new AckList();
          const sources = new SourceList();
          for (const a of step.acks!) {
            acks.push(a.seq, a.reason);
          }

          for (const s of step.sources!) {
            sources.push(s.requestId, s.status, s.code);
          }

          core.observe({ acks, sources });
          expect(answers.splice(0), where).toEqual(step.answers);
          break;
        }
        case 'reset':
          core.reset();
          expect(answers.splice(0), where).toEqual(step.answers);
          break;
      }
    });
  });

  it('settles a subscribe promise with its answer, and a dropped one on reset', async () => {
    const plan = CatalogPlan.compile(parseCatalog(goldenBin('subscribe-requests')));
    const queue = new CommandQueue({ plan, now: () => 0, firstSeq: 1 });
    const core = new GroupSubscriptions(plan);
    const applied = core.subscribe('lists', 7);
    const dropped = core.unsubscribe('lists', 8n);
    expect(core.pump(queue)).toBe(2);
    const sources = new SourceList();
    sources.push(1, 0, 0);
    core.observe({ acks: new AckList(), sources });
    core.reset();
    await expect(applied).resolves.toMatchObject({ requestId: 1, outcome: 'applied', key: 7n });
    await expect(dropped).resolves.toMatchObject({ requestId: 2, outcome: 'dropped', op: 2 });
    await expect(core.subscribe('towns', 1)).rejects.toThrow('not an explicit set');
    await expect(core.subscribe('nowhere', 1)).rejects.toThrow('no group set');
  });
});
