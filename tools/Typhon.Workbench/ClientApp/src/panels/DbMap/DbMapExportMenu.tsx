import { useState } from 'react';
import { Download } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';

// The toolbar export menu (Module 15, A4, §4.6). A small popover offering the three exports — the current
// view as a PNG, the whole map as a PNG, and the region table as CSV. The panel owns the export logic.

interface DbMapExportMenuProps {
  onExportViewPng: () => void;
  onExportMapPng: () => void;
  onExportCsv: () => void;
}

export function DbMapExportMenu(props: DbMapExportMenuProps) {
  const [open, setOpen] = useState(false);

  const item = (label: string, run: () => void) => (
    <button
      type="button"
      onClick={() => {
        run();
        setOpen(false);
      }}
      className="w-full rounded px-2 py-1 text-left text-fs-sm text-foreground hover:bg-muted/60"
    >
      {label}
    </button>
  );

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-fs-sm text-muted-foreground hover:text-foreground"
          title="Export the map or the region table"
          data-testid="dbmap-export"
        >
          <Download className="h-3 w-3" /> Export
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-44 p-1" align="start">
        {item('PNG — current view', props.onExportViewPng)}
        {item('PNG — whole map', props.onExportMapPng)}
        {item('CSV — region table', props.onExportCsv)}
      </PopoverContent>
    </Popover>
  );
}
