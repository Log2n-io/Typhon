import { Database, FileSearch, Plug } from 'lucide-react';
import { Button } from '@/components/ui/button';
export default function PlaceholderStartHere() {
 return (
 <div className="flex h-full flex-col items-center justify-center gap-6 bg-background">
 <p className="text-fs-lg text-muted-foreground">
 Welcome to Typhon Workbench — connect to a database to begin
 </p>
 <div className="flex gap-2">
 <Button variant="ghost" size="sm" className="text-fs-lg" disabled>
 <Database className="mr-1.5 h-3.5 w-3.5" /> Open .typhon File
 </Button>
 <Button variant="ghost" size="sm" className="text-fs-lg" disabled>
 <FileSearch className="mr-1.5 h-3.5 w-3.5" /> Open Trace
 </Button>
 <Button variant="ghost" size="sm" className="text-fs-lg" disabled>
 <Plug className="mr-1.5 h-3.5 w-3.5" /> Attach
 </Button>
 </div>
 </div>
 );
}
