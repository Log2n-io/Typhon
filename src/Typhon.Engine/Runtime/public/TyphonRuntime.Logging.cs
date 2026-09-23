using Microsoft.Extensions.Logging;

namespace Typhon.Engine;

public sealed partial class TyphonRuntime
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Replication grid: cell {CellM} m ({SpatialCellM} m spatial), {DimX} x {DimY} x {DimZ} cells, window {Window} x {Window} "
                  + "for R {RadiusM} m, {Shape}")]
    private partial void LogReplicationGrid(double cellM, double spatialCellM, int dimX, int dimY, int dimZ, int window, double radiusM, string shape);
}
