using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LidFlow.App.Diagnostics;

/// <summary>
/// Draws the tray icon at runtime instead of shipping a binary asset.
/// <para>
/// Keeps the repository free of opaque files and lets the icon follow the DPI it
/// is asked for. The shape is the effect in miniature: a panel with a bright
/// aperture across it.
/// </para>
/// </summary>
internal static class AppIcon
{
    public static Icon Create(int size = 32)
    {
        using Bitmap bitmap = new(size, size);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float inset = size * 0.10f;
            float radius = size * 0.18f;
            RectangleF panel = new(inset, inset * 1.4f, size - (inset * 2f), size - (inset * 2.8f));

            using GraphicsPath path = RoundedRectangle(panel, radius);
            using LinearGradientBrush body = new(
                panel,
                Color.FromArgb(255, 32, 34, 38),
                Color.FromArgb(255, 12, 13, 15),
                LinearGradientMode.Vertical);

            graphics.FillPath(body, path);

            using Pen rim = new(Color.FromArgb(120, 200, 205, 215), Math.Max(1f, size / 32f));
            graphics.DrawPath(rim, path);

            // The aperture: a bright band, off-centre, the way it looks part-way
            // through a close.
            RectangleF aperture = new(
                panel.Left + (panel.Width * 0.13f),
                panel.Top + (panel.Height * 0.34f),
                panel.Width * 0.74f,
                panel.Height * 0.24f);

            using LinearGradientBrush glow = new(
                aperture,
                Color.FromArgb(255, 236, 240, 248),
                Color.FromArgb(255, 150, 172, 205),
                LinearGradientMode.Vertical);

            graphics.FillRectangle(glow, aperture);
        }

        IntPtr handle = bitmap.GetHicon();

        try
        {
            // Clone, so the icon survives destroying the temporary HICON.
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            LidFlow.App.Interop.NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>
    /// First installed font from <paramref name="families"/>, falling back to the
    /// supplied generic. Constructing a <see cref="Font"/> from a family name that
    /// is not installed does not reliably fall back, and LidFlow has to run on
    /// Windows 10, where several Windows 11 font families are absent.
    /// </summary>
    public static Font FirstAvailableFont(float size, FontStyle style, params string[] families)
    {
        foreach (string family in families)
        {
            try
            {
                using FontFamily candidate = new(family);
                return new Font(candidate, size, style, GraphicsUnit.Point);
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }

        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        GraphicsPath path = new();

        if (diameter <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();

        return path;
    }
}
