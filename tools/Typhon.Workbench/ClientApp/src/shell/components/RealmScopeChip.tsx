import { Link2, Unlink2 } from 'lucide-react';
import { useRealmList } from '@/hooks/realms/useRealmList';
import { realmLabel } from '@/hooks/realms/types';
import { useRealmScopeStore } from '@/stores/useRealmScopeStore';

/**
 * The realm scope chip (#1083 rung 5) — which world every pillar is narrowed to.
 *
 * **"all realms" is the default and it is spelled out**, rather than shown as an empty chip or as "realm 0". A blank
 * would read as "not loaded yet"; realm 0 would be a lie, and precisely the lie WB-06 just stopped the Query Console
 * telling. The unscoped state is a real, nameable choice and the only one that leaves every panel behaving as it did
 * before this chip existed.
 */
export default function RealmScopeChip() {
  const { list } = useRealmList();
  const realm = useRealmScopeStore((s) => s.realm);
  const linked = useRealmScopeStore((s) => s.linked);
  const setRealm = useRealmScopeStore((s) => s.setRealm);
  const setLinked = useRealmScopeStore((s) => s.setLinked);

  const current = realm == null ? null : list.find((r) => r.id === realm);

  return (
    <span className="flex items-center gap-1.5">
      <select
        aria-label="Realm scope"
        title="Narrow every panel to one realm (global scope)"
        className="max-w-48 truncate bg-transparent text-inherit outline-none"
        value={realm == null ? '' : String(realm)}
        onChange={(e) => setRealm(e.target.value === '' ? null : Number(e.target.value))}
      >
        <option value="">all realms</option>
        {list.map((r) => (
          <option key={r.id} value={String(r.id)}>
            {realmLabel(r)}
          </option>
        ))}
      </select>

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
              ? `Panels are narrowed to ${current ? realmLabel(current) : `realm ${realm}`}. Click to stop applying it.`
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
