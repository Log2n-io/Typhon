import { KeyRound } from 'lucide-react';
import { useComponentSchemas } from '@/hooks/dataBrowser/useComponentSchemas';
import { formatValue } from '@/hooks/dataBrowser/formatValue';
import type { ComponentValue, EntityDetail } from '@/hooks/dataBrowser/types';
import type { ComponentSchema } from '@/hooks/schema/types';

/**
 * Renders one entity's components as a card stack — the Data Browser's contribution to the shared Detail pane. Each card is a
 * component; each field a typed read-out joined to the schema for its name + indexed marker. Reusable: any surface (Data
 * Browser, future Query Console) that has an entity's <see cref="EntityDetail"/> can mount this.
 */
export default function EntityCardsDetail({ detail }: { detail: EntityDetail }) {
  const schemas = useComponentSchemas(detail.components.map((c) => c.typeName));

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-[12px] font-semibold text-foreground">Entity</h3>
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{detail.entityId}</span>
        <span className="ml-auto text-[11px] text-muted-foreground">{detail.components.length} components</span>
      </div>

      <div className="flex-1 overflow-auto p-2">
        {detail.components.map((comp) => (
          <div
            key={comp.typeName}
            className={`mb-2 rounded border border-border ${comp.enabled ? '' : 'opacity-50'}`}
            data-testid="component-card"
            data-type-name={comp.typeName}
            data-enabled={comp.enabled}
          >
            <div className="flex items-center gap-2 border-b border-border bg-muted/40 px-2 py-1">
              <span className="font-mono text-[12px] font-semibold text-foreground">{comp.typeName}</span>
              {!comp.enabled && <span className="ml-auto text-[10px] uppercase text-muted-foreground">disabled</span>}
            </div>
            <div className="divide-y divide-border/60">
              {comp.fields.map((f) => (
                <FieldRow key={f.fieldId} field={f} schema={schemas.get(comp.typeName)} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function FieldRow({ field, schema }: { field: ComponentValue; schema: ComponentSchema | undefined }) {
  const meta = schema?.fields.find((sf) => sf.fieldId === field.fieldId);
  const name = meta?.name ?? `#${field.fieldId}`;
  const text = formatValue(field);
  // Multi-line decoded values (e.g. an AABB rendered as "min(...)\nmax(...)") break onto their own lines instead of truncating.
  const multiline = text.includes('\n');
  return (
    <div className={`flex gap-2 px-2 py-0.5 text-[11px] ${multiline ? 'items-start' : 'items-center'}`}>
      <span className="flex w-2/5 min-w-0 items-center gap-1 truncate font-mono text-muted-foreground" title={name}>
        {meta?.isIndexed && <KeyRound className="h-3 w-3 shrink-0 text-amber-400" aria-label="Indexed field" />}
        {name}
      </span>
      <span className={`flex-1 font-mono tabular-nums text-foreground ${multiline ? 'whitespace-pre-line' : 'truncate'}`} title={text}>
        {text}
      </span>
    </div>
  );
}
