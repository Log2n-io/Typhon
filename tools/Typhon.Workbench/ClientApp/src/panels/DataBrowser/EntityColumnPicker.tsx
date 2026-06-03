import { Check, Columns3 } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { ComponentSchema } from '@/hooks/schema/types';
import { samePreviewField, type PreviewField } from '@/hooks/dataBrowser/previewFields';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';

/**
 * Preview-column chooser for the entity list. Lists every field of the archetype's components grouped by component; toggling
 * a field turns its column on/off. "Reset to default" hands `null` back to the store so the schema-derived default applies.
 */
export default function EntityColumnPicker({
  componentNames,
  schemas,
  effective,
  onChange,
}: {
  componentNames: string[];
  schemas: Map<string, ComponentSchema>;
  effective: PreviewField[];
  onChange: (fields: PreviewField[] | null) => void;
}) {
  const { label: componentName } = useComponentNames();
  const toggle = (field: PreviewField) => {
    const exists = effective.some((e) => samePreviewField(e, field));
    onChange(exists ? effective.filter((e) => !samePreviewField(e, field)) : [...effective, field]);
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-fs-sm text-foreground hover:bg-accent"
          title="Choose preview columns"
          data-testid="column-picker"
        >
          <Columns3 className="h-3 w-3" />
          Columns
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="max-h-80 w-64 overflow-auto p-1 text-fs-base">
        {componentNames.map((typeName) => {
          const schema = schemas.get(typeName);
          if (!schema) return null;
          return (
            <div key={typeName} className="mb-1">
              <div className="px-2 py-0.5 text-fs-xs font-semibold uppercase tracking-wide text-muted-foreground">
                {componentName(typeName)}
              </div>
              {schema.fields.map((f) => {
                const pf: PreviewField = { typeName, fieldId: f.fieldId };
                const on = effective.some((e) => samePreviewField(e, pf));
                return (
                  <button
                    key={f.fieldId}
                    type="button"
                    onClick={() => toggle(pf)}
                    className="flex w-full items-center gap-2 rounded px-2 py-0.5 text-left hover:bg-accent"
                  >
                    <Check className={`h-3 w-3 shrink-0 ${on ? 'opacity-100' : 'opacity-0'}`} />
                    <span className="truncate font-mono">{f.name}</span>
                    <span className="ml-auto shrink-0 text-fs-xs text-muted-foreground">{f.typeName}</span>
                  </button>
                );
              })}
            </div>
          );
        })}
        <div className="border-t border-border pt-1">
          <button
            type="button"
            onClick={() => onChange(null)}
            className="w-full rounded px-2 py-0.5 text-left text-fs-sm text-muted-foreground hover:bg-accent"
          >
            Reset to default
          </button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
