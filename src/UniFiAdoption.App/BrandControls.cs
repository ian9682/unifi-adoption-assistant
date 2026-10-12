using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace UniFiAdoption.App;

/// <summary>Images built into the exe (Assets\logo.png and Assets\leaf-white.png).</summary>
internal static class BrandAssets
{
    public static Image? Logo { get; } = Load("logo.png");

    public static Image? LeafWhite { get; } = Load("leaf-white.png");

    private static Image? Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        try
        {
            using var decoded = Image.FromStream(stream);
            return new Bitmap(decoded); // independent copy, so the stream can be closed
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

internal static class Shapes
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Max(1f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>The green band under the logo, with a large, faint leaf on the right.</summary>
internal sealed class HeroPanel : TableLayoutPanel
{
    public HeroPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Brand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var leaf = BrandAssets.LeafWhite;
        if (leaf is null || Height <= 0)
        {
            return;
        }

        var height = Height * 1.35f;
        var width = height * leaf.Width / leaf.Height;
        var bounds = new RectangleF(Width - (width * 0.82f), (Height - height) / 2 + (Height * 0.12f), width, height);

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.12f });
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(leaf, Rectangle.Round(bounds), 0, 0, leaf.Width, leaf.Height, GraphicsUnit.Pixel, attributes);
    }
}

/// <summary>White strip (logo bar or action bar); draws a hairline on its bottom or top edge.</summary>
internal sealed class BrandBar : TableLayoutPanel
{
    public BrandBar()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TopLine { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.BrandLine);
        var y = TopLine ? 0 : Height - 1;
        e.Graphics.DrawLine(pen, 0, y, Width, y);
    }
}

/// <summary>A numbered circle for the setup steps.</summary>
internal sealed class StepBadge : Control
{
    public StepBadge(int number)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Size = new Size(26, 26);
        Font = Theme.StepNumber;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = $"Step {Text}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.White);
        using var fill = new SolidBrush(Theme.Brand);
        g.FillEllipse(fill, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Theme.OnBrand,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>The main action button: rounded, brand green, with a visible keyboard focus ring.</summary>
internal sealed class PrimaryButton : Button
{
    private bool _hover;
    private bool _pressed;

    public PrimaryButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.OnBrand;
        Cursor = Cursors.Hand;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Subdued { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.White);

        Color fill, text;
        if (Subdued)
        {
            fill = _pressed ? Theme.BrandLine : Color.White;
            text = Theme.BrandDeep;
        }
        else
        {
            fill = !Enabled ? Theme.Disabled : _pressed ? Theme.BrandDeep : _hover ? Theme.BrandHover : Theme.Brand;
            text = Theme.OnBrand;
        }

        var bounds = new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f);
        var radius = Math.Min(10f, Height * 0.24f);
        using (var path = Shapes.Rounded(bounds, radius))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (Subdued)
            {
                using var border = new Pen(Theme.Brand, 1.2f);
                g.DrawPath(border, path);
            }
        }

        if (Focused && ShowFocusCues)
        {
            using var ring = Shapes.Rounded(RectangleF.Inflate(bounds, -3f, -3f), Math.Max(2f, radius - 3f));
            using var pen = new Pen(Theme.Marker, 2f);
            g.DrawPath(pen, ring);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
