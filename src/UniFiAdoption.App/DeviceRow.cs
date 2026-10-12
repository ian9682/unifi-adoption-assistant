using System.ComponentModel;
using System.Drawing.Drawing2D;
using UniFiAdoption.Core.Inventory;

namespace UniFiAdoption.App;

/// <summary>
/// One access point in the list: a tick box (only for access points that can be set up), the model
/// name, the MAC address with its last characters highlighted like the label, a coloured status pill
/// and one plain sentence. Everything technical stays out of sight.
/// </summary>
internal sealed class DeviceRow : Control
{
    private bool _hover;

    public DeviceRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        Font = Theme.Body;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        Margin = Padding.Empty;
    }

    public event EventHandler? Toggled;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeviceView? View { get; private set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; private set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selectable { get; private set; }

    public static string StatusLabel(DeviceStatus status) => status switch
    {
        DeviceStatus.Ready => "Ready",
        DeviceStatus.Working => "Setting up",
        DeviceStatus.WaitingForApproval => "Waiting for approval",
        DeviceStatus.Adopted => "Adopted",
        DeviceStatus.NeedsAttention => "Needs a look",
        DeviceStatus.NotResponding => "Not responding",
        _ => "Checking",
    };

    public void Bind(DeviceView view, bool selected, bool selectable)
    {
        View = view;
        Selected = selected;
        Selectable = selectable;
        Cursor = selectable ? Cursors.Hand : Cursors.Default;
        Height = Theme.RowTitle.Height + (Font.Height * 2) + (Font.Height * 3 / 2);
        AccessibleName = $"{view.FriendlyModel}, MAC {view.Mac}, {StatusLabel(view.Status)}. {view.Reason}" +
                         (selectable ? (selected ? " Selected." : " Not selected.") : string.Empty);
        AccessibleDefaultActionDescription = selectable ? "Select or clear" : null;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        if (Selectable)
        {
            Toggled?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && Selectable)
        {
            Toggled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_hover && Selectable ? Theme.RowHover : Color.White);
        using (var line = new Pen(Theme.Line))
        {
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

        if (View is not { } view)
        {
            return;
        }

        var unit = Font.Height;
        var pad = unit * 3 / 4;
        var x = pad;

        // Tick box.
        var box = unit;
        var boxRect = new Rectangle(x, (Height - box) / 2, box, box);
        if (Selectable)
        {
            using var path = Shapes.Rounded(boxRect, unit / 5f);
            if (Selected)
            {
                using var fill = new SolidBrush(Theme.Brand);
                g.FillPath(fill, path);
                using var tick = new Pen(Color.White, Math.Max(2f, unit / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(boxRect.Left + (box * 0.24f), boxRect.Top + (box * 0.52f)),
                    new PointF(boxRect.Left + (box * 0.43f), boxRect.Top + (box * 0.72f)),
                    new PointF(boxRect.Left + (box * 0.78f), boxRect.Top + (box * 0.30f)),
                });
            }
            else
            {
                using var border = new Pen(Theme.Muted, 1.5f);
                g.DrawPath(border, path);
            }
        }

        x += box + pad;

        // Small access point icon: a disc with a ring and a status light.
        var icon = unit * 2;
        var iconRect = new Rectangle(x, (Height - icon) / 2, icon, icon);
        using (var disc = new SolidBrush(Color.FromArgb(0xF1, 0xF4, 0xF2)))
        using (var rim = new Pen(Theme.BrandLine, 1.5f))
        using (var light = new SolidBrush(LightColor(view.Status)))
        {
            g.FillEllipse(disc, iconRect);
            g.DrawEllipse(rim, iconRect);
            var dot = icon / 4;
            g.FillEllipse(light, iconRect.Left + ((icon - dot) / 2), iconRect.Top + ((icon - dot) / 2), dot, dot);
        }

        x += icon + pad;

        // Status pill on the right.
        var label = StatusLabel(view.Status);
        var (pillBack, pillFore) = Theme.ForStatus(view.Status);
        var labelSize = TextRenderer.MeasureText(g, label, Theme.Pill, Size.Empty, TextFormatFlags.NoPadding);
        var pill = new Rectangle(Width - pad - labelSize.Width - unit, (Height - (labelSize.Height + (unit / 2))) / 2, labelSize.Width + unit, labelSize.Height + (unit / 2));
        using (var path = Shapes.Rounded(pill, pill.Height / 2f))
        using (var fill = new SolidBrush(pillBack))
        {
            g.FillPath(fill, path);
        }

        TextRenderer.DrawText(g, label, Theme.Pill, pill, pillFore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        // Text block.
        var textWidth = Math.Max(40, pill.Left - pad - x);
        var y = pad;
        var titleHeight = Theme.RowTitle.Height;
        TextRenderer.DrawText(g, view.FriendlyModel, Theme.RowTitle, new Rectangle(x, y, textWidth, titleHeight), Theme.Ink,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        y += titleHeight + (unit / 6);

        // MAC address with the last 4 characters highlighted, as on the label.
        var mac = view.Mac.ToString();
        var head = mac.Substring(0, mac.Length - 5);
        var tail = mac.Substring(mac.Length - 5);
        const TextFormatFlags Flat = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var headSize = TextRenderer.MeasureText(g, head, Theme.RowMac, Size.Empty, Flat);
        var tailSize = TextRenderer.MeasureText(g, tail, Theme.RowMac, Size.Empty, Flat);
        TextRenderer.DrawText(g, head, Theme.RowMac, new Point(x, y), Theme.Muted, Flat);
        var mark = new Rectangle(x + headSize.Width, y - 1, tailSize.Width + 4, tailSize.Height + 2);
        using (var marker = new SolidBrush(Theme.Marker))
        {
            g.FillRectangle(marker, mark);
        }

        TextRenderer.DrawText(g, tail, Theme.RowMac, new Point(mark.Left + 2, y), Theme.Ink, Flat);
        if (view.Address is { } address)
        {
            TextRenderer.DrawText(g, "    " + address, Font, new Rectangle(mark.Right, y, Math.Max(0, x + textWidth - mark.Right), unit), Theme.Muted,
                Flat | TextFormatFlags.EndEllipsis);
        }

        y += unit + (unit / 6);
        TextRenderer.DrawText(g, view.Reason, Font, new Rectangle(x, y, textWidth, unit), ReasonColor(view.Status),
            Flat | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Marker, 2f);
            g.DrawRectangle(focus, 1, 1, Width - 3, Height - 3);
        }
    }

    private static Color LightColor(DeviceStatus status) => status switch
    {
        DeviceStatus.Ready => Theme.Brand,
        DeviceStatus.Working => Theme.Brand,
        DeviceStatus.WaitingForApproval => Color.FromArgb(0xE0, 0xA1, 0x1B),
        DeviceStatus.Adopted => Color.FromArgb(0x2E, 0x9E, 0x5B),
        DeviceStatus.NeedsAttention => Theme.ErrorText,
        _ => Theme.Disabled,
    };

    private static Color ReasonColor(DeviceStatus status) =>
        status == DeviceStatus.NeedsAttention ? Theme.ErrorText : Theme.Muted;
}
