using System.Text.Json;
using System.Drawing;
using System.Runtime.InteropServices;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Reads the clipboard (Perception, ungated): text goes out in an untrusted block, images as PNG with the
/// screenshot size limit applied. Clipboard access needs STA and is retried on COM busy errors.
/// </summary>
public sealed class ClipboardReadTool : ToolBase
{
    private readonly Services _s;

    public ClipboardReadTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_clipboard_read";
    public override ToolKind Kind => ToolKind.Perception;

    public override string Description =>
        "Reads the current clipboard: text comes back in an untrusted block, images as PNG (long edge scaled to " +
        "the screenshot limit; note it is not a screen frame, so there is no image_id mapping). Returns kind " +
        "text|image|empty. Ungated.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var (kind, text, bitmap) = await WithRetryAsync(Read, ctx);
        switch (kind)
        {
            case "text":
                return ScreenTextResult(new { ok = true, kind = "text", length = text!.Length }, text);
            case "image":
            {
                using var bmp = bitmap!;
                var (scaled, _) = ImageOps.FitLongEdge(bmp, _s.Config.MaxScreenshotEdge);
                using var scaledDisposer = scaled;
                var png = ImageOps.ToPng(scaled);
                ctx.ScreenshotSha256 = ImageOps.Sha256Hex(png);
                return JsonImageResult(new
                {
                    ok = true,
                    kind = "image",
                    size = new { width = bmp.Width, height = bmp.Height },
                    scaled = new { width = scaled.Width, height = scaled.Height },
                    format = "png",
                    max_edge = _s.Config.MaxScreenshotEdge
                }, png);
            }
            default:
                return JsonResult(new { ok = true, kind = "empty" });
        }
    }

    private (string kind, string? text, Bitmap? bitmap) Read()
    {
        return _s.Ui.Invoke<(string, string?, Bitmap?)>(() =>
        {
            if (Clipboard.ContainsText())
            {
                return ("text", Clipboard.GetText(), null);
            }
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                return img == null ? ("empty", null, null) : ("image", null, new Bitmap(img));
            }
            return ("empty", null, null);
        });
    }

    internal static bool IsBusy(ExternalException ex) =>
        ex.HResult == unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    private async Task<T> WithRetryAsync<T>(Func<T> action, ToolContext ctx)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException ex) when (attempt < 5 && IsBusy(ex))
            {
                await Task.Delay(60 * attempt, ctx.Cancel);
            }
            catch (ExternalException ex)
            {
                throw new ToolException("clipboard_failed", $"Clipboard is busy or unreadable: {ex.Message}.",
                    "Retry in a moment; if it persists another application holds the clipboard open.");
            }
        }
    }
}

/// <summary>
/// Writes the clipboard: text or a PNG image (image_base64). Gated like every action (foreground window) and
/// redacted in the audit log. Retried on COM busy errors.
/// </summary>
public sealed class ClipboardWriteTool : ToolBase
{
    private readonly Services _s;

    public ClipboardWriteTool(Services services)
    {
        _s = services;
    }

    public override string Name => "windows_clipboard_write";
    public override ToolKind Kind => ToolKind.Action;
    public override IReadOnlyList<string> RedactedArguments => new[] { "text", "image_base64" };

    public override string Description =>
        "Sets the clipboard content: exactly one of text (string) or image_base64 (PNG bytes, base64). " +
        "Useful to prepare for ctrl+v. The value is redacted in the audit log. Returns the kind written. " +
        "Retry on clipboard_busy errors if another app holds the clipboard.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            text = new { type = "string", description = "Text to place on the clipboard" },
            image_base64 = new { type = "string", description = "PNG image bytes, base64-encoded" }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, ToolContext ctx)
    {
        var hasText = HasArgument(arguments, "text");
        var hasImage = HasArgument(arguments, "image_base64");
        if (hasText == hasImage)
        {
            throw new ToolException("bad_request", "Pass exactly one of text or image_base64.",
                "Use text for strings, image_base64 for PNG images (base64).");
        }

        var window = _s.Session.ResolveWindow(GetStringArgument(arguments, "window"));
        await ctx.Gate.EnsureControlAsync(window, ctx);
        ctx.ThrowIfCancelled();

        if (hasImage)
        {
            var b64 = GetStringArgument(arguments, "image_base64")!;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                throw new ToolException("bad_request", "image_base64 is not valid base64.",
                    "Base64-encode the PNG bytes, or take a windows_screenshot and reuse its base64 data.");
            }
            using var stream = new MemoryStream(bytes);
            using var image = new Bitmap(stream);
            await WithRetryAsync(() => _s.Ui.Invoke(() =>
            {
                Clipboard.SetImage(image);
                return true;
            }), ctx);
            return JsonResult(new { ok = true, kind = "image", window = window.Handle });
        }

        var text = GetStringArgument(arguments, "text")!;
        await WithRetryAsync(() => _s.Ui.Invoke(() =>
        {
            Clipboard.SetText(text);
            return true;
        }), ctx);
        return JsonResult(new { ok = true, kind = "text", length = text.Length, window = window.Handle });
    }

    private async Task<T> WithRetryAsync<T>(Func<T> action, ToolContext ctx)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException ex) when (attempt < 5 && ClipboardReadTool.IsBusy(ex))
            {
                await Task.Delay(60 * attempt, ctx.Cancel);
            }
            catch (ExternalException ex)
            {
                throw new ToolException("clipboard_failed", $"Clipboard is busy: {ex.Message}.",
                    "Retry in a moment; if it persists another application holds the clipboard open.");
            }
        }
    }
}
