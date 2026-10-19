namespace Typhon.Engine.Internals;

/// <summary>
/// Copy-before-write, as the data-file write chokepoint sees it: installed by an online backup for the life of its capture, null otherwise. Storage
/// knows nothing of what a backup does with it.
/// </summary>
internal interface IDataFileWriteHook
{
    /// <summary>
    /// Called inside the shared write gate, before pages <c>[firstPage, firstPage + count)</c> are written. Returns once every page of the run that must
    /// be preserved has been, or once the capture has aborted. Never throws: an internal fault aborts the capture instead (BKP-12).
    /// </summary>
    /// <param name="firstPage">The first file page about to be written.</param>
    /// <param name="count">How many consecutive pages.</param>
    void BeforeWrite(int firstPage, int count);
}
