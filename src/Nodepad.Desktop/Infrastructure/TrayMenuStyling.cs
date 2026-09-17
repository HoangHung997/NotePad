using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Nodepad.Desktop.Infrastructure;

internal sealed class RoundedTrayContextMenuStrip : Forms.ContextMenuStrip
{
    private const int CornerRadius = 14;

    protected override void OnOpening(CancelEventArgs e)
    {
        base.OnOpening(e);
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 1 || Height <= 1)
        {
            return;
        }

        using var path = TrayMenuRenderer.CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region = new Region(path);
    }
}

internal sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    internal static readonly Color MenuBackgroundColor = ColorTranslator.FromHtml("#FFFFFBF7");
    internal static readonly Color MenuBorderColor = ColorTranslator.FromHtml("#FFD9C7B5");
    internal static readonly Color MenuHoverColor = ColorTranslator.FromHtml("#FFF2E8DE");
    internal static readonly Color MenuTextColor = ColorTranslator.FromHtml("#FF241A14");
    internal static readonly Color MenuMutedColor = ColorTranslator.FromHtml("#FF6D5A4F");
    internal static readonly Color MenuSeparatorColor = ColorTranslator.FromHtml("#FFE9DED4");

    public TrayMenuRenderer()
        : base(new TrayMenuColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var backgroundBrush = new SolidBrush(MenuBackgroundColor);
        using var path = CreateRoundedRectanglePath(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 14);
        e.Graphics.FillPath(backgroundBrush, path);
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var backgroundBrush = new SolidBrush(MenuBackgroundColor);
        e.Graphics.FillRectangle(backgroundBrush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = CreateRoundedRectanglePath(bounds, 14);
        using var borderPen = new Pen(MenuBorderColor, 1f);
        e.Graphics.DrawPath(borderPen, path);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var backgroundColor = e.Item.Selected ? MenuHoverColor : MenuBackgroundColor;
        using var backgroundBrush = new SolidBrush(backgroundColor);

        var bounds = new Rectangle(6, 2, e.Item.Width - 12, e.Item.Height - 4);
        using var path = CreateRoundedRectanglePath(bounds, 10);
        e.Graphics.FillPath(backgroundBrush, path);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Bounds.Top + (e.Item.Bounds.Height / 2);
        using var pen = new Pen(MenuSeparatorColor, 1f);
        e.Graphics.DrawLine(pen, 14, y, e.Item.Width - 14, y);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? MenuTextColor : Color.FromArgb(130, MenuTextColor);
        e.TextFont = new Font("Segoe UI", 10f, FontStyle.Regular);
        base.OnRenderItemText(e);
    }

    internal static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class TrayMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBackgroundColor;
        public override Color ImageMarginGradientBegin => MenuBackgroundColor;
        public override Color ImageMarginGradientMiddle => MenuBackgroundColor;
        public override Color ImageMarginGradientEnd => MenuBackgroundColor;
        public override Color MenuBorder => MenuBorderColor;
        public override Color MenuItemBorder => MenuHoverColor;
        public override Color MenuItemSelected => MenuHoverColor;
        public override Color MenuItemSelectedGradientBegin => MenuHoverColor;
        public override Color MenuItemSelectedGradientEnd => MenuHoverColor;
        public override Color MenuStripGradientBegin => MenuBackgroundColor;
        public override Color MenuStripGradientEnd => MenuBackgroundColor;
        public override Color SeparatorDark => MenuSeparatorColor;
        public override Color SeparatorLight => MenuSeparatorColor;
    }
}

internal static class TrayMenuIconFactory
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap Create(string glyph, Color color)
    {
        var key = $"{glyph}|{color.ToArgb()}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = new Bitmap(18, 18);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var font = CreateIconFont(13f);
        using var brush = new SolidBrush(color);
        var textBounds = new RectangleF(0, 0, bitmap.Width, bitmap.Height);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(glyph, font, brush, textBounds, format);

        Cache[key] = bitmap;
        return bitmap;
    }

    private static Font CreateIconFont(float size)
    {
        if (FontFamily.Families.Any(family => family.Name.Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase)))
        {
            return new Font("Segoe Fluent Icons", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        if (FontFamily.Families.Any(family => family.Name.Equals("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase)))
        {
            return new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        return new Font("Segoe UI Symbol", size, FontStyle.Regular, GraphicsUnit.Pixel);
    }
}
