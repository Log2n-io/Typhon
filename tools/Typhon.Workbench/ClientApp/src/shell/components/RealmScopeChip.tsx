import { useMemo, useRef, useState } from 'react';
import { Activity, Link2, Unlink2 } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Input } from '@/components/ui/input';
import { useRealmList } from '@/hooks/realms/useRealmList';
import { useReportingRealms } from '@/hooks/realms/useReportingRealms';
import { realmLabel } from '@/hooks/realms/types';
import type { Realm } from '@/hooks/realms/types';
import { useRealmScopeStore } from '@/stores/useRealmScopeStore';

/**
 * The realm scope chip (#1083 rung 5) — which world every pillar is narrowed to.
 *
 * **"all realms" is the default and it is spelled out**, rather than shown as an empty chip or as "realm 0". A blank
 * would read as "not loaded yet"; realm 0 would be a lie, and precisely the lie WB-06 just stopped the Query Console
 * telling. The unscoped state is a real, nameable choice and the only one that leaves every panel behaving as it did
 * before this chip existed.
 *
 * **A typeahead, not a `<select>`, and the list is BUSY-FIRST.** This was a native select over every registered realm.
 * Against a galaxy of 1 237 interiors that renders fine and is unusable: the options are there but cannot be found,
 * and the realm anyone actually wants is the one doing work right now. The rate records already name that set — the
 * engine emits a row only for a realm the fence touched — so the realms with numbers on screen are offered first,
 * under their own heading, and the full catalog stays reachable below for scoping to something asleep.
 */
export default function RealmScopeChip() {
  const { list } = useRealmList();
  const reporting = useReportingRealms();
  const realm = useRealmScopeStore((s) => s.realm);
  const linked = useRealmScopeStore((s) => s.linked);
  const setRealm = useRealmScopeStore((s) => s.setRealm);
  const setLinked = useRealmScopeStore((s) => s.setLinked);

  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  const current = realm == null ? null : list.find((r) => r.id === realm);
  const currentLabel = realm == null ? 'all realms' : current ? realmLabel(current) : `realm ${realm}`;

  const { busy, rest } = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    const matches = (r: Realm) => needle === '' || realmLabel(r).toLowerCase().includes(needle) || String(r.id) === needle;
    const shown = list.filter(matches);

    // A realm reporting rates that the CATALOG does not list is real — a run-time registration not yet persisted, or a
    // session that cannot read the catalog at all — so it is synthesised rather than dropped. Dropping it would hide
    // the one realm a user is most likely hunting: the busy one their catalog has never heard of.
    const known = new Set(shown.map((r) => r.id));
    const synthetic = reporting.ids
      .filter((id) => !known.has(id) && (needle === '' || String(id).includes(needle)))
      .map((id) => ({ id, label: `realm ${id}` }));

    return {
      busy: [...shown.filter((r) => reporting.reported(r.id)).map((r) => ({ id: r.id, label: realmLabel(r) })), ...synthetic],
      rest: shown.filter((r) => !reporting.reported(r.id)).map((r) => ({ id: r.id, label: realmLabel(r) })),
    };
  }, [list, filter, reporting]);

  const choose = (id: number | null) => {
    setRealm(id);
    setOpen(false);
    setFilter('');
  };

  return (
    <span className="flex items-center gap-1.5">
      <Popover open={open} onOpenChange={(next) => { setOpen(next); if (!next) { setFilter(''); } }}>
        <PopoverTrigger asChild>
          <button
            type="button"
            aria-label="Realm scope"
            aria-haspopup="listbox"
            aria-expanded={open}
            title="Narrow every panel to one realm (global scope)"
            className="max-w-48 truncate bg-transparent text-inherit outline-none hover:text-foreground"
          >
            {currentLabel}
          </button>
        </PopoverTrigger>

        <PopoverContent align="start" className="w-72 p-0" onOpenAutoFocus={(e) => { e.preventDefault(); inputRef.current?.focus(); }}>
          <div className="border-b p-1.5">
            <Input
              ref={inputRef}
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Filter by name or id…"
              aria-label="Filter realms"
              className="h-7 text-xs"
            />
          </div>

          <div className="max-h-72 overflow-auto py-1" role="listbox" aria-label="Realms">
            <Option label="all realms" selected={realm == null} onSelect={() => choose(null)} />

            {busy.length > 0 && (
              <>
                <Heading>
                  doing work now
                  {/* Named as a window, not a tick: the set is over the same 60 seconds the rates are quoted over, which
                      is what stops it reordering under the pointer fifty times a second. */}
                  <span className="ml-1 font-normal normal-case text-muted-foreground">· in the current window</span>
                </Heading>
                {busy.map((r) => (
                  <Option key={`b${r.id}`} label={r.label} busy selected={realm === r.id} onSelect={() => choose(r.id)} />
                ))}
              </>
            )}

            {rest.length > 0 && (
              <>
                {busy.length > 0 && <Heading>all realms</Heading>}
                {rest.map((r) => (
                  <Option key={`r${r.id}`} label={r.label} selected={realm === r.id} onSelect={() => choose(r.id)} />
                ))}
              </>
            )}

            {busy.length === 0 && rest.length === 0 && (
              <p className="px-3 py-2 text-xs text-muted-foreground">No realm matches “{filter}”.</p>
            )}
          </div>
        </PopoverContent>
      </Popover>

      {/* Only offered once a realm is chosen: unlinking "all realms" narrows nothing to nothing, so the control would
          be a toggle with no effect — the dead affordance IA §7 rules out. */}
      {realm != null && (
        <button
          type="button"
          onClick={() => setLinked(!linked)}
          aria-pressed={linked}
          aria-label={linked ? 'Realm scope linked — click to unlink' : 'Realm scope unlinked — click to re-link'}
          title={
            linked
              ? `Panels are narrowed to ${currentLabel}. Click to stop applying it.`
              : 'The realm is remembered but not applied. Click to apply it again.'
          }
          className={'rounded p-0.5 ' + (linked ? 'text-muted-foreground hover:text-foreground' : 'text-amber-500')}
        >
          {linked ? <Link2 className="h-3.5 w-3.5" /> : <Unlink2 className="h-3.5 w-3.5" />}
        </button>
      )}
    </span>
  );
}

function Heading({ children }: { children: React.ReactNode }) {
  return <p className="px-2 pb-0.5 pt-1.5 text-fs-2xs font-medium uppercase text-muted-foreground">{children}</p>;
}

function Option({ label, selected, busy, onSelect }: { label: string; selected: boolean; busy?: boolean; onSelect: () => void }) {
  return (
    <button
      type="button"
      role="option"
      aria-selected={selected}
      onClick={onSelect}
      className={'flex w-full items-center gap-1.5 px-2 py-1 text-left text-xs hover:bg-accent ' + (selected ? 'bg-accent' : '')}
    >
      {/* The marker is on the BUSY rows rather than the quiet ones: it marks the exception, and on a galaxy of sleeping
          interiors the exception is the realm doing something. */}
      {busy ? <Activity className="h-3 w-3 shrink-0 text-emerald-500" aria-label="reported work in this window" /> : <span className="w-3 shrink-0" />}
      <span className="truncate">{label}</span>
    </button>
  );
}
