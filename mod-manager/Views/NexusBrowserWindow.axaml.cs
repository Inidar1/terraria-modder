using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WebViewCore.Events;
using WebViewCore.Enums;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.Views;

public partial class NexusBrowserWindow : Window
{
    private readonly NxmLinkHandler _nxmHandler = null!;
    private string _currentUrl = "";
    private bool _downloadHooked;

    private TaskCompletionSource<InlineBrowserResult?>? _resultTcs;
    private string? _pendingUrl;

    private static readonly string StagingDir = Path.Combine(
        Path.GetTempPath(), "TerrariaModderVault", "staging");

    /// <summary>
    /// Fired when a result is ready (nxm link or downloaded file).
    /// Legacy event kept for backwards compat — new code should use GetResultAsync().
    /// </summary>
    public event Action<NxmLink>? NxmLinkIntercepted;

    // Hide ads and strip page chrome on Nexus Mods pages
    private const string AdBlockScript = """
        (function() {
            var css = document.createElement('style');
            css.textContent = `
                .premium-banner, .premium-nag,
                [id*="premium"], [class*="premium-banner"],
                [class*="adsbygoogle"], [id*="adsbygoogle"],
                ins.adsbygoogle, [data-ad-slot],
                .banner-ad, .ad-container, .ad-wrapper,
                [class*="ad-banner"], [id*="ad-banner"],
                .skyrim-together, .donation-box,
                .head-container .wrap + div,
                div[class*="BannerAd"],
                div[class*="RightSidebar"] > div:first-child,
                .membersupport, .member-support,
                [class*="PremiumBanner"], [class*="premium-upsell"],
                iframe[src*="ads"], iframe[src*="doubleclick"],
                .patreon-banner, .ko-fi-banner,
                header, .page-header, nav.navbar,
                #head, .head-container,
                .sidebar, .right-column, aside,
                div[class*="RightSidebar"],
                #comment-container, .comments-container, .comment-section,
                footer, .footer, #footer
            { display: none !important; visibility: hidden !important; height: 0 !important; overflow: hidden !important; }
            `;
            document.head.appendChild(css);
        })();
        """;

    // Remove ad/tracking iframes (Wabbajack-style), keep consent + cloudflare
    private const string IframeRemovalScript = """
        (function() {
            document.querySelectorAll('iframe').forEach(function(iframe) {
                var src = (iframe.src || '').toLowerCase();
                if (src.includes('consent') || src.includes('cloudflare') || src.includes('recaptcha'))
                    return;
                iframe.remove();
            });
        })();
        """;

    // Intercept nxm:// links via postMessage — catches links that NavigationStarting misses
    private const string NxmInterceptScript = """
        (function() {
            if (window.__nxmInterceptInstalled) return;
            window.__nxmInterceptInstalled = true;

            function sendNxm(url) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'nxm', url: url }));
            }

            // Intercept clicks on nxm:// links
            document.addEventListener('click', function(e) {
                var link = e.target.closest('a[href^="nxm://"]');
                if (link) {
                    e.preventDefault();
                    e.stopPropagation();
                    sendNxm(link.href);
                }
            }, true);

            // Intercept programmatic nxm:// navigations
            var origOpen = window.open;
            window.open = function(url) {
                if (url && typeof url === 'string' && url.toLowerCase().startsWith('nxm://')) {
                    sendNxm(url);
                    return null;
                }
                return origOpen.apply(this, arguments);
            };

            // Intercept location.assign and location.replace
            var origAssign = Location.prototype.assign;
            Location.prototype.assign = function(url) {
                if (typeof url === 'string' && url.toLowerCase().startsWith('nxm://')) {
                    sendNxm(url);
                    return;
                }
                return origAssign.call(this, url);
            };
            var origReplace = Location.prototype.replace;
            Location.prototype.replace = function(url) {
                if (typeof url === 'string' && url.toLowerCase().startsWith('nxm://')) {
                    sendNxm(url);
                    return;
                }
                return origReplace.call(this, url);
            };

            // Auto-scan for nxm:// links on the page
            function scanForNxmLinks() {
                var links = document.querySelectorAll('a[href^="nxm://"]');
                if (links.length > 0) {
                    sendNxm(links[0].href);
                }
            }

            scanForNxmLinks();
            setTimeout(scanForNxmLinks, 500);
            setTimeout(scanForNxmLinks, 1500);

            // Monitor for dynamically added nxm:// links
            var observer = new MutationObserver(function(mutations) {
                mutations.forEach(function(mutation) {
                    mutation.addedNodes.forEach(function(node) {
                        if (node.nodeType === 1) {
                            if (node.tagName === 'A' && node.href && node.href.startsWith('nxm://')) {
                                sendNxm(node.href);
                                return;
                            }
                            var links = node.querySelectorAll ? node.querySelectorAll('a[href^="nxm://"]') : [];
                            if (links.length > 0) {
                                sendNxm(links[0].href);
                            }
                        }
                    });
                });
            });
            observer.observe(document.body, { childList: true, subtree: true });
        })();
        """;

    public NexusBrowserWindow() => InitializeComponent();

    public NexusBrowserWindow(NxmLinkHandler nxmHandler)
    {
        _nxmHandler = nxmHandler;
        InitializeComponent();
        Directory.CreateDirectory(StagingDir);

        Browser.NavigationStarting += OnNavigationStarting;
        Browser.NavigationCompleted += OnNavigationCompleted;
        Browser.WebViewNewWindowRequested += OnNewWindowRequested;
        Browser.WebMessageReceived += OnWebMessageReceived;
        Browser.WebViewCreated += OnWebViewCreated;
    }

    /// <summary>
    /// Returns a task that completes when the user triggers a download (nxm or file).
    /// </summary>
    public Task<InlineBrowserResult?> GetResultAsync()
    {
        _resultTcs = new TaskCompletionSource<InlineBrowserResult?>();
        return _resultTcs.Task;
    }

    public void SetContext(string modName)
    {
        ModNameText.Text = $"Installing: {modName}";
        InstructionHeader.IsVisible = true;
    }

    public void NavigateTo(string url)
    {
        _currentUrl = url;
        UrlDisplay.Text = url;
        _pendingUrl = url;
        // WebView2 may not be initialized yet — OnWebViewCreated will flush _pendingUrl
        try { Browser.Url = new Uri(url); } catch { }
    }

    private void OnNavigationStarting(object? sender, WebViewUrlLoadingEventArg e)
    {
        var url = e.Url;
        if (url == null) return;

        var urlStr = url.ToString();

        // Intercept nxm:// links
        if (urlStr.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            HandleNxmUrl(urlStr);
            return;
        }

        _currentUrl = urlStr;
        UrlDisplay.Text = urlStr;
    }

    private async void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e)
    {
        BackButton.IsEnabled = Browser.IsCanGoBack;
        ForwardButton.IsEnabled = Browser.IsCanGoForward;

        // Inject ad blocker CSS, remove tracking iframes, and nxm interceptor on Nexus pages
        if (_currentUrl.Contains("nexusmods.com"))
        {
            try
            {
                await Browser.ExecuteScriptAsync(AdBlockScript);
                await Browser.ExecuteScriptAsync(IframeRemovalScript);
                await Browser.ExecuteScriptAsync(NxmInterceptScript);
            }
            catch { }
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowEventArgs e)
    {
        // Prevent popups — navigate in the same view
        e.UrlLoadingStrategy = UrlRequestStrategy.CancelLoad;
        if (e.Url != null)
            Browser.Url = e.Url;
    }

    private void OnWebMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        var message = e.Message;
        if (string.IsNullOrEmpty(message)) return;

        try
        {
            if (message.Contains("\"type\"") && message.Contains("\"nxm\""))
            {
                var urlStart = message.IndexOf("\"url\"", StringComparison.Ordinal);
                if (urlStart >= 0)
                {
                    urlStart = message.IndexOf('"', urlStart + 5) + 1;
                    var urlEnd = message.IndexOf('"', urlStart);
                    if (urlStart > 0 && urlEnd > urlStart)
                    {
                        var nxmUrl = message[urlStart..urlEnd];
                        HandleNxmUrl(nxmUrl);
                    }
                }
            }
        }
        catch { }
    }

    private void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        if (!e.IsSucceed)
        {
            App.AppLogger?.Error($"WebView2 init failed (window): {e.Message}");
            return;
        }
        TryHookDownloadStarting();

        // Flush any navigation that was requested before WebView2 was ready
        if (_pendingUrl != null)
        {
            Browser.Url = new Uri(_pendingUrl);
            _pendingUrl = null;
        }
    }

    private void TryHookDownloadStarting()
    {
        if (_downloadHooked) return;

        try
        {
            var platformView = Browser.PlatformWebView;
            if (platformView == null) return;

            var coreWebView2Prop = platformView.GetType().GetProperty("CoreWebView2");
            var coreWebView2 = coreWebView2Prop?.GetValue(platformView);
            if (coreWebView2 == null) return;

            var downloadEvent = coreWebView2.GetType().GetEvent("DownloadStarting");
            if (downloadEvent == null) return;

            var handlerType = downloadEvent.EventHandlerType;
            if (handlerType == null) return;

            var method = GetType().GetMethod(nameof(OnCoreDownloadStarting),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return;

            var handler = Delegate.CreateDelegate(handlerType, this, method);
            downloadEvent.AddEventHandler(coreWebView2, handler);
            _downloadHooked = true;
        }
        catch { }
    }

    /// <summary>
    /// Called via reflection from CoreWebView2.DownloadStarting.
    /// Redirects file downloads to staging folder and monitors completion.
    /// </summary>
    private void OnCoreDownloadStarting(object? sender, object e)
    {
        try
        {
            var opProp = e.GetType().GetProperty("DownloadOperation");
            var op = opProp?.GetValue(e);
            if (op == null) return;

            var uriProp = op.GetType().GetProperty("Uri");
            var uri = uriProp?.GetValue(op) as string;

            // If it's an nxm:// link that somehow triggered a download, handle it
            if (!string.IsNullOrEmpty(uri) && uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                var cancelProp = e.GetType().GetProperty("Cancel");
                cancelProp?.SetValue(e, true);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleNxmUrl(uri));
                return;
            }

            // For real file downloads: redirect to staging folder
            var fileName = "download.zip";
            if (!string.IsNullOrEmpty(uri))
            {
                try
                {
                    var uriObj = new Uri(uri);
                    var pathName = Path.GetFileName(uriObj.LocalPath);
                    if (!string.IsNullOrEmpty(pathName))
                        fileName = pathName;
                }
                catch { }
            }

            var resultPath = Path.Combine(StagingDir, $"{Guid.NewGuid():N}_{fileName}");

            // Set save path and suppress save dialog
            var resultPathProp = e.GetType().GetProperty("ResultFilePath");
            resultPathProp?.SetValue(e, resultPath);

            var handledProp = e.GetType().GetProperty("Handled");
            handledProp?.SetValue(e, true);

            // Monitor download completion via StateChanged event
            var stateEvent = op.GetType().GetEvent("StateChanged");
            if (stateEvent != null)
            {
                var path = resultPath;
                EventHandler<object> handler = null!;
                handler = (s, args) =>
                {
                    try
                    {
                        var stateProp = op.GetType().GetProperty("State");
                        var state = stateProp?.GetValue(op)?.ToString();

                        if (state == "Completed")
                        {
                            stateEvent.RemoveEventHandler(op, handler);
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleDownloadedFile(path));
                        }
                        else if (state == "Interrupted")
                        {
                            stateEvent.RemoveEventHandler(op, handler);
                            try { if (File.Exists(path)) File.Delete(path); } catch { }
                        }
                    }
                    catch { }
                };
                stateEvent.AddEventHandler(op, handler);
            }
        }
        catch { }
    }

    private void HandleDownloadedFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var result = new InlineBrowserResult { DownloadedFilePath = filePath };
        _resultTcs?.TrySetResult(result);
        _resultTcs = null;
        Close();
    }

    private void HandleNxmUrl(string url)
    {
        var link = _nxmHandler.Parse(url);
        if (link != null && _nxmHandler.IsTerrariaLink(link))
        {
            // Fire legacy event
            NxmLinkIntercepted?.Invoke(link);

            // Set result for new API
            var result = new InlineBrowserResult { NxmLink = link };
            _resultTcs?.TrySetResult(result);
            _resultTcs = null;

            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // If window closes without a result, signal null
        _resultTcs?.TrySetResult(null);
        _resultTcs = null;
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Browser.GoBack();
    private void OnForward(object? sender, RoutedEventArgs e) => Browser.GoForward();
    private void OnRefresh(object? sender, RoutedEventArgs e) => Browser.Reload();
}
