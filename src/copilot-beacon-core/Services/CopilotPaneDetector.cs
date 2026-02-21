using System.Windows.Automation;
using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;

namespace CopilotBeaconCore.Services;

/// <summary>
/// Detects Copilot state changes by polling VS Code's UI Automation tree.
///
/// <para><b>How it works:</b></para>
/// <para>
/// VS Code is an Electron (Chromium) app, so its entire DOM is exposed via the
/// Windows UI Automation accessibility tree. This detector enumerates all top-level
/// windows with class <c>Chrome_WidgetWin_1</c> whose title contains "Visual Studio Code",
/// then walks each window's descendant elements looking for specific CSS class names
/// that Copilot's chat view injects into the DOM:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>chat-confirmation-widget-container</c> — present when Copilot is showing a
///     confirmation dialog (Allow / Run / Continue). Triggers <c>copilot.waiting</c>.
///   </item>
///   <item>
///     <c>chat-response-loading</c> — present while Copilot is actively generating a
///     response (spinner visible). When this element disappears, it triggers <c>copilot.done</c>.
///   </item>
/// </list>
///
/// <para><b>Rediscovering class names after breaking changes:</b></para>
/// <para>
/// If a future VS Code or Copilot update renames or restructures these elements, you
/// can locate the new names using one of these approaches:
/// </para>
/// <list type="number">
///   <item>
///     <b>Accessibility Insights for Windows</b> (free Microsoft tool) — use the live
///     inspect mode to hover over elements in VS Code's Copilot chat pane. The ClassName
///     property will show the CSS class string that Chromium exposes.
///   </item>
///   <item>
///     <b>PowerShell / C# snippet</b> — call <c>AutomationElement.FromHandle(hwnd)</c>
///     on a VS Code window, then <c>FindAll(TreeScope.Descendants, TrueCondition)</c>
///     and dump <c>ClassName</c> for every element. Search the output for "chat-" or
///     "confirmation" to find the relevant nodes.
///   </item>
///   <item>
///     <b>VS Code DevTools</b> — open Help → Toggle Developer Tools inside VS Code,
///     inspect the chat panel, and note the class names on the confirmation widget and
///     the loading indicator. Those same class names appear in the Automation tree.
///   </item>
/// </list>
///
/// <para>
/// The top-level window filter (<c>Chrome_WidgetWin_1</c> + title contains "Visual
/// Studio Code") may also need updating if Electron changes its window class name or
/// VS Code changes its title format.
/// </para>
/// </summary>
public sealed class CopilotPaneDetector : BackgroundService
{
    private readonly EventBus _bus;
    private readonly CoreConfig _config;
    private readonly ILogger<CopilotPaneDetector> _logger;

    private bool _wasWaiting;
    private bool _wasLoading;

    public CopilotPaneDetector(EventBus bus, CoreConfig config, ILogger<CopilotPaneDetector> logger)
    {
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This detector scans ALL VS Code windows, not just the focused one.
        // A user may have multiple VS Code windows open — one focused, others in
        // the background. A background window can still have an active Copilot
        // session waiting for confirmation (Allow/Run). We want to detect that
        // regardless of which window is in front.
        //
        // This means both this detector and the ToastDetector may observe the same
        // event. That's fine — the EventBus has state gating that prevents duplicate
        // emissions (e.g., publishing copilot.waiting when already in waiting mode
        // is a no-op).
        _logger.LogInformation(
            "Copilot pane detector started (poll={Poll}ms)",
            _config.PollIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);

            try
            {
                ScanVsCodeWindows();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error scanning VS Code pane");
            }
        }
    }

    private void ScanVsCodeWindows()
    {
        var hasConfirmation = false;
        var hasLoading = false;

        foreach (var hwnd in GetVsCodeWindowHandles())
        {
            var root = AutomationElement.FromHandle(hwnd);
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);

            foreach (AutomationElement el in all)
            {
                var cls = el.Current.ClassName ?? "";

                if (cls.Contains("chat-confirmation-widget-container"))
                    hasConfirmation = true;

                if (cls.Contains("chat-response-loading"))
                    hasLoading = true;

                if (hasConfirmation)
                    break;
            }

            if (hasConfirmation)
                break;
        }

        if (hasConfirmation && !_wasWaiting)
        {
            _logger.LogInformation("Copilot pane: confirmation dialog detected");
            _wasWaiting = true;
            _wasLoading = false;
            _bus.Publish(
                new CopilotEvent
                {
                    EventName = "copilot.waiting",
                    Payload = new ToastPayload
                    {
                        RawText = "[pane] Confirmation dialog visible",
                        Confidence = 1.0,
                        Source = "pane",
                    },
                }
            );
        }
        else if (!hasConfirmation && _wasWaiting)
        {
            _logger.LogInformation("Copilot pane: confirmation dialog dismissed");
            _wasWaiting = false;
        }

        if (!hasLoading && _wasLoading && !hasConfirmation)
        {
            _logger.LogInformation("Copilot pane: response finished");
            _wasLoading = false;
            _bus.Publish(
                new CopilotEvent
                {
                    EventName = "copilot.done",
                    Payload = new ToastPayload
                    {
                        RawText = "[pane] Response completed",
                        Confidence = 1.0,
                        Source = "pane",
                    },
                }
            );
        }
        else if (hasLoading && !_wasLoading)
        {
            _wasLoading = true;
        }
    }

    private List<nint> GetVsCodeWindowHandles()
    {
        var handles = new List<nint>();
        var desktop = AutomationElement.RootElement;
        var children = desktop.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1")
        );

        foreach (AutomationElement win in children)
        {
            if (win.Current.Name.Contains("Visual Studio Code"))
                handles.Add((nint)win.Current.NativeWindowHandle);
        }

        return handles;
    }
}
