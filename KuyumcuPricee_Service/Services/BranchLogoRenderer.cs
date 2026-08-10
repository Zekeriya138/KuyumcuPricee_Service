using System.Globalization;
using System.Text;
using SkiaSharp;

namespace KUYUMCU.Price_Service.Services;

/// <summary>Şube adından premium kuyumcu monogram logosu üretir (3D altın, siyah zemin).</summary>
internal static class BranchLogoRenderer
{
    private static readonly SKColor Black = SKColor.Parse("#050505");
    private static readonly SKColor BlackSoft = SKColor.Parse("#141414");
    private static readonly SKColor GoldDeep = SKColor.Parse("#6B4E12");
    private static readonly SKColor GoldMid = SKColor.Parse("#C9A227");
    private static readonly SKColor GoldBright = SKColor.Parse("#F4E4A6");
    private static readonly SKColor GoldHighlight = SKColor.Parse("#FFF8DC");
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static byte[] RenderPng(string branchName, int size = 2048)
    {
        var parsed = ParseBranchName(branchName);

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        var rect = SKRect.Create(0, 0, size, size);
        DrawLuxuryBackground(canvas, rect);
        DrawBorderGlow(canvas, rect);

        var monogramFace = ResolveSerifFace(true);
        var nameFace = ResolveSerifFace(false);

        var monogramSize = parsed.Initials.Length switch
        {
            1 => size * 0.36f,
            2 => size * 0.30f,
            _ => size * 0.24f
        };
        var monogramY = size * 0.36f;

        DrawMonogram(canvas, parsed.Initials, size * 0.5f, monogramY, monogramSize, monogramFace);

        var diamondY = monogramY + monogramSize * 0.42f;
        DrawFacetedDiamond(canvas, size * 0.5f, diamondY, size * 0.045f);

        var primaryY = diamondY + size * 0.09f;
        DrawMetallicCaption(canvas, parsed.PrimaryLine, size * 0.5f, primaryY, size * 0.085f, nameFace, letterSpacing: 0.08f);

        if (!string.IsNullOrWhiteSpace(parsed.SecondaryLine))
        {
            var secondaryY = primaryY + size * 0.105f;
            DrawSecondaryWithRules(canvas, parsed.SecondaryLine, size * 0.5f, secondaryY, size * 0.048f, nameFace);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static string ExtractInitials(string branchName) => ParseBranchName(branchName).Initials;

    private sealed record BranchNameParts(string Initials, string PrimaryLine, string SecondaryLine);

    private static BranchNameParts ParseBranchName(string branchName)
    {
        var name = (branchName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return new BranchNameParts("?", "?", "");

        var parts = name.Split([' ', '-', '_', '.', ',', '&', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0)
            return new BranchNameParts("?", "?", "");

        string initials;
        string primary;
        string secondary;

        if (parts.Length == 1)
        {
            var word = parts[0];
            initials = word.Length >= 2 ? word[..2].ToUpper(Tr) : word.ToUpper(Tr);
            primary = word.ToUpper(Tr);
            secondary = "";
        }
        else
        {
            initials = string.Concat(parts.Take(3).Select(p => char.ToUpper(p[0], Tr)));
            primary = parts[0].ToUpper(Tr);
            secondary = string.Join(' ', parts.Skip(1)).ToUpper(Tr);
        }

        return new BranchNameParts(initials, primary, secondary);
    }

    private static void DrawLuxuryBackground(SKCanvas canvas, SKRect rect)
    {
        using var radial = new SKPaint { IsAntialias = true };
        radial.Shader = SKShader.CreateRadialGradient(
            new SKPoint(rect.MidX, rect.MidY * 0.92f),
            rect.Width * 0.72f,
            new[] { BlackSoft, Black, SKColor.Parse("#000000") },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(rect, radial);

        using var vignette = new SKPaint { IsAntialias = true };
        vignette.Shader = SKShader.CreateRadialGradient(
            new SKPoint(rect.MidX, rect.MidY),
            rect.Width * 0.78f,
            new[] { SKColors.Transparent, SKColors.Black.WithAlpha(120) },
            new[] { 0.55f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(rect, vignette);
    }

    private static void DrawBorderGlow(SKCanvas canvas, SKRect rect)
    {
        var inset = rect.Width * 0.04f;
        var frame = SKRect.Inflate(rect, -inset, -inset);

        using var outer = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = rect.Width * 0.004f,
            Color = GoldMid.WithAlpha(90)
        };
        canvas.DrawRect(frame, outer);

        using var inner = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = rect.Width * 0.0018f,
            Color = GoldBright.WithAlpha(50)
        };
        var innerFrame = SKRect.Inflate(frame, -rect.Width * 0.012f, -rect.Width * 0.012f);
        canvas.DrawRect(innerFrame, inner);
    }

    private static void DrawMonogram(SKCanvas canvas, string initials, float cx, float cy, float fontSize, SKTypeface face)
    {
        if (initials.Length == 2)
        {
            var spacing = fontSize * 0.18f;
            DrawMetallicText(canvas, initials[0].ToString(), cx - spacing, cy, fontSize, face, isMonogram: true);
            DrawMetallicText(canvas, initials[1].ToString(), cx + spacing, cy, fontSize, face, isMonogram: true);
            return;
        }

        DrawMetallicText(canvas, initials, cx, cy, fontSize, face, isMonogram: true);
    }

    private static void DrawMetallicCaption(SKCanvas canvas, string text, float cx, float cy, float fontSize, SKTypeface face, float letterSpacing = 0f)
        => DrawMetallicText(canvas, text, cx, cy, fontSize, face, isMonogram: false, letterSpacing: letterSpacing);

    private static void DrawMetallicText(
        SKCanvas canvas,
        string text,
        float cx,
        float cy,
        float fontSize,
        SKTypeface face,
        bool isMonogram,
        float letterSpacing = 0f)
    {
        using var font = new SKFont(face, fontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
        font.Embolden = isMonogram;

        var width = MeasureTextWidth(font, text, letterSpacing);
        var metrics = font.Metrics;
        var x = cx - width / 2f;
        var y = cy - (metrics.Ascent + metrics.Descent) / 2f;
        var depth = fontSize * (isMonogram ? 0.018f : 0.010f);

        // Derin gölge
        using (var deepShadow = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha(210),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, fontSize * 0.025f)
        })
            DrawTextWithSpacing(canvas, text, x + depth * 2.2f, y + depth * 2.8f, font, deepShadow, letterSpacing);

        // Bronz taban (bevel)
        using (var baseLayer = new SKPaint { IsAntialias = true, Color = GoldDeep })
            DrawTextWithSpacing(canvas, text, x + depth, y + depth * 1.2f, font, baseLayer, letterSpacing);

        // Ana altın gradyan
        using (var goldFill = new SKPaint { IsAntialias = true })
        {
            goldFill.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, y + metrics.Ascent),
                new SKPoint(x, y + metrics.Descent),
                new[] { GoldBright, GoldMid, GoldDeep, SKColor.Parse("#3D2E08") },
                new[] { 0f, 0.35f, 0.72f, 1f },
                SKShaderTileMode.Clamp);
            DrawTextWithSpacing(canvas, text, x, y, font, goldFill, letterSpacing);
        }

        // Üst highlight
        using (var highlight = new SKPaint { IsAntialias = true, Color = GoldHighlight.WithAlpha((byte)(isMonogram ? 85 : 55)) })
            DrawTextWithSpacing(canvas, text, x - depth * 0.35f, y - depth * 0.55f, font, highlight, letterSpacing);

        // İnce kontur
        using (var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, fontSize * 0.012f),
            Color = GoldDeep.WithAlpha(160)
        })
            DrawTextWithSpacing(canvas, text, x, y, font, outline, letterSpacing);
    }

    private static float MeasureTextWidth(SKFont font, string text, float letterSpacing)
    {
        if (letterSpacing <= 0f || text.Length <= 1)
            return font.MeasureText(text);

        var spacing = letterSpacing * font.Size;
        var width = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            width += font.MeasureText(text[i].ToString());
            if (i < text.Length - 1) width += spacing;
        }
        return width;
    }

    private static void DrawTextWithSpacing(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint, float letterSpacing)
    {
        if (letterSpacing <= 0f || text.Length <= 1)
        {
            canvas.DrawText(text, x, y, font, paint);
            return;
        }

        var spacing = letterSpacing * font.Size;
        var cursor = x;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i].ToString();
            canvas.DrawText(ch, cursor, y, font, paint);
            cursor += font.MeasureText(ch) + spacing;
        }
    }

    private static void DrawSecondaryWithRules(SKCanvas canvas, string text, float cx, float cy, float fontSize, SKTypeface face)
    {
        using var font = new SKFont(face, fontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
        const float letterSpacing = 0.22f;

        var textWidth = MeasureTextWidth(font, text, letterSpacing);
        var metrics = font.Metrics;
        var x = cx - textWidth / 2f;
        var y = cy - (metrics.Ascent + metrics.Descent) / 2f;
        var gap = fontSize * 0.55f;
        var lineHalf = Math.Max(textWidth * 0.55f, fontSize * 2.2f);

        using var linePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, fontSize * 0.07f),
            Color = GoldMid.WithAlpha(200)
        };
        canvas.DrawLine(cx - lineHalf - gap, cy, cx - textWidth / 2f - gap * 0.35f, cy, linePaint);
        canvas.DrawLine(cx + textWidth / 2f + gap * 0.35f, cy, cx + lineHalf + gap, cy, linePaint);

        using var fill = new SKPaint { IsAntialias = true, Color = GoldBright.WithAlpha(230) };
        DrawTextWithSpacing(canvas, text, x, y, font, fill, letterSpacing);
    }

    private static void DrawFacetedDiamond(SKCanvas canvas, float cx, float cy, float size)
    {
        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha(140),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.35f)
        };
        using var shadowPath = BuildDiamondPath(cx, cy + size * 0.15f, size);
        canvas.DrawPath(shadowPath, shadow);

        using var body = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(cx, cy - size),
                new SKPoint(cx, cy + size),
                new[] { GoldHighlight, GoldBright, GoldMid, GoldDeep },
                new[] { 0f, 0.25f, 0.65f, 1f },
                SKShaderTileMode.Clamp)
        };
        using var path = BuildDiamondPath(cx, cy, size);
        canvas.DrawPath(path, body);

        using var facet = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.08f,
            Color = GoldHighlight.WithAlpha(180)
        };
        canvas.DrawLine(cx, cy - size, cx, cy + size * 0.55f, facet);
        canvas.DrawLine(cx - size * 0.62f, cy, cx + size * 0.62f, cy, facet);
        canvas.DrawLine(cx - size * 0.22f, cy - size * 0.35f, cx, cy, facet);
        canvas.DrawLine(cx + size * 0.22f, cy - size * 0.35f, cx, cy, facet);

        using var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.06f,
            Color = GoldDeep.WithAlpha(200)
        };
        canvas.DrawPath(path, outline);
    }

    private static SKPath BuildDiamondPath(float cx, float cy, float size)
    {
        var path = new SKPath();
        path.MoveTo(cx, cy - size);
        path.LineTo(cx + size * 0.62f, cy);
        path.LineTo(cx, cy + size * 0.58f);
        path.LineTo(cx - size * 0.62f, cy);
        path.Close();
        return path;
    }

    private static SKTypeface ResolveSerifFace(bool monogram)
    {
        var families = monogram
            ? new[] { "Times New Roman", "Georgia", "Palatino Linotype", "Garamond", "Cambria" }
            : new[] { "Georgia", "Times New Roman", "Palatino Linotype", "Cambria", "Garamond" };

        var style = monogram ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var family in families)
        {
            using var probe = SKTypeface.FromFamilyName(family, style);
            if (!string.Equals(probe.FamilyName, "serif", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(probe.FamilyName, "sans-serif", StringComparison.OrdinalIgnoreCase))
                return SKTypeface.FromFamilyName(family, style);
        }

        return SKTypeface.FromFamilyName("Times New Roman", SKFontStyle.Bold);
    }
}
