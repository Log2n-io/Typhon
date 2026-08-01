import React, { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview';
import { useArchetypeList } from '@/hooks/schema/useArchetypeList';
import {
  MAX_RESOLVED_COHORT,
  useCohortSurvival,
  useEntityCohort,
  useFullCohortIds,
  useLifecycleSeries,
} from '@/hooks/profiles/useEntityCohort';
import {
  canJoinCohort,
  explainBlocker,
  summariseSurvival,
  type CohortJoinBlocker,
} from '@/libs/profiles/entityCohort';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useSessionCapability } from '@/stores/useSessionStore';
import { openDataBrowser } from '@/shell/commands/openSchemaBrowser';

/**
 * Spawn/destroy cohorts — the entity lens (#620, design §4.4).
 *
 * *"Spawn storm at tick 4,102 → 1,240 entities spawned here — 830 still alive → open those 830 in the Data Browser."*
 *
 * The capture knows who was born when; the database knows who is still here. The join is safe because entity ids are
 * monotonic and never recycled, so an old id either finds the same entity or finds nothing — never a different one.
 */

/** Ids listed under the summary. The survival split is computed over the WHOLE cohort — see useFullCohortIds. */
const PREVIEW_IDS = 200;

function fmt(n: number): string {
  return n.toLocaleString();
}

/** The per-tick strip. Bars are relative to the busiest tick, so a storm is visible without reading any number. */
function LifecycleStrip({
  points,
  selected,
  onSelect,
  colour,
  label,
}: {
  points: { tickNumber: number; entityCount: number; runCount: number }[];
  selected: number | null;
  onSelect: (tick: number) => void;
  colour: string;
  label: string;
}): React.JSX.Element {
  const max = useMemo(() => points.reduce((m, p) => Math.max(m, p.entityCount), 0), [points]);

  if (points.length === 0) {
    return <div className="px-2 py-1 text-fs-sm text-muted-foreground">No {label} recorded in this capture.</div>;
  }

  return (
    <div className="flex items-end gap-px overflow-x-auto px-2 py-1" style={{ height: 56 }} data-testid={`strip-${label}`}>
      {points.map((p) => (
        <button
          key={p.tickNumber}
          type="button"
          onClick={() => onSelect(p.tickNumber)}
          title={`tick ${p.tickNumber}: ${fmt(p.entityCount)} ${label} in ${fmt(p.runCount)} run(s)`}
          data-testid={`strip-bar-${p.tickNumber}`}
          className={`w-1.5 shrink-0 rounded-t ${colour} ${selected === p.tickNumber ? 'ring-1 ring-foreground' : ''}`}
          style={{ height: `${max > 0 ? Math.max(3, (p.entityCount / max) * 48) : 3}px` }}
        />
      ))}
    </div>
  );
}

function Field({ label, value, testId }: { label: string; value: React.ReactNode; testId?: string }): React.JSX.Element {
  return (
    <div className="flex items-baseline gap-2">
      <span className="w-40 shrink-0 text-fs-sm text-muted-foreground">{label}</span>
      <span className="font-mono text-fs-sm text-foreground" data-testid={testId}>
        {value}
      </span>
    </div>
  );
}

export default function CohortsPanel(_props: IDockviewPanelProps): React.JSX.Element {
  const hasDatabase = useSessionCapability('database');
  const [kind, setKind] = useState<'spawn' | 'destroy'>('spawn');
  const [tick, setTick] = useState<number | null>(null);

  const { data: spawnSeries = [] } = useLifecycleSeries('spawn');
  const { data: destroySeries = [] } = useLifecycleSeries('destroy');
  const series = kind === 'spawn' ? spawnSeries : destroySeries;

  const { data: cohort } = useEntityCohort(kind, tick, tick, null, 0, PREVIEW_IDS);
  const { list: archetypes } = useArchetypeList();
  const setCohort = useDataBrowserStore((s) => s.setCohort);
  const setArchetype = useDataBrowserStore((s) => s.setArchetype);

  // The database's archetype is found by NAME — the capture's archetype id is a per-process catalog id and joining on
  // it is design §5.3's landmine. Its routing id then has to agree with the cohort's before anything is asked.
  const dbArchetype = useMemo(
    () => (cohort?.archetypeName ? archetypes.find((a) => a.name === cohort.archetypeName) ?? null : null),
    [cohort?.archetypeName, archetypes],
  );
  const dbRoutingId = useMemo(() => {
    // The database's routing id is read off its own entities, not asked for separately: every id embeds it, and using
    // the same source the join will use is what makes the check meaningful rather than ceremonial.
    if (!cohort?.entityIds?.length || !dbArchetype) return null;
    return cohort.routingId ?? null;
  }, [cohort, dbArchetype]);

  const blocker: CohortJoinBlocker | null = canJoinCohort(cohort, hasDatabase, dbRoutingId);

  // Resolve the WHOLE cohort, not the preview page. "160 of 200 alive" printed beside "620 spawned" is arithmetic the
  // reader has to reconcile, and the obvious reconciliation — that the cohort is 200 — is wrong.
  const { data: fullCohort } = useFullCohortIds(kind, tick, tick, null, blocker === null ? cohort?.totalEntities ?? 0 : 0);
  const { data: resolution } = useCohortSurvival(
    blocker === null && dbArchetype ? String(dbArchetype.archetypeId) : null,
    blocker === null ? fullCohort?.ids ?? null : null,
  );
  const survival = summariseSurvival(resolution);
  const sampled = fullCohort != null && !fullCohort.complete;

  const openInDataBrowser = (ids: string[], label: string) => {
    if (!dbArchetype) return;
    setArchetype(String(dbArchetype.archetypeId));
    setCohort({ entityIds: ids, label });
    openDataBrowser();
  };

  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      <div className="wb-pane-header flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-fs-base font-semibold text-foreground">Entity Lifecycle</h3>
        <div className="ml-2 flex items-center gap-1">
          {(['spawn', 'destroy'] as const).map((k) => (
            <button
              key={k}
              type="button"
              onClick={() => {
                setKind(k);
                setTick(null);
              }}
              data-testid={`kind-${k}`}
              className={`rounded px-2 py-0.5 text-fs-sm ${
                kind === k ? 'bg-primary/15 text-foreground' : 'text-muted-foreground hover:text-foreground'
              }`}
            >
              {k === 'spawn' ? 'Spawned' : 'Destroyed'}
            </button>
          ))}
        </div>
        <span className="ml-auto text-fs-sm text-muted-foreground">
          {fmt(series.reduce((t, p) => t + p.entityCount, 0))} {kind === 'spawn' ? 'spawned' : 'destroyed'} across{' '}
          {fmt(series.length)} tick(s)
        </span>
      </div>

      <LifecycleStrip
        points={series}
        selected={tick}
        onSelect={setTick}
        colour={kind === 'spawn' ? 'bg-emerald-500/70' : 'bg-rose-500/70'}
        label={kind === 'spawn' ? 'spawns' : 'destroys'}
      />

      <div className="flex-1 overflow-auto border-t border-border p-3">
        {tick === null ? (
          <p className="text-fs-sm text-muted-foreground">
            Pick a tick above to see which entities were {kind === 'spawn' ? 'born' : 'destroyed'} there.
          </p>
        ) : !cohort ? (
          <p className="text-fs-sm text-muted-foreground">Loading…</p>
        ) : (
          <div className="flex flex-col gap-3">
            <div className="flex flex-col gap-1">
              <Field label="Tick" value={fmt(tick)} />
              <Field label={kind === 'spawn' ? 'Entities spawned' : 'Entities destroyed'} value={fmt(cohort.totalEntities)} />
              <Field label="Archetype" value={cohort.archetypeName ?? <span className="text-muted-foreground">unresolved</span>} />
              {/* Both identifiers, labelled. They are usually different numbers for the same archetype (§5.3), and a UI
                  that showed only one would leave the reader to assume it was the other. */}
              <Field
                label="Routing id (durable)"
                testId="cohort-routing-id"
                value={cohort.routingId ?? <span className="text-muted-foreground">mixed</span>}
              />
              <Field
                label="Catalog id (this capture)"
                testId="cohort-catalog-id"
                value={cohort.catalogArchetypeId ?? <span className="text-muted-foreground">not recorded</span>}
              />
            </div>

            {/* §5.7: a bridge whose key did not survive is ABSENT, and says why — never a disabled control with no
                explanation, and never a zero that reads as a real measurement. */}
            {blocker !== null ? (
              <div className="rounded border border-border bg-muted/30 p-2 text-fs-sm text-muted-foreground" data-testid="cohort-blocked">
                {explainBlocker(blocker, cohort.archetypeName)}
              </div>
            ) : (
              <div className="flex flex-col gap-2 rounded border border-border p-2" data-testid="cohort-survival">
                <div className="text-fs-sm font-semibold text-foreground">Still in the database</div>
                <Field
                  label="Alive"
                  value={`${fmt(survival.alive)} of ${fmt(survival.total)} (${survival.alivePct.toFixed(0)}%)`}
                />
                {sampled && (
                  // Past the cap the split covers a prefix, and says so rather than letting the number read as the whole cohort.
                  <p className="text-fs-sm text-muted-foreground">
                    Sampled the first {fmt(MAX_RESOLVED_COHORT)} of {fmt(cohort.totalEntities)} — the rest were not checked.
                  </p>
                )}
                <Field label="Gone" value={fmt(survival.destroyed)} />
                {survival.foreign > 0 && (
                  <Field label="Wrong archetype" value={<span className="text-destructive">{fmt(survival.foreign)}</span>} />
                )}
                {/* B2 / §4.7: the values behind these ids are CURRENT. Trace-time data is not recoverable from these
                    artefacts, and implying otherwise is the one thing this bridge must not do. */}
                <p className="text-fs-sm text-muted-foreground">
                  Read at TSN {fmt(survival.revision)}. Opening these shows their values <em>now</em>, not as they were at
                  tick {fmt(tick)} — old versions are reclaimed, so trace-time data cannot be recovered.
                </p>
                <div className="flex gap-2">
                  <button
                    type="button"
                    data-testid="open-alive-in-data-browser"
                    disabled={survival.alive === 0}
                    onClick={() =>
                      openInDataBrowser(
                        resolution?.aliveIds ?? [],
                        `${fmt(survival.alive)} of ${fmt(cohort.totalEntities)} ${kind === 'spawn' ? 'spawned' : 'destroyed'} at tick ${fmt(tick)} — still alive`,
                      )
                    }
                    className="rounded border border-border px-2 py-0.5 text-fs-sm text-foreground hover:bg-muted disabled:opacity-40"
                  >
                    Open {fmt(survival.alive)} alive in Data Browser
                  </button>
                </div>
              </div>
            )}

            <div className="flex flex-col gap-1">
              <div className="text-fs-sm text-muted-foreground">
                First {fmt(cohort.entityIds.length)} entity ids{cohort.hasMore ? ' (more not shown)' : ''}
              </div>
              <div className="max-h-48 overflow-auto rounded border border-border p-1 font-mono text-fs-sm">
                {cohort.entityIds.map((id) => (
                  <div key={id}>{id}</div>
                ))}
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
