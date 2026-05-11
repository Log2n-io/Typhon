import type { QueryDefinitionDto } from '@/api/generated/model';
import { Table, TableBody, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { QueryCatalogRow } from './QueryCatalogRow';

/**
 * Tabular body for the Query Catalog panel. Receives a pre-filtered list of definitions + the
 * resolved name lookups + the duplicate-detection set, renders one <QueryCatalogRow> per entry.
 *
 * Issue #338 (P5 of #342).
 */
interface TableProps {
  definitions: QueryDefinitionDto[];
  archetypeNames: Map<number, string>;
  systemNames: Map<number, string>;
  duplicateRowIds: Set<string>;
}

export function QueryCatalogTable({ definitions, archetypeNames, systemNames, duplicateRowIds }: TableProps) {
  return (
    <Table className="text-[12px]">
      <TableHeader>
        <TableRow>
          <TableHead className="py-1 w-[20px] text-[11px]" />
          <TableHead className="py-1 text-[11px]">ID</TableHead>
          <TableHead className="py-1 text-[11px]">Owners</TableHead>
          <TableHead className="py-1 text-[11px]">Archetype</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Filters</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Executions</TableHead>
          <TableHead className="py-1 text-right text-[11px]">Avg wall</TableHead>
          <TableHead className="py-1 text-[11px]">Source</TableHead>
          <TableHead className="py-1 w-[28px] text-[11px]" />
          <TableHead className="py-1 w-[28px] text-[11px]" />
          <TableHead className="py-1 w-[24px] text-[11px]" />
        </TableRow>
      </TableHeader>
      <TableBody>
        {definitions.map((d) => {
          const kind = Number(d.instanceId.kind);
          const localId = Number(d.instanceId.localId);
          const rowId = `${kind}:${localId}`;
          const archetypeName = archetypeNames.get(Number(d.targetComponentType)) ?? '';
          const ownerNames = (d.ownerSystemIds ?? [])
            .map((id) => systemNames.get(Number(id)))
            .filter((n): n is string => !!n);
          return (
            <QueryCatalogRow
              key={rowId}
              definition={d}
              archetypeName={archetypeName}
              ownerSystemNames={ownerNames}
              isDuplicate={duplicateRowIds.has(rowId)}
            />
          );
        })}
      </TableBody>
    </Table>
  );
}
