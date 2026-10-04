using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using ClassicMac.App.ViewModels;
using NativeWebView.Core;
using WebView = NativeWebView.Controls.NativeWebView;

namespace ClassicMac.App.Views;

// Apple Help pages (docs/formats/resources/help-pages.md) and documents' HTML exports: the page made ready by HelpPages is loaded into a native
// web view (NativeWebView: WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux) as a data: URI, with
// JavaScript off; every navigation it starts goes through the model, which lets only the page itself load and turns a
// link to a file of the disk into a selection in the tree. Without an engine (or in a window without a native
// handle, as in tests) the model is told why and shows the source.
internal sealed class HelpWebView
{
    private readonly Window window;
    private readonly Panel host;
    private WebView? webView;
    private MainViewModel? helpModel;

    public HelpWebView(Window window, Panel host)
    {
        this.window = window;
        this.host = host;
        window.DataContextChanged += (_, _) =>
        {
            if (helpModel is not null)
            {
                helpModel.PropertyChanged -= OnHelpModelChanged;
                helpModel.HelpPreview.PropertyChanged -= OnHelpModelChanged;
            }

            helpModel = window.DataContext as MainViewModel;
            if (helpModel is not null)
            {
                helpModel.PropertyChanged += OnHelpModelChanged;
                helpModel.HelpPreview.PropertyChanged += OnHelpModelChanged;
            }
        };
        window.Closed += (_, _) => webView?.Dispose();
    }

    private void OnHelpModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The model's Preview, or its HelpPreview's switch and page.
        if (helpModel is { } model && (sender is MainViewModel && e.PropertyName == nameof(MainViewModel.Preview)
            || sender is HelpPreview && e.PropertyName is nameof(HelpPreview.ShowsHelpRendered) or nameof(HelpPreview.WebUri)))
        {
            ShowHelpPage(model);
        }
    }

    // The page into the web view, made the first time a page is to be rendered.
    private async void ShowHelpPage(MainViewModel model)
    {
        if (model.Preview.Web is not { } page || model.HelpPreview.ShowHelpSource)
        {
            return;
        }

        if (webView is null && model.HelpPreview.WebEngineMessage is null)
        {
            model.HelpPreview.WebEngineMessage = CreateWebView();
        }

        if (webView is null || model.HelpPreview.WebEngineMessage is not null)
        {
            return;
        }

        try
        {
            if (!webView.IsInitialized)
            {
                await webView.InitializeAsync();
            }

            if (ReferenceEquals(model.Preview.Web, page))
            {
                webView.Navigate(page.DataUri);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            model.HelpPreview.WebEngineMessage = $"The web view could not start ({e.Message}), so the page shows as text.";
        }
    }

    // The web view in the help host, or why there is none.
    private string? CreateWebView()
    {
        // A window with no native handle (Avalonia's headless platform gives a zero "STUB" handle) cannot host one.
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
        {
            return "This window has no native web view, so the page shows as text.";
        }

        try
        {
            NativeWebViewRuntime.EnsureCurrentPlatformRegistered();
            var diagnostics = NativeWebViewRuntime.GetCurrentPlatformDiagnostics();
            if (!diagnostics.IsReady)
            {
                var why = diagnostics.Issues.FirstOrDefault()?.Message ?? "the platform's web engine is missing";
                return $"No web engine ({why}), so the page shows as text.";
            }

            if (!NativeWebViewRuntime.Factory.TryCreateNativeWebViewBackend(NativeWebViewRuntime.CurrentPlatform, out var backend))
            {
                return "No web engine for this system, so the page shows as text.";
            }

            var view = new WebView(backend);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicMac", "WebView");
            view.InstanceConfiguration.EnvironmentOptions.UserDataFolder = folder;
            view.InstanceConfiguration.ControllerOptions.IsJavaScriptEnabled = false;
            view.InstanceConfiguration.ControllerOptions.IsInPrivateModeEnabled = true;
            view.IsDevToolsEnabled = false;
            view.IsStatusBarEnabled = false;
            view.NavigationStarted += OnHelpNavigation;
            view.NewWindowRequested += OnHelpNewWindow;
            view.StatusTextChanged += (_, e) => helpModel?.HelpPreview.HoverHelpLink(e.StatusText);
            host.Children.Add(view);
            webView = view;
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or TypeLoadException)
        {
            return $"The web view could not start ({e.Message}), so the page shows as text.";
        }
    }

    // Every navigation goes through the model: only the page itself loads.
    private void OnHelpNavigation(object? sender, NativeWebViewNavigationStartedEventArgs e)
    {
        if (helpModel is not null && e.Uri is { } uri && !helpModel.HelpPreview.FollowHelpLink(uri.OriginalString))
        {
            e.Cancel = true;
        }
    }

    private void OnHelpNewWindow(object? sender, NativeWebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is { } uri)
        {
            helpModel?.HelpPreview.FollowHelpLink(uri.OriginalString);
        }
    }
}
