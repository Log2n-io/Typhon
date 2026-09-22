// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Emptied by #872 step 13.
//
// This file held the producer ref structs for SpatialMaintainInsert and SpatialMaintainUpdateSlowPath — the
// entity-level R-Tree's insert and its escape-triggered remove-and-reinsert. Both were emitted from
// SpatialMaintainer, whose writers went with the tree they wrote to, so the structs had no caller left and the two
// events could never fire.
//
// The TraceEventKind VALUES stay (SpatialMaintainInsert = 138, SpatialMaintainUpdateSlowPath = 139, and the two
// beside them): the enum is a wire format, and reusing a retired number would make an old trace decode as something
// it is not. They are marked retired there rather than deleted here.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
