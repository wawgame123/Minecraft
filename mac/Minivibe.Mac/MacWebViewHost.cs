using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Minivibe.Mac;

internal sealed class MacWebViewHost : NativeControlHost
{
    private IntPtr _webView;
    private string? _pendingUrl;
    private string? _pendingHtml;

    public void Navigate(string url)
    {
        _pendingUrl = url;
        _pendingHtml = null;
        ApplyPendingNavigation();
    }

    public void NavigateToString(string html)
    {
        _pendingHtml = html;
        _pendingUrl = null;
        ApplyPendingNavigation();
    }

    public void Reload()
    {
        if (_webView != IntPtr.Zero)
        {
            MacWebKit.Reload(_webView);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return base.CreateNativeControlCore(parent);
        }

        _webView = MacWebKit.CreateWebView();
        ApplyPendingNavigation();
        return new PlatformHandle(_webView, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_webView != IntPtr.Zero)
        {
            MacWebKit.Release(_webView);
            _webView = IntPtr.Zero;
            return;
        }

        base.DestroyNativeControlCore(control);
    }

    private void ApplyPendingNavigation()
    {
        if (_webView == IntPtr.Zero)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyPendingNavigation);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingHtml))
        {
            MacWebKit.LoadHtml(_webView, _pendingHtml);
        }
        else if (!string.IsNullOrWhiteSpace(_pendingUrl))
        {
            MacWebKit.LoadUrl(_webView, _pendingUrl);
        }
    }

    private static class MacWebKit
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
        private const string WebKitLibrary = "/System/Library/Frameworks/WebKit.framework/WebKit";
        private static IntPtr _webKitHandle;

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CGPoint(double x, double y)
        {
            public readonly double X = x;
            public readonly double Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CGSize(double width, double height)
        {
            public readonly double Width = width;
            public readonly double Height = height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CGRect(CGPoint origin, CGSize size)
        {
            public readonly CGPoint Origin = origin;
            public readonly CGSize Size = size;
        }

        public static IntPtr CreateWebView()
        {
            EnsureWebKitLoaded();
            var configuration = Send(GetClass("WKWebViewConfiguration"), "new");
            var allocated = Send(GetClass("WKWebView"), "alloc");
            var frame = new CGRect(new CGPoint(0, 0), new CGSize(1, 1));
            var webView = Send(allocated, "initWithFrame:configuration:", frame, configuration);
            SendVoid(configuration, "release");
            if (webView == IntPtr.Zero)
            {
                throw new InvalidOperationException("macOS не удалось создать WKWebView.");
            }

            SendVoid(webView, "setAutoresizingMask:", 18UL);
            return webView;
        }

        public static void LoadUrl(IntPtr webView, string url)
        {
            var nsUrl = Send(GetClass("NSURL"), "URLWithString:", CreateString(url));
            if (nsUrl == IntPtr.Zero)
            {
                throw new InvalidOperationException("Некорректный URL для WKWebView: " + url);
            }

            var request = Send(GetClass("NSURLRequest"), "requestWithURL:", nsUrl);
            Send(webView, "loadRequest:", request);
        }

        public static void LoadHtml(IntPtr webView, string html)
        {
            Send(webView, "loadHTMLString:baseURL:", CreateString(html), IntPtr.Zero);
        }

        public static void Reload(IntPtr webView)
        {
            Send(webView, "reload");
        }

        public static void Release(IntPtr value)
        {
            SendVoid(value, "release");
        }

        private static IntPtr CreateString(string value)
        {
            return SendString(GetClass("NSString"), "stringWithUTF8String:", value);
        }

        private static void EnsureWebKitLoaded()
        {
            if (_webKitHandle == IntPtr.Zero)
            {
                _webKitHandle = NativeLibrary.Load(WebKitLibrary);
            }
        }

        private static IntPtr GetClass(string name)
        {
            var value = objc_getClass(name);
            return value != IntPtr.Zero
                ? value
                : throw new InvalidOperationException("Objective-C class is unavailable: " + name);
        }

        private static IntPtr Selector(string name) => sel_registerName(name);

        private static IntPtr Send(IntPtr receiver, string selector) =>
            objc_msgSend(receiver, Selector(selector));

        private static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) =>
            objc_msgSend_IntPtr(receiver, Selector(selector), argument);

        private static IntPtr Send(IntPtr receiver, string selector, IntPtr first, IntPtr second) =>
            objc_msgSend_IntPtr_IntPtr(receiver, Selector(selector), first, second);

        private static IntPtr Send(IntPtr receiver, string selector, CGRect frame, IntPtr configuration) =>
            objc_msgSend_CGRect_IntPtr(receiver, Selector(selector), frame, configuration);

        private static IntPtr SendString(IntPtr receiver, string selector, string value) =>
            objc_msgSend_String(receiver, Selector(selector), value);

        private static void SendVoid(IntPtr receiver, string selector) =>
            objc_msgSend_void(receiver, Selector(selector));

        private static void SendVoid(IntPtr receiver, string selector, ulong value) =>
            objc_msgSend_ulong(receiver, Selector(selector), value);

        [DllImport(ObjCLibrary)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjCLibrary)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_CGRect_IntPtr(IntPtr receiver, IntPtr selector, CGRect frame, IntPtr configuration);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_String(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_ulong(IntPtr receiver, IntPtr selector, ulong value);
    }
}
