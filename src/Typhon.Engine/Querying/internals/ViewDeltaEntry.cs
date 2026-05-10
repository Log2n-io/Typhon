using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ViewDeltaEntry  // 24 bytes exactly
{
    public long EntityPK;        // 8B offset 0
    public KeyBytes8 BeforeKey;  // 8B offset 8
    public KeyBytes8 AfterKey;   // 8B offset 16
}