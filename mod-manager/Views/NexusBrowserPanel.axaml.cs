using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WebViewCore.Events;
using WebViewCore.Enums;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.Views;

public partial class NexusBrowserPanel : UserControl
{
    private readonly NxmLinkHandler _nxmHandler = new();
    private TaskCompletionSource<InlineBrowserResult?>? _tcs;
    private string _currentUrl = "";
    private string? _pendingUrl;
    private bool _eventsWired;
    private bool _downloadHooked;

    private static readonly string StagingDir = Path.Combine(
        Path.GetTempPath(), "TerrariaModderVault", "staging");

    // Hide ads, strip page chrome, and remove distractions on Nexus pages
    private const string PageCleanupScript = """
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

    // Remove ad/tracking iframes (Wabbajack-style), keep consent + cloudflare + recaptcha
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

            // Auto-scan: find any existing nxm:// links on the page and send the first one.
            // This catches the "click here to download manually" link on the download confirmation page.
            function scanForNxmLinks() {
                var links = document.querySelectorAll('a[href^="nxm://"]');
                if (links.length > 0) {
                    sendNxm(links[0].href);
                }
            }

            // Scan now and again shortly (for dynamically rendered content)
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

    /// <summary>
    /// Called on the UI thread as soon as a file download starts (before it completes).
    /// Set this before calling OpenAsync to be notified when the user triggers a download.
    /// Cleared automatically after it fires.
    /// </summary>
    public Action? DownloadStartedCallback { get; set; }

    public NexusBrowserPanel()
    {
        InitializeComponent();
        Directory.CreateDirectory(StagingDir);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireEvents();
    }

    private void WireEvents()
    {
        if (_eventsWired) return;
        _eventsWired = true;

        Browser.NavigationStarting += OnNavigationStarting;
        Browser.NavigationCompleted += OnNavigationCompleted;
        Browser.WebViewNewWindowRequested += OnNewWindowRequested;
        Browser.WebMessageReceived += OnWebMessageReceived;
        Browser.WebViewCreated += OnWebViewCreated;
    }

    /// <summary>
    /// Shows the browser panel and navigates to the given URL.
    /// Returns when the user triggers a download (nxm link or file) or closes the panel.
    /// </summary>
    public Task<InlineBrowserResult?> OpenAsync(string url, string modName, string? toastMessage = null)
    {
        _tcs = new TaskCompletionSource<InlineBrowserResult?>();
        _currentUrl = url;

        ModNameText.Text = modName;
        UrlDisplay.Text = url;

        if (toastMessage != null)
        {
            ToastText.Text = toastMessage;
            ToastBorder.IsVisible = true;
        }
        else
        {
            ToastBorder.IsVisible = false;
        }

        IsVisible = true;
        _pendingUrl = url;
        // WebView2 may not be initialized yet — OnWebViewCreated will flush _pendingUrl
        try { Browser.Url = new Uri(url); } catch { }

        return _tcs.Task;
    }

    public void Close()
    {
        IsVisible = false;
        ToastBorder.IsVisible = false;
        _tcs?.TrySetResult(null);
        _tcs = null;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnNavigationStarting(object? sender, WebViewUrlLoadingEventArg e)
    {
        var url = e.Url;
        if (url == null) return;

        var urlStr = url.ToString();

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

        if (_currentUrl.Contains("nexusmods.com"))
        {
            try
            {
                await Browser.ExecuteScriptAsync(PageCleanupScript);
                await Browser.ExecuteScriptAsync(IframeRemovalScript);
                await Browser.ExecuteScriptAsync(NxmInterceptScript);
            }
            catch { }
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowEventArgs e)
    {
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
            App.AppLogger?.Error($"WebView2 init failed (panel): {e.Message}");
            return;
        }
        TryHookDownloadStarting();

        // Flush any navigation requested before WebView2 was ready
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

            // Fire the download-started callback immediately (before waiting for completion)
            var downloadStartedCb = DownloadStartedCallback;
            DownloadStartedCallback = null;
            if (downloadStartedCb != null)
                Avalonia.Threading.Dispatcher.UIThread.Post(downloadStartedCb);

            // Monitor download completion via StateChanged event
            var stateEvent = op.GetType().GetEvent("StateChanged");
            if (stateEvent != null)
            {
                var path = resultPath; // capture for closure
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
                            // Resolve TCS so OpenAsync doesn't hang
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                var tcs = _tcs;
                                _tcs = null;
                                tcs?.TrySetResult(null);
                            });
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

        var tcs = _tcs;
        IsVisible = false;
        ToastBorder.IsVisible = false;
        _tcs = null;
        tcs?.TrySetResult(new InlineBrowserResult { DownloadedFilePath = filePath });
    }

    private void HandleNxmUrl(string url)
    {
        var link = _nxmHandler.Parse(url);
        if (link != null && _nxmHandler.IsTerrariaLink(link))
        {
            var tcs = _tcs;
            IsVisible = false;
            ToastBorder.IsVisible = false;
            _tcs = null;
            tcs?.TrySetResult(new InlineBrowserResult { NxmLink = link });
        }
    }

    public void ClearCookies()
    {
        try
        {
            var platformView = Browser.PlatformWebView;
            if (platformView == null) return;

            var coreWebView2Prop = platformView.GetType().GetProperty("CoreWebView2");
            var coreWebView2 = coreWebView2Prop?.GetValue(platformView);
            if (coreWebView2 == null) return;

            var cookieManagerProp = coreWebView2.GetType().GetProperty("CookieManager");
            var cookieManager = cookieManagerProp?.GetValue(coreWebView2);
            if (cookieManager == null) return;

            cookieManager.GetType().GetMethod("DeleteAllCookies")?.Invoke(cookieManager, null);
        }
        catch { }
    }

    // Nav bar button handlers
    private void OnBack(object? sender, RoutedEventArgs e) => Browser.GoBack();
    private void OnForward(object? sender, RoutedEventArgs e) => Browser.GoForward();
    private void OnRefresh(object? sender, RoutedEventArgs e) => Browser.Reload();

}
