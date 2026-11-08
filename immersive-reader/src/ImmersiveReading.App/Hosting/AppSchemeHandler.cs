using System.Text;
using ImmersiveReading.App.Plugins;

namespace ImmersiveReading.App.Hosting;

/// <summary>
/// Handles <c>ir-app://</c> custom scheme requests for static web UI assets, covers, study-pack outputs, plugin bundles, and SDK bridges.
/// </summary>
public sealed class AppSchemeHandler
{
    private readonly string _wwwrootDir;
    private readonly string _coversDir;
    private readonly string? _extractorOutputDir;
    private readonly PluginHostManager? _pluginHostManager;

    public AppSchemeHandler(
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir = null,
        PluginHostManager? pluginHostManager = null)
    {
        _wwwrootDir = Path.GetFullPath(wwwrootDir);
        _coversDir = Path.GetFullPath(coversDir);
        _extractorOutputDir = !string.IsNullOrEmpty(extractorOutputDir) ? Path.GetFullPath(extractorOutputDir) : null;
        _pluginHostManager = pluginHostManager;
    }

    public Stream Handle(string url, out string contentType)
    {
        return HandleRequest(url, _wwwrootDir, _coversDir, _extractorOutputDir, _pluginHostManager, out contentType);
    }

    public static Stream HandleRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        out string contentType)
        => HandleRequest(url, wwwrootDir, coversDir, extractorOutputDir: null, pluginHostManager: null, out contentType);

    public static Stream HandleRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir,
        out string contentType)
        => HandleRequest(url, wwwrootDir, coversDir, extractorOutputDir, pluginHostManager: null, out contentType);

    public static Stream HandleRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir,
        PluginHostManager? pluginHostManager,
        out string contentType)
    {
        contentType = "text/plain; charset=utf-8";
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            string relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

            // Prevent path traversal
            if (relativePath.Contains("..", StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal))
            {
                return new MemoryStream([]);
            }

            // 1. Virtual Plugin Asset Serving (ir-app://plugins/{pluginId}/{*assetPath})
            if (host == "plugins" || relativePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = host == "plugins" ? relativePath : relativePath["plugins/".Length..];
                var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && pluginHostManager != null)
                {
                    var pluginId = segments[0];
                    var assetSubPath = string.Join('/', segments.Skip(1));
                    var stream = pluginHostManager.OpenAssetStream(pluginId, assetSubPath);
                    if (stream != null)
                    {
                        contentType = GetMimeType(assetSubPath);
                        return stream;
                    }
                }
                return new MemoryStream([]);
            }

            // 2. Virtual SDK Bridge Modules (ir-app://sdk/{moduleName})
            if (host == "sdk" || relativePath.StartsWith("sdk/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = host == "sdk" ? relativePath : relativePath["sdk/".Length..];
                var shim = GetSdkShim(rel);
                if (shim != null)
                {
                    contentType = "application/javascript; charset=utf-8";
                    return new MemoryStream(Encoding.UTF8.GetBytes(shim));
                }
                return new MemoryStream([]);
            }

            string fullWwwroot = Path.GetFullPath(wwwrootDir);
            string fullCovers = Path.GetFullPath(coversDir);
            string? fullExtractor = !string.IsNullOrEmpty(extractorOutputDir) ? Path.GetFullPath(extractorOutputDir) : null;

            if (host == "covers" || relativePath.StartsWith("covers/", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = host == "covers" ? Path.GetFileName(relativePath) : Path.GetFileName(relativePath["covers/".Length..]);
                string fullCoverPath = Path.GetFullPath(Path.Combine(fullCovers, fileName));
                if (IsUnderRoot(fullCoverPath, fullCovers) && File.Exists(fullCoverPath))
                {
                    contentType = GetMimeType(fullCoverPath);
                    return new FileStream(fullCoverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            if (fullExtractor != null &&
                (host == "studypack" || relativePath.StartsWith("studypack/", StringComparison.OrdinalIgnoreCase)))
            {
                string subPath = host == "studypack" ? relativePath : relativePath["studypack/".Length..];
                string fullStudyPath = Path.GetFullPath(Path.Combine(fullExtractor, subPath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnderRoot(fullStudyPath, fullExtractor) && File.Exists(fullStudyPath))
                {
                    contentType = GetMimeType(fullStudyPath);
                    return new FileStream(fullStudyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            // When URL is e.g. "ir-app://assets/ir-reader-injected.css", uri.Host is "assets" and uri.AbsolutePath is "/ir-reader-injected.css".
            if (!string.IsNullOrEmpty(host) && host != "app" && host != "localhost")
            {
                string hostCombined = Path.GetFullPath(Path.Combine(fullWwwroot, host, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnderRoot(hostCombined, fullWwwroot) && File.Exists(hostCombined))
                {
                    contentType = GetMimeType(hostCombined);
                    return new FileStream(hostCombined, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }

            string targetFile = Path.GetFullPath(Path.Combine(fullWwwroot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsUnderRoot(targetFile, fullWwwroot) && File.Exists(targetFile))
            {
                contentType = GetMimeType(targetFile);
                return new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            string distTarget = Path.GetFullPath(Path.Combine(fullWwwroot, "dist", relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsUnderRoot(distTarget, fullWwwroot) && File.Exists(distTarget))
            {
                contentType = GetMimeType(distTarget);
                return new FileStream(distTarget, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }
        catch
        {
            // Fall through
        }

        return new MemoryStream(Encoding.UTF8.GetBytes("Not found"));
    }

    private static bool IsUnderRoot(string fullPath, string rootPath)
    {
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetSdkShim(string relativePath)
    {
        string name = Path.GetFileName(relativePath).ToLowerInvariant();
        return name switch
        {
            "react.js" => """
                const R = (typeof window !== 'undefined' ? window.__IR_RUNTIME__?.React : null) || {};
                export default R;
                export const {
                    useState, useEffect, useLayoutEffect, useInsertionEffect, useMemo, useCallback, useRef,
                    useContext, createContext, useReducer, useId, useTransition, useDeferredValue, useSyncExternalStore,
                    startTransition, memo, forwardRef, lazy, Suspense, Fragment, Children, cloneElement, createElement,
                    isValidElement, createRef
                } = R;
                """,
            "react-dom.js" => """
                const RD = (typeof window !== 'undefined' ? window.__IR_RUNTIME__?.ReactDOM : null) || {};
                export default RD;
                export const { createPortal, flushSync } = RD;
                """,
            "jsx-runtime.js" => """
                const JX = (typeof window !== 'undefined' ? window.__IR_RUNTIME__?.JsxRuntime : null) || {};
                export const jsx = JX.jsx;
                export const jsxs = JX.jsxs;
                export const Fragment = JX.Fragment;
                export default JX;
                """,
            "lucide-react.js" => """
                const L = (typeof window !== 'undefined' ? window.__IR_RUNTIME__?.LucideReact : null) || {};
                export default L;
                export const createLucideIcon = L.createLucideIcon;
                export const {
                    Bot, Sparkles, BrainCircuit, BookOpen, Settings, Layers, Languages, Check, X,
                    ChevronRight, ChevronDown, ChevronLeft, ChevronUp, Play, Pause, RotateCcw,
                    HelpCircle, Info, AlertCircle, MessageSquare, Send, Search, Folder, Tag,
                    Filter, Sliders, Download, Upload, Trash, Edit, Plus, RefreshCw, FileText,
                    Library, Volume2, VolumeX, Moon, Sun, Monitor, Maximize, Minimize
                } = L;
                """,
            "host-sdk.js" => """
                const SDK = (typeof window !== 'undefined' ? window.__IR_RUNTIME__?.HostSdk : null) || {};
                export default SDK;
                export const {
                    usePluginRpc, usePluginConfig, useActiveBook, EnrichedText,
                    PluginPageLayout, PluginDrawerShell, Button
                } = SDK;
                """,
            _ => null
        };
    }

    public static string GetMimeType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}

