using JetBrains.Annotations;
using System;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Background service that periodically updates resource snapshots and raises alerts.
/// </summary>
/// <remarks>
/// <para>
/// This service is timer-based and does not depend on ASP.NET Core's IHostedService.
/// It can be used in any .NET application.
/// </para>
/// <para>
/// <b>Operation:</b>
/// </para>
/// <list type="bullet">
///   <item><description>Periodically calls <see cref="ResourceMetricsExporter.UpdateSnapshot"/></description></item>
///   <item><description>Runs health checks after each snapshot</description></item>
///   <item><description>Raises <see cref="AlertRaised"/> on state transitions</description></item>
/// </list>
/// <example>
/// <code>
/// var service = new ResourceMetricsService(exporter, healthChecker, alertGenerator, options);
/// service.AlertRaised += (sender, alert) =>
/// {
///     Console.WriteLine($"Alert: {alert.Title}");
/// };
/// service.Start();
///
/// // Later...
/// service.Stop();
/// service.Dispose();
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public sealed class ResourceMetricsService : IDisposable
{
    private readonly ResourceMetricsExporter _exporter;
    private readonly ResourceHealthChecker _healthChecker;
    private readonly ResourceAlertGenerator _alertGenerator;
    private readonly ObservabilityBridgeOptions _options;
    private readonly Timer _snapshotTimer;

    private HealthStatus _previousStatus = HealthStatus.Healthy;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Event raised when a resource crosses health thresholds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alerts are only raised on state transitions to avoid flooding:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Healthy → Degraded: Warning alert</description></item>
    ///   <item><description>Healthy → Unhealthy: Critical alert</description></item>
    ///   <item><description>Degraded → Unhealthy: Critical alert (escalation)</description></item>
    /// </list>
    /// <para>
    /// No alert is raised when recovering (Unhealthy → Degraded → Healthy).
    /// </para>
    /// </remarks>
    public event EventHandler<ResourceAlert> AlertRaised;

    /// <summary>
    /// Event raised when health status changes (including recovery).
    /// </summary>
    public event EventHandler<HealthStatusChangedEventArgs> HealthStatusChanged;

    /// <summary>
    /// Creates a new ResourceMetricsService.
    /// </summary>
    /// <param name="exporter">The metrics exporter.</param>
    /// <param name="healthChecker">The health checker.</param>
    /// <param name="alertGenerator">The alert generator.</param>
    /// <param name="options">Configuration options.</param>
    public ResourceMetricsService(
        ResourceMetricsExporter exporter,
        ResourceHealthChecker healthChecker,
        ResourceAlertGenerator alertGenerator,
        ObservabilityBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(healthChecker);
        ArgumentNullException.ThrowIfNull(alertGenerator);
        ArgumentNullException.ThrowIfNull(options);

        _exporter = exporter;
        _healthChecker = healthChecker;
        _alertGenerator = alertGenerator;
        _options = options;

        _snapshotTimer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Whether the service is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// The current health status from the last check.
    /// </summary>
    public HealthStatus CurrentStatus => _previousStatus;

    /// <summary>
    /// Start the background refresh service.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRunning)
        {
            return;
        }

        _isRunning = true;

        // Take initial snapshot immediately
        OnTimerTick(null);

        // Start periodic timer
        _snapshotTimer.Change(_options.SnapshotInterval, _options.SnapshotInterval);
    }

    /// <summary>
    /// Stop the background refresh service.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _snapshotTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _isRunning = false;
    }

    /// <summary>
    /// Force an immediate snapshot update and health check.
    /// </summary>
    /// <returns>The current health status.</returns>
    public HealthStatus ForceUpdate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OnTimerTick(null);
        return _previousStatus;
    }

    private void OnTimerTick(object state)
    {
        try
        {
            // Update snapshot
            var snapshot = _exporter.UpdateSnapshot();

            // Check health
            var newStatus = _healthChecker.CheckHealth();

            // Detect state transitions
            if (newStatus != _previousStatus)
            {
                var oldStatus = _previousStatus;
                _previousStatus = newStatus;

                // Raise status changed event
                HealthStatusChanged?.Invoke(this, new HealthStatusChangedEventArgs(oldStatus, newStatus));

                // Generate alerts on degradation (not on recovery)
                if (newStatus > oldStatus) // HealthStatus enum: Healthy=0, Degraded=1, Unhealthy=2
                {
                    GenerateAndRaiseAlerts(snapshot);
                }
            }
        }
        catch
        {
            // Swallow exceptions to prevent timer death
            // In production, consider logging
        }
    }

    private void GenerateAndRaiseAlerts(ResourceSnapshot snapshot)
    {
        foreach (var alert in _alertGenerator.GenerateAlerts(snapshot))
        {
            AlertRaised?.Invoke(this, alert);
        }
    }

    /// <summary>
    /// Disposes the timer and stops the service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _snapshotTimer.Dispose();
    }
}

/// <summary>
/// Event arguments for health status changes.
/// </summary>
[PublicAPI]
public sealed class HealthStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// The previous health status.
    /// </summary>
    public HealthStatus PreviousStatus { get; }

    /// <summary>
    /// The new health status.
    /// </summary>
    public HealthStatus NewStatus { get; }

    /// <summary>
    /// Whether this is a degradation (status got worse).
    /// </summary>
    public bool IsDegradation => NewStatus > PreviousStatus;

    /// <summary>
    /// Whether this is a recovery (status improved).
    /// </summary>
    public bool IsRecovery => NewStatus < PreviousStatus;

    /// <summary>
    /// Creates new event arguments.
    /// </summary>
    public HealthStatusChangedEventArgs(HealthStatus previousStatus, HealthStatus newStatus)
    {
        PreviousStatus = previousStatus;
        NewStatus = newStatus;
    }
}
