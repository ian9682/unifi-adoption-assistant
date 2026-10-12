
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UniFiAdoption.App;

/// <summary>
/// A simple, original drawing of the back of a round ceiling access point with its label. The last
/// four characters of the MAC address are highlighted and replaced live by what the person types,
/// so they can see they are reading the right part of the label. IT can replace the drawing with a
/// photo of their own device (DeviceImagePath in settings).
/// </summary>
internal sealed class AccessPointIllustration : Control
{
    private string _suffix = string.Empty;
    private Image? _photo;

    public AccessPointIllustration()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint, true);

        BackColor = Color.White;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Drawing of the back of the access point. The MAC address is on the label; type its last 4 characters.";
    }

    /// <summary>The characters typed so far (shown in the highlight on the label).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Suffix
    {
        get => _suffix;
        set
        {
            _suffix = value ?? string.Empty;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Photo
    {
        get => _photo;
        set
        {
            _photo = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var width = ClientSize.Width;
        var height = ClientSize.Height;
        var captionHeight = Math.Max(18, height * 0.12f);

        if (_photo is not null)
        {
            DrawPhoto(g, new RectangleF(0, 0, width, height - captionHeight));
        }
        else
        {
            DrawDevice(g, width, height - captionHeight);
        }

        using var captionFont = new Font(
            "Segoe UI",
            Math.Max(7f, captionHeight * 0.42f),
            FontStyle.Regular,
            GraphicsUnit.Pixel);

        using var captionBrush = new SolidBrush(Theme.Muted);
        using var center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.DrawString(
            "Back of the access point",
            captionFont,
            captionBrush,
            new RectangleF(0, height - captionHeight, width, captionHeight),
            center);
    }

    private void DrawPhoto(Graphics g, RectangleF area)
    {
        var scale = Math.Min(
            area.Width / _photo!.Width,
            area.Height / _photo.Height);

        var size = new SizeF(
            _photo.Width * scale,
            _photo.Height * scale);

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        g.DrawImage(
            _photo,
            area.X + ((area.Width - size.Width) / 2),
            area.Y + ((area.Height - size.Height) / 2),
            size.Width,
            size.Height);
    }

    private void DrawDevice(Graphics g, float width, float height)
    {
        var diameter = Math.Min(width, height) - 16;

        var disc = new RectangleF(
            (width - diameter) / 2,
            (height - diameter) / 2,
            diameter,
            diameter);

        // Soft shadow, body, and the recessed mounting area.
        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
        {
            g.FillEllipse(
                shadow,
                disc.X + 3,
                disc.Y + 6,
                disc.Width,
                disc.Height);
        }

        using (var body = new LinearGradientBrush(
            disc,
            Color.FromArgb(0xF7, 0xF9, 0xFA),
            Color.FromArgb(0xE3, 0xE8, 0xEC),
            90f))
        {
            g.FillEllipse(body, disc);
        }

        using (var rim = new Pen(
            Color.FromArgb(0xC4, 0xCD, 0xD3),
            Math.Max(1.5f, diameter / 110f)))
        {
            g.DrawEllipse(rim, disc);

            var inner = RectangleF.Inflate(
                disc,
                -diameter * 0.09f,
                -diameter * 0.09f);

            rim.Color = Color.FromArgb(0xD6, 0xDE, 0xE3);
            g.DrawEllipse(rim, inner);
        }

        // The label.
        var labelWidth = diameter * 0.64f;
        var labelHeight = diameter * 0.36f;

        var label = new RectangleF(
            disc.X + ((diameter - labelWidth) / 2),
            disc.Y + ((diameter - labelHeight) / 2),
            labelWidth,
            labelHeight);

        using (var path = RoundedRectangle(label, labelHeight * 0.06f))
        using (var paper = new SolidBrush(Color.White))
        using (var edge = new Pen(Color.FromArgb(0xB8, 0xC1, 0xC8), 1.2f))
        {
            g.FillPath(paper, path);
            g.DrawPath(edge, path);
        }

        // A decorative barcode pattern.
        var barTop = label.Y + (labelHeight * 0.12f);
        var barHeight = labelHeight * 0.26f;
        var x = label.X + (labelWidth * 0.08f);
        var unit = labelWidth / 120f;

        int[] pattern =
        [
            2, 1, 1, 3, 2, 1, 3, 1, 1, 2, 2, 1,
            1, 3, 1, 2, 3, 1, 2, 1, 1, 2, 3, 1,
            1, 2, 1, 3, 2, 1, 1, 2, 2, 1, 3, 1
        ];

        using (var ink = new SolidBrush(Color.FromArgb(0x3A, 0x44, 0x4C)))
        {
            for (var i = 0;
                 i < pattern.Length && x < label.Right - (labelWidth * 0.08f);
                 i++)
            {
                var w = pattern[i] * unit;

                if (i % 2 == 0)
                {
                    g.FillRectangle(ink, x, barTop, w, barHeight);
                }

                x += w + unit;
            }
        }

        // MAC caption and highlighted final four characters.
        var textHeight = labelHeight * 0.2f;

        using var small = new Font(
            "Segoe UI", textHeight * 0.62f,
            FontStyle.Bold, GraphicsUnit.Pixel);

        using var mono = new Font(
            "Consolas", textHeight * 0.95f,
            FontStyle.Bold, GraphicsUnit.Pixel);

        using var mutedBrush = new SolidBrush(Color.FromArgb(0x8A, 0x95, 0x9D));
        using var inkBrush = new SolidBrush(Theme.Ink);

        var macCaptionY = barTop + barHeight + (labelHeight * 0.07f);

        g.DrawString(
            "MAC", small, mutedBrush,
            label.X + (labelWidth * 0.08f),
            macCaptionY);

        var lineY = macCaptionY + (textHeight * 0.85f);

        const string Hidden = "XXXXXXXX";
        var shown = BuildSuffixText();

        var hiddenSize = g.MeasureString(
            Hidden, mono, PointF.Empty, StringFormat.GenericTypographic);

        var shownSize = g.MeasureString(
            "WWWW", mono, PointF.Empty, StringFormat.GenericTypographic);

        var lineX = label.X +
            ((labelWidth - hiddenSize.Width - shownSize.Width - 6) / 2);

        g.DrawString(
            Hidden, mono, mutedBrush,
            lineX, lineY, StringFormat.GenericTypographic);

        var highlight = new RectangleF(
            lineX + hiddenSize.Width + 2,
            lineY - 2,
            shownSize.Width + 6,
            shownSize.Height + 4);

        using (var marker = new SolidBrush(Theme.Marker))
        {
            g.FillRectangle(marker, highlight);
        }

        g.DrawString(
            shown, mono, inkBrush,
            highlight.X + 3, lineY,
            StringFormat.GenericTypographic);
    }

    private string BuildSuffixText()
    {
        var typed = new string(
            _suffix.Where(char.IsLetterOrDigit).Take(4).ToArray())
            .ToUpperInvariant();

        return typed.PadRight(4, '_');
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);

        path.CloseFigure();
        return path;
    }
}
