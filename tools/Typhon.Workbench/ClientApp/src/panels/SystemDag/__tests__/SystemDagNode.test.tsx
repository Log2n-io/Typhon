// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ReactFlowProvider } from '@xyflow/react';
import SystemDagNode from '../SystemDagNode';
import type { DagNodeData } from '../dagModel';
import { categoricalColor } from '@/libs/color/categorical';
import { rgbCss } from '@/libs/color/contrast';

// Phase 5 / AC-D — the DAG node carries the system's shared DS-2 identity hue as a non-destructive left stripe
// (the same hue the timeline lane / Access-Matrix header / Query Analyzer show for that system).

function makeData(systemName: string): DagNodeData {
  return {
    systemName,
    kind: 'Pipeline',
    phaseName: 'Update',
    isParallel: false,
    isExclusivePhase: false,
    tierFilter: 0x0f,
    dagId: 0,
    reads: [],
    readsFresh: [],
    readsSnapshot: [],
    writes: [],
    sideWrites: [],
    readsEvents: [],
    writesEvents: [],
    readsResources: [],
    writesResources: [],
    changeFilterTypes: [],
    hasAccess: true,
  };
}

function renderNode(data: DagNodeData) {
  return render(
    <ReactFlowProvider>
      <SystemDagNode data={data} selected={false} />
    </ReactFlowProvider>,
  );
}

afterEach(() => cleanup());

describe('SystemDagNode — DS-2 system identity accent', () => {
  it('renders a left stripe coloured by the system shared categorical hue', () => {
    renderNode(makeData('Movement'));
    const stripe = screen.getByTestId('system-dag-accent-Movement');
    expect(stripe.style.backgroundColor).toBe(rgbCss(categoricalColor('Movement')));
  });

  it('distinct systems get distinct accents; the same name is stable', () => {
    renderNode(makeData('Movement'));
    const a = screen.getByTestId('system-dag-accent-Movement').style.backgroundColor;
    cleanup();
    renderNode(makeData('Render'));
    const b = screen.getByTestId('system-dag-accent-Render').style.backgroundColor;
    expect(a).toBe(rgbCss(categoricalColor('Movement')));
    expect(a).not.toBe(b);
  });

  it('is additive — the accent coexists with the node body and kind badge (heat border untouched)', () => {
    renderNode(makeData('Movement'));
    expect(screen.getByTestId('system-dag-accent-Movement')).toBeTruthy();
    expect(screen.getByTestId('system-dag-node-Movement')).toBeTruthy();
    expect(screen.getByText('Pipeline')).toBeTruthy(); // the kind badge still renders
  });
});
