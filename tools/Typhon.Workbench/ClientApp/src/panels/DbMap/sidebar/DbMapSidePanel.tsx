import type { ReactNode } from 'react';
import { PanelRightClose, PanelRightOpen } from 'lucide-react';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useDbMapStore, type DbMapTab } from '@/stores/useDbMapStore';

// The Database File Map side rail (Module 15, A3, §6.4) — a right-docked, collapsible, tabbed rail. This
// component is pure chrome: tab switching + collapse. The panel builds each tab's content and passes it in,
// so the analysis data flows panel → tab in a single hop.

interface DbMapSidePanelProps {
  legend: ReactNode;
  regions: ReactNode;
  bookmarks: ReactNode;
}

const TABS: { value: DbMapTab; label: string }[] = [
  { value: 'legend', label: 'Legend' },
  { value: 'regions', label: 'Regions' },
  { value: 'bookmarks', label: 'Bookmarks' },
];

export function DbMapSidePanel({ legend, regions, bookmarks }: DbMapSidePanelProps) {
  const activeTab = useDbMapStore((s) => s.activeTab);
  const setActiveTab = useDbMapStore((s) => s.setActiveTab);
  const railCollapsed = useDbMapStore((s) => s.railCollapsed);
  const toggleRail = useDbMapStore((s) => s.toggleRail);

  if (railCollapsed) {
    return (
      <div className="flex h-full w-7 shrink-0 flex-col items-center border-l border-border bg-background py-1">
        <button
          type="button"
          onClick={toggleRail}
          className="rounded p-1 text-muted-foreground hover:text-foreground"
          title="Expand the side rail"
        >
          <PanelRightOpen className="h-3.5 w-3.5" />
        </button>
      </div>
    );
  }

  return (
    <div className="flex h-full w-[280px] shrink-0 flex-col border-l border-border bg-background">
      <Tabs
        value={activeTab}
        onValueChange={(v) => setActiveTab(v as DbMapTab)}
        className="flex min-h-0 flex-1 flex-col"
      >
        <div className="flex items-center gap-1 border-b border-border px-1.5 py-1">
          <TabsList className="h-6 gap-0.5 bg-transparent p-0">
            {TABS.map((t) => (
              <TabsTrigger key={t.value} value={t.value} className="h-5 px-2 py-0 text-[11px]">
                {t.label}
              </TabsTrigger>
            ))}
          </TabsList>
          <button
            type="button"
            onClick={toggleRail}
            className="ml-auto rounded p-1 text-muted-foreground hover:text-foreground"
            title="Collapse the side rail"
          >
            <PanelRightClose className="h-3.5 w-3.5" />
          </button>
        </div>
        <TabsContent value="legend" className="mt-0 min-h-0 flex-1 overflow-y-auto">
          {legend}
        </TabsContent>
        <TabsContent value="regions" className="mt-0 min-h-0 flex-1 overflow-y-auto">
          {regions}
        </TabsContent>
        <TabsContent value="bookmarks" className="mt-0 min-h-0 flex-1 overflow-y-auto">
          {bookmarks}
        </TabsContent>
      </Tabs>
    </div>
  );
}
