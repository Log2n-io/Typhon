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
    <Table className="text-fs-base">
      <TableHeader>
        <TableRow>
          <TableHead className="w-[20px] text-fs-sm" />
          <TableHead className="text-fs-sm">ID</TableHead>
          <TableHead className="text-fs-sm">Owners</TableHead>
          <TableHead className="text-fs-sm">Archetype</TableHead>
          <TableHead className="text-right text-fs-sm">Filters</TableHead>
          <TableHead className="text-right text-fs-sm">Executions</TableHead>
          <TableHead className="text-right text-fs-sm">Avg wall</TableHead>
          <TableHead className="text-fs-sm">Source</TableHead>
          <TableHead className="w-[28px] text-fs-sm" />
          <TableHead className="w-[28px] text-fs-sm" />
          <TableHead className="w-[24px] text-fs-sm" />
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
