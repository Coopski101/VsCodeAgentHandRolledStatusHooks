using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CopilotBeaconCore.Services;

public sealed class ToastDetector : BackgroundService
{
    private readonly EventBus _bus;
    private readonly CoreConfig _config;
    private readonly ILogger<ToastDetector> _logger;
    private readonly HashSet<uint> _seenNotificationIds = [];

    public ToastDetector(EventBus bus, CoreConfig config, ILogger<ToastDetector> logger)
    {
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = UserNotificationListener.Current;
        var access = await listener.RequestAccessAsync();

        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            _logger.LogError(
                "Notification access denied ({Status}). Enable in Settings > System > Notifications > Notification access",
                access
            );
            return;
        }

        _logger.LogInformation("Toast detector started — notification access granted");

        // Seed seen IDs with current notifications so we don't replay old ones
        var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var n in existing)
            _seenNotificationIds.Add(n.Id);

        _logger.LogDebug("Seeded {Count} existing notification IDs", _seenNotificationIds.Count);

        // Poll for new notifications. The NotificationChanged event exists but is unreliable
        // in out-of-process scenarios, so polling is the robust approach.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);

            try
            {
                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var notification in notifications)
                {
                    if (!_seenNotificationIds.Add(notification.Id))
                        continue;

                    ProcessNotification(notification);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error polling notifications");
            }
        }
    }

    private void ProcessNotification(UserNotification notification)
    {
        var binding = notification.Notification?.Visual?.GetBinding(
            KnownNotificationBindings.ToastGeneric
        );
        if (binding is null)
            return;

        var textElements = binding.GetTextElements();
        var allText = string.Join(" | ", textElements.Select(t => t.Text));
        var appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";

        if (_config.DebugLogging)
            _logger.LogInformation("Toast from [{App}]: {Text}", appName, allText);

        var isFromVsCode = _config.Keywords.NotificationAppNames.Any(name =>
            appName.Contains(name, StringComparison.OrdinalIgnoreCase)
        );

        if (!isFromVsCode)
            return;

        var lowerText = allText.ToLowerInvariant();

        if (MatchesAny(lowerText, _config.Keywords.WaitingKeywords))
        {
            _logger.LogInformation("Classified as WAITING: {Text}", allText);
            _bus.Publish(
                new CopilotEvent
                {
                    EventType = BeaconEventType.Waiting,
                    Source = BeaconEventSource.Toast,
                    Reason = $"Toast seen: \"{allText}\"",
                }
            );
        }
        else if (MatchesAny(lowerText, _config.Keywords.DoneKeywords))
        {
            _logger.LogInformation("Classified as DONE: {Text}", allText);
            _bus.Publish(
                new CopilotEvent
                {
                    EventType = BeaconEventType.Done,
                    Source = BeaconEventSource.Toast,
                    Reason = $"Toast seen: \"{allText}\"",
                }
            );
        }
        else if (_config.DebugLogging)
        {
            _logger.LogDebug("VS Code toast did not match any keywords: {Text}", allText);
        }
    }

    private static bool MatchesAny(string text, string[] keywords)
    {
        return keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }
}
