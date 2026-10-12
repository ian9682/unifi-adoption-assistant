using System.Diagnostics;
using System.Text;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Inventory;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

/// <summary>
/// Version 2 main screen: the list fills itself in, access points are grouped in plain language,
/// ready ones are ticked automatically, and one button sets up everything that is ticked.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly SettingsLoadResult _load;
    private readonly AssistantSettings _settings;
    private readonly AssistantHost _host;
    private readonly ICredentialStore _credentials;
    private readonly IHostKeyStore _hostKeys;
    private readonly DiagnosticLog _log;

    private readonly BrandBar _brandBar = new();
    private readonly HeroPanel _hero = new();
    private readonly Panel _scroller = new();
    private readonly TableLayoutPanel _root = new();
    private readonly Label _statusDot = new();
    private readonly Label _statusText;
    private readonly Label _controllerText;
    private readonly Label _testBanner;
    private readonly Label _settingsBanner;
    private readonly TableLayoutPanel _findCard = new();
    private readonly AccessPointIllustration _illustration = new();
    private readonly TextBox _findBox = new();
    private readonly Label _findHint;
    private readonly TableLayoutPanel _list = new();
    private readonly TableLayoutPanel _summary = new();
    private readonly Label _summaryTitle;
    private readonly Label _summaryText;
    private readonly PrimaryButton _handOffButton = new();
    private readonly PrimaryButton _retryButton = new();
    private readonly LinkLabel _saveReportLink = new();
    private readonly Label _helpText;
    private readonly Label _footerNote;
    private readonly LinkLabel _adminLink = new();
    private readonly BrandBar _actionBar = new();
    private readonly Label _selectionText = new();
    private readonly PrimaryButton _runButton = new();
    private readonly PrimaryButton _stopButton = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 400 };

    private readonly List<Label> _fullWidthLabels = new();
    private readonly List<Label> _heroLabels = new();
    private readonly List<Label> _findLabels = new();
    private readonly List<Label> _summaryLabels = new();
    private readonly Dictionary<MacAddress, DeviceRow> _rows = new();
    private readonly HashSet<MacAddress> _selected = new();
    private readonly HashSet<MacAddress> _offeredForSelection = new();

    private SshCredential? _sessionCredential;
    private bool _testMode;
    private bool _showAdopted;
    private int _shownVersion = -1;
    private string _shownFilter = string.Empty;
    private CancellationTokenSource? _batchCts;
    private List<MacAddress>? _batchMacs;
    private BatchResult? _lastBatch;
    private bool _lastBatchWasTest;
    private Color _summaryAccent = Theme.Brand;

    /// <summary>Test seam: answers the confirmation automatically in UI checks. Null in the real app.</summary>
    internal static Func<string, bool>? ConfirmOverride { get; set; }

    public MainForm(SettingsLoadResult load, AssistantHost host, ICredentialStore credentials, IHostKeyStore hostKeys, bool forceTestMode)
    {
        _load = load;
        _settings = load.Settings;
        _host = host;
        _credentials = credentials;
        _hostKeys = hostKeys;
        _log = host.Log;
        _testMode = _settings.TestMode || forceTestMode;

        _statusText = Wrap(_fullWidthLabels, "Starting…", Theme.Body, Theme.Ink, Padding.Empty);
        _controllerText = new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 3, 0, 0), UseMnemonic = false };
        _testBanner = Wrap(_fullWidthLabels, "Test mode is on. Access points are checked, but nothing is changed.", Theme.Body, Color.FromArgb(0x5C, 0x44, 0x00), new Padding(0, 0, 0, 12));
        _settingsBanner = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.ErrorText, new Padding(0, 0, 0, 12));
        _findHint = Wrap(_findLabels, "Type the last 4 characters printed after “MAC” on the label.", Theme.Small, Theme.Muted, new Padding(0, 6, 0, 0));
        _summaryTitle = Wrap(_summaryLabels, string.Empty, Theme.ResultTitle, Theme.Ink, new Padding(0, 0, 0, 6));
        _summaryText = Wrap(_summaryLabels, string.Empty, Theme.Body, Theme.Ink, new Padding(0, 0, 0, 10));
        _helpText = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.Ink, Padding.Empty);
        _footerNote = Wrap(_fullWidthLabels, string.Empty, Theme.Small, Theme.Muted, new Padding(0, 4, 0, 0));

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"UniFi Adoption Assistant – {Theme.Organization} {Theme.Team}";
        Font = Theme.Body;
        BackColor = Color.White;
        ForeColor = Theme.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 860);
        MinimumSize = new Size(760, 600);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Default icon is fine.
        }

        BuildLayout();
        LoadDevicePhoto();
        ResumeLayout(performLayout: true);

        _uiTimer.Tick += (_, _) => RefreshView();
        _log.Info($"Assistant {AdoptionReport.AssistantVersion} started. Settings: {_load.SourcePath ?? "built-in defaults"}.");
        foreach (var problem in _load.Problems)
        {
            _log.Error($"Settings problem: {problem}");
        }

        foreach (var warning in _load.Warnings)
        {
            _log.Warn($"Settings: {warning}");
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            Height = area.Height;
            Top = area.Top;
        }

        UpdateBanners();
        UpdateWrapWidths();
        _host.Monitor.Start();
        _uiTimer.Start();
        RefreshView();
        _findBox.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_batchCts is not null)
        {
            var answer = MessageBox.Show(this, "Access points are still being set up. Stop and close the assistant?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _batchCts.Cancel();
        }

        _uiTimer.Stop();
        base.OnFormClosing(e);
    }

    // ----------------------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        _scroller.Dock = DockStyle.Fill;
        _scroller.AutoScroll = true;
        _scroller.Padding = new Padding(32, 20, 32, 20);
        _scroller.BackColor = Color.White;
        _scroller.Resize += (_, _) => UpdateWrapWidths();

        _root.Dock = DockStyle.Top;
        _root.AutoSize = true;
        _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _root.ColumnCount = 1;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.BackColor = Color.White;

        _root.Controls.Add(BuildStatusStrip());

        _testBanner.BackColor = Color.FromArgb(0xFF, 0xF1, 0xB8);
        _testBanner.Padding = new Padding(12, 9, 12, 9);
        _root.Controls.Add(_testBanner);
        _settingsBanner.BackColor = Color.FromArgb(0xFD, 0xEC, 0xEA);
        _settingsBanner.Padding = new Padding(12, 9, 12, 9);
        _root.Controls.Add(_settingsBanner);

        _root.Controls.Add(BuildFindCard());
        _root.Controls.Add(BuildSummary());

        _list.ColumnCount = 1;
        _list.AutoSize = true;
        _list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _list.Dock = DockStyle.Fill;
        _list.Margin = new Padding(0, 4, 0, 16);
        _list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.Controls.Add(_list);

        _root.Controls.Add(BuildHelp());
        _root.Controls.Add(BuildFooter());
        _root.Controls.Add(_footerNote);
        _scroller.Controls.Add(_root);

        // The control added last takes the outermost edge: brand bar on top, then the hero band,
        // the action bar at the bottom, and the scrolling list fills the rest.
        Controls.Add(_scroller);
        Controls.Add(BuildActionBar());
        Controls.Add(BuildHero());
        Controls.Add(BuildBrandBar());
    }

    private BrandBar BuildBrandBar()
    {
        _brandBar.Dock = DockStyle.Top;
        _brandBar.AutoSize = true;
        _brandBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _brandBar.ColumnCount = 2;
        _brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _brandBar.Padding = new Padding(30, 12, 32, 12);

        Control logo = BrandAssets.Logo is { } image
            ? new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(200, 50), Margin = Padding.Empty, BackColor = Color.White, AccessibleName = Theme.Organization }
            : new Label { Text = Theme.Organization, Font = Theme.Title, ForeColor = Theme.Brand, AutoSize = true, Margin = Padding.Empty };
        _brandBar.Controls.Add(logo, 0, 0);

        var team = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = Padding.Empty, BackColor = Color.White };
        team.Controls.Add(new Label { Text = Theme.Team, Font = Theme.TeamName, ForeColor = Theme.Brand, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty, UseMnemonic = false });
        team.Controls.Add(new Label { Text = Theme.Motto, Font = Theme.MottoFont, ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0), UseMnemonic = false });
        _brandBar.Controls.Add(team, 1, 0);
        return _brandBar;
    }

    private HeroPanel BuildHero()
    {
        _hero.Dock = DockStyle.Top;
        _hero.AutoSize = true;
        _hero.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _hero.ColumnCount = 1;
        _hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _hero.Padding = new Padding(32, 18, 32, 20);
        var title = Wrap(_heroLabels, "Set up UniFi access points", Theme.Title, Theme.OnBrand, new Padding(0, 0, 0, 4));
        var subtitle = Wrap(_heroLabels,
            $"Access points on this network appear below by themselves. Tick the ones to set up and they will be connected to {Theme.Organization}'s central controller.",
            Theme.Body, Theme.OnBrandSubtle, Padding.Empty);
        title.BackColor = Color.Transparent;
        subtitle.BackColor = Color.Transparent;
        _hero.Controls.Add(title);
        _hero.Controls.Add(subtitle);
        return _hero;
    }

    private TableLayoutPanel BuildStatusStrip()
    {
        var strip = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _statusDot.Text = "●";
        _statusDot.AutoSize = true;
        _statusDot.ForeColor = Theme.Brand;
        _statusDot.Margin = new Padding(0, 0, 6, 0);
        strip.Controls.Add(_statusDot, 0, 0);
        var texts = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        texts.Controls.Add(_statusText);
        texts.Controls.Add(_controllerText);
        strip.Controls.Add(texts, 1, 0);
        var refresh = new LinkLabel { Text = "Search again", AutoSize = true, LinkColor = Theme.Brand, ActiveLinkColor = Theme.BrandDeep, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty };
        refresh.LinkClicked += (_, _) =>
        {
            _host.Monitor.Refresh();
            _statusText.Text = "Searching…";
        };
        strip.Controls.Add(refresh, 2, 0);
        return strip;
    }

    private TableLayoutPanel BuildFindCard()
    {
        _findCard.ColumnCount = 2;
        _findCard.AutoSize = true;
        _findCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _findCard.Dock = DockStyle.Fill;
        _findCard.BackColor = Theme.BrandTint;
        _findCard.Padding = new Padding(18, 14, 22, 14);
        _findCard.Margin = new Padding(0, 0, 0, 14);
        _findCard.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _findCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _illustration.Size = new Size(130, 140);
        _illustration.BackColor = Theme.BrandTint;
        _illustration.Margin = new Padding(0, 0, 20, 0);
        _findCard.Controls.Add(_illustration, 0, 0);

        var right = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = Theme.BrandTint, Anchor = AnchorStyles.Left };
        right.Controls.Add(Wrap(_findLabels, "Looking for a specific access point?", Theme.Heading, Theme.Ink, new Padding(0, 0, 0, 2)));
        right.Controls.Add(Wrap(_findLabels, "Enter the last 4 characters of the MAC address", Theme.Body, Theme.Muted, new Padding(0, 0, 0, 8)));
        _findBox.Font = Theme.FindInput;
        _findBox.Width = 150;
        _findBox.MaxLength = 17;
        _findBox.CharacterCasing = CharacterCasing.Upper;
        _findBox.TextAlign = HorizontalAlignment.Center;
        _findBox.BorderStyle = BorderStyle.FixedSingle;
        _findBox.Margin = Padding.Empty;
        _findBox.AccessibleName = "Last 4 characters of the MAC address";
        _findBox.TextChanged += (_, _) => OnFindChanged();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _host.Monitor.Refresh();
                OnFindChanged();
            }
        };
        right.Controls.Add(_findBox);
        right.Controls.Add(_findHint);
        _findCard.Controls.Add(right, 1, 0);
        return _findCard;
    }

    private TableLayoutPanel BuildSummary()
    {
        _summary.ColumnCount = 1;
        _summary.AutoSize = true;
        _summary.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _summary.Dock = DockStyle.Fill;
        _summary.Padding = new Padding(22, 16, 18, 16);
        _summary.Margin = new Padding(0, 0, 0, 16);
        _summary.Visible = false;
        _summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _summary.Controls.Add(_summaryTitle);
        _summary.Controls.Add(_summaryText);
        _summary.Paint += (_, e) =>
        {
            using var bar = new SolidBrush(_summaryAccent);
            e.Graphics.FillRectangle(bar, 0, 0, (int)Math.Round(5 * e.Graphics.DpiX / 96f), _summary.Height);
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        _handOffButton.Text = $"Copy message for the {Theme.Team}";
        _handOffButton.Subdued = true;
        _handOffButton.AutoSize = true;
        _handOffButton.Padding = new Padding(14, 7, 14, 7);
        _handOffButton.Margin = new Padding(0, 0, 10, 0);
        _handOffButton.Click += (_, _) => CopyHandOff();
        _retryButton.Text = "Try the failed ones again";
        _retryButton.Subdued = true;
        _retryButton.AutoSize = true;
        _retryButton.Padding = new Padding(14, 7, 14, 7);
        _retryButton.Margin = new Padding(0, 0, 10, 0);
        _retryButton.Click += (_, _) => RetryFailed();
        _saveReportLink.Text = "Save report";
        _saveReportLink.AutoSize = true;
        _saveReportLink.LinkColor = Theme.Brand;
        _saveReportLink.Margin = new Padding(4, 9, 0, 0);
        _saveReportLink.LinkClicked += (_, _) => SaveReport();
        buttons.Controls.Add(_handOffButton);
        buttons.Controls.Add(_retryButton);
        buttons.Controls.Add(_saveReportLink);
        _summary.Controls.Add(buttons);
        return _summary;
    }

    private TableLayoutPanel BuildHelp()
    {
        var help = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) };
        help.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var toggle = new LinkLabel { Text = "Troubleshooting ▸", AutoSize = true, Font = Theme.Heading, LinkColor = Theme.Brand, ActiveLinkColor = Theme.BrandDeep, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 0, 0, 6) };
        _helpText.Text = string.Join("\n",
            "•  Nothing in the list: check the access point has power (its light is on) and is connected to the same network as this computer. New access points take about 2 minutes to start.",
            "•  Not responding: check its power and cable, then click “Search again”.",
            "•  Needs a look: the sentence under the access point says what to do. Clicking “Search again” checks it again.",
            "•  The MAC address only uses 0 to 9 and A to F, so an O on the label is the number 0.",
            "•  Setting up again is safe. Only ticked access points are changed, and only after they have been checked.",
            $"•  Still stuck? Click “Copy support details” and send them to {_settings.SupportContact}.");
        _helpText.Visible = false;
        toggle.LinkClicked += (_, _) =>
        {
            _helpText.Visible = !_helpText.Visible;
            toggle.Text = _helpText.Visible ? "Troubleshooting ▾" : "Troubleshooting ▸";
        };
        help.Controls.Add(toggle);
        help.Controls.Add(_helpText);
        return help;
    }

    private FlowLayoutPanel BuildFooter()
    {
        var footer = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };

        LinkLabel Link(string text, Action action)
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Font = Theme.Body, LinkColor = Theme.Brand, ActiveLinkColor = Theme.BrandDeep, VisitedLinkColor = Theme.Brand, Margin = new Padding(0, 0, 24, 0) };
            link.LinkClicked += (_, _) => action();
            return link;
        }

        footer.Controls.Add(Link("Copy support details", CopySupportDetails));
        footer.Controls.Add(Link("Open log folder", OpenLogFolder));
        _adminLink.Text = "Admin settings";
        _adminLink.AutoSize = true;
        _adminLink.Font = Theme.Body;
        _adminLink.LinkColor = Theme.Brand;
        _adminLink.ActiveLinkColor = Theme.BrandDeep;
        _adminLink.Margin = new Padding(0, 0, 24, 0);
        _adminLink.LinkClicked += (_, _) => OpenAdminSettings();
        footer.Controls.Add(_adminLink);
        footer.Controls.Add(new Label { Text = $"{Theme.Organization} {Theme.Team}  |  Version {AdoptionReport.AssistantVersion}", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) });
        return footer;
    }

    private BrandBar BuildActionBar()
    {
        _actionBar.Dock = DockStyle.Bottom;
        _actionBar.AutoSize = true;
        _actionBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _actionBar.ColumnCount = 3;
        _actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _actionBar.Padding = new Padding(32, 12, 32, 12);
        _actionBar.TopLine = true;

        _selectionText.AutoSize = true;
        _selectionText.Anchor = AnchorStyles.Left;
        _selectionText.ForeColor = Theme.Muted;
        _selectionText.Margin = Padding.Empty;
        _actionBar.Controls.Add(_selectionText, 0, 0);

        _stopButton.Text = "Stop";
        _stopButton.Subdued = true;
        _stopButton.AutoSize = true;
        _stopButton.Padding = new Padding(16, 9, 16, 9);
        _stopButton.Margin = new Padding(0, 0, 10, 0);
        _stopButton.Visible = false;
        _stopButton.Anchor = AnchorStyles.Right;
        _stopButton.Click += (_, _) =>
        {
            _stopButton.Enabled = false;
            _batchCts?.Cancel();
        };
        _actionBar.Controls.Add(_stopButton, 1, 0);

        _runButton.Text = "Set up access points";
        _runButton.Font = Theme.Button;
        _runButton.AutoSize = true;
        _runButton.Padding = new Padding(24, 11, 24, 11);
        _runButton.Margin = Padding.Empty;
        _runButton.Anchor = AnchorStyles.Right;
        _runButton.Click += async (_, _) => await RunBatchAsync(SelectedReady());
        _actionBar.Controls.Add(_runButton, 2, 0);
        return _actionBar;
    }

    // ------------------------------------------------------------------------- live updates

    private void RefreshView()
    {
        var network = _host.Inventory.Network;
        UpdateStatus(network);

        var filter = MacSuffix.Clean(_findBox.Text);
        var version = _host.Inventory.Version;
        if (version == _shownVersion && filter == _shownFilter)
        {
            return;
        }

        _shownVersion = version;
        _shownFilter = filter;
        var views = _host.Inventory.Snapshot();

        // Tick newly ready access points once; respect a technician who unticks one.
        foreach (var view in views.Where(v => v.Status == DeviceStatus.Ready))
        {
            if (_offeredForSelection.Add(view.Mac))
            {
                _selected.Add(view.Mac);
            }
        }

        _selected.RemoveWhere(mac => views.FirstOrDefault(v => v.Mac == mac)?.Status != DeviceStatus.Ready);
        RebuildList(views, filter, network);
        UpdateFindHint(views, filter);
        UpdateSummary(views);
        UpdateActionBar();
    }

    private void UpdateStatus(NetworkSnapshot network)
    {
        string text;
        Color dot;
        if (!network.HasNetwork)
        {
            dot = Theme.ErrorText;
            text = network.SelfAssignedOnly
                ? "This computer is connected but didn't get a network address. Check the cable and that the router is on."
                : "This computer isn't connected to a network. Connect it to the same network as the access points.";
        }
        else if (network.SearchBlocked)
        {
            dot = Theme.ErrorText;
            text = $"Security settings on this computer are blocking the search. Contact {_settings.SupportContact}.";
        }
        else
        {
            dot = Theme.Brand;
            var where = network.AdapterName is null ? "this network" : $"{network.AdapterName} ({network.SubnetText})";
            text = network.Searching || network.LastSearchCompleted is null
                ? $"Searching {where}…"
                : $"Searching {where} automatically. Last checked {network.LastSearchCompleted.Value:HH:mm:ss}.";
        }

        _statusDot.ForeColor = dot;
        _statusText.Text = text;
        _controllerText.Text = network.Controller switch
        {
            null => "Checking the central controller…",
            { PortReachable: true } => "Central controller: reachable from this computer ✓",
            _ => "Central controller: not reachable from this computer. Check the site's internet connection.",
        };
        _controllerText.ForeColor = network.Controller is { PortReachable: false } ? Theme.ErrorText : Theme.Muted;
    }

    private void RebuildList(IReadOnlyList<DeviceView> views, string filter, NetworkSnapshot network)
    {
        var filtering = filter.Length >= 2;
        var shown = filtering ? views.Where(v => v.Mac.EndsWithHex(filter)).ToList() : views.ToList();

        _list.SuspendLayout();
        _list.Controls.Clear();

        void Section(string title, IList<DeviceView> items, bool collapsible = false)
        {
            if (items.Count == 0)
            {
                return;
            }

            var expanded = !collapsible || _showAdopted || filtering;
            var header = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 14, 0, 4) };
            header.Controls.Add(new Label { Text = $"{title} ({items.Count})", AutoSize = true, Font = Theme.Heading, ForeColor = Theme.Ink, Margin = Padding.Empty, UseMnemonic = false });
            if (collapsible && !filtering)
            {
                var toggle = new LinkLabel { Text = _showAdopted ? "Hide" : "Show", AutoSize = true, LinkColor = Theme.Brand, Margin = new Padding(12, 2, 0, 0) };
                toggle.LinkClicked += (_, _) =>
                {
                    _showAdopted = !_showAdopted;
                    _shownVersion = -1;
                    RefreshView();
                };
                header.Controls.Add(toggle);
            }

            _list.Controls.Add(header);
            if (!expanded)
            {
                return;
            }

            foreach (var view in items)
            {
                if (!_rows.TryGetValue(view.Mac, out var row))
                {
                    row = new DeviceRow { Anchor = AnchorStyles.Left | AnchorStyles.Right };
                    var mac = view.Mac;
                    row.Toggled += (_, _) => ToggleSelection(mac);
                    _rows[view.Mac] = row;
                }

                row.Bind(view, _selected.Contains(view.Mac), view.Status == DeviceStatus.Ready && _batchCts is null);
                _list.Controls.Add(row);
            }
        }

        Section("Setting up", shown.Where(v => v.Status == DeviceStatus.Working).ToList());
        Section("Ready to set up", shown.Where(v => v.Status == DeviceStatus.Ready).ToList());
        Section("Waiting for approval", shown.Where(v => v.Status == DeviceStatus.WaitingForApproval).ToList());
        Section("Needs a look", shown.Where(v => v.Status is DeviceStatus.NeedsAttention or DeviceStatus.NotResponding).ToList());
        Section("Checking", shown.Where(v => v.Status == DeviceStatus.Checking).ToList());
        Section("Already adopted", shown.Where(v => v.Status == DeviceStatus.Adopted).ToList(), collapsible: true);

        if (shown.Count == 0 && !filtering)
        {
            var empty = network.Searching || network.LastSearchCompleted is null
                ? "Searching for access points… They appear here as soon as they are found."
                : "No UniFi access points found on this network yet. Check that they have power and are connected to the same network as this computer. The list keeps searching by itself.";
            _list.Controls.Add(Wrap(_fullWidthLabels, empty, Theme.Body, Theme.Muted, new Padding(0, 16, 0, 0)));
        }

        _list.ResumeLayout(performLayout: true);
        UpdateWrapWidths();
    }

    private void UpdateFindHint(IReadOnlyList<DeviceView> views, string filter)
    {
        _illustration.Suffix = filter.Length <= 4 ? filter : filter.Substring(filter.Length - 4);
        if (filter.Length == 0)
        {
            _findHint.ForeColor = Theme.Muted;
            _findHint.Text = "Type the last 4 characters printed after “MAC” on the label.";
            return;
        }

        var validation = MacSuffix.Validate(filter.Length <= 4 ? filter : filter.Substring(filter.Length - 4));
        if (filter.Length >= 4 && !validation.IsValid)
        {
            _findHint.ForeColor = Theme.ErrorText;
            _findHint.Text = validation.Problem;
            return;
        }

        if (filter.Length < 4)
        {
            _findHint.ForeColor = Theme.Muted;
            _findHint.Text = "Keep typing…";
            return;
        }

        var matches = views.Where(v => v.Mac.EndsWithHex(filter)).ToList();
        _findHint.ForeColor = matches.Count == 0 ? Theme.ErrorText : Theme.Muted;
        _findHint.Text = matches.Count switch
        {
            0 => $"No access point ending in {Pretty(filter)} found yet. Check its power and cable. The list keeps searching; press Enter to search again now.",
            1 => $"Found it: {matches[0].FriendlyModel}, {DeviceRow.StatusLabel(matches[0].Status).ToLowerInvariant()}. Only this access point is shown below.",
            _ => $"{matches.Count} access points end in {Pretty(filter)}. Type 2 more characters from the label to tell them apart.",
        };
    }

    private void OnFindChanged()
    {
        _shownVersion = -1;
        RefreshView();
    }

    private void ToggleSelection(MacAddress mac)
    {
        if (!_selected.Remove(mac))
        {
            _selected.Add(mac);
        }

        _shownVersion = -1;
        RefreshView();
    }

    private List<MacAddress> SelectedReady() =>
        _host.Inventory.Snapshot().Where(v => v.Status == DeviceStatus.Ready && _selected.Contains(v.Mac)).Select(v => v.Mac).ToList();

    private void UpdateActionBar()
    {
        var count = SelectedReady().Count;
        var settingsOk = _load.Problems.Count == 0;
        if (_batchCts is not null && _batchMacs is not null)
        {
            var done = _batchMacs.Count(m => _host.Inventory.Get(m)?.Job is JobState.Succeeded or JobState.Failed or JobState.Skipped or JobState.Cancelled);
            _runButton.Text = $"Setting up… {done} of {_batchMacs.Count}";
            _runButton.Enabled = false;
            _selectionText.Text = "Please keep this window open until it finishes.";
            return;
        }

        _runButton.Enabled = count > 0 && settingsOk;
        _runButton.Text = count switch
        {
            0 => "Set up access points",
            1 => _testMode ? "Test 1 access point" : "Set up 1 access point",
            _ => _testMode ? $"Test {count} access points" : $"Set up {count} access points",
        };
        _selectionText.Text = !settingsOk
            ? $"Setup is turned off because of a settings problem. Contact {_settings.SupportContact}."
            : count == 0 ? "Tick the access points you want to set up." : $"{count} ticked.";
    }

    // ------------------------------------------------------------------------------- batches

    private async Task RunBatchAsync(List<MacAddress> macs)
    {
        if (macs.Count == 0 || _batchCts is not null)
        {
            return;
        }

        var views = macs.Select(m => _host.Inventory.Get(m)).Where(v => v is not null).Select(v => v!).ToList();
        var list = string.Join("\n", views.Take(12).Select(v => $"   •  {v.FriendlyModel}   {v.Mac}   ({v.Address})")) +
                   (views.Count > 12 ? $"\n   …and {views.Count - 12} more" : string.Empty);
        var question = _testMode
            ? $"Test {views.Count} access point(s)? Nothing will be changed.\n\n{list}"
            : $"Set up {views.Count} access point(s)?\n\n{list}\n\nEach one will be connected to {Theme.Organization}'s central controller.";
        var confirmed = ConfirmOverride?.Invoke(question) ?? MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        if (!confirmed)
        {
            return;
        }

        _batchCts = new CancellationTokenSource();
        _batchMacs = macs;
        _lastBatchWasTest = _testMode;
        _stopButton.Visible = true;
        _stopButton.Enabled = true;
        _adminLink.Enabled = false;
        _shownVersion = -1;
        RefreshView();
        try
        {
            _lastBatch = await _host.Batches.RunAsync(macs, _testMode, _sessionCredential, _batchCts.Token);
        }
        catch (Exception ex)
        {
            _log.Error("Batch failed outside the workflow.", ex);
            MessageBox.Show(this, $"Something unexpected went wrong: {ex.Message}\n\nPlease tell {_settings.SupportContact}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            _stopButton.Visible = false;
            _adminLink.Enabled = true;
            _selected.ExceptWith(macs);
            _shownVersion = -1;
            RefreshView();
            _scroller.ScrollControlIntoView(_summary);
        }
    }

    private void RetryFailed()
    {
        if (_batchMacs is null)
        {
            return;
        }

        var failed = _batchMacs.Where(m => _host.Inventory.Get(m)?.Job == JobState.Failed).ToList();
        foreach (var mac in failed)
        {
            _selected.Add(mac);
        }

        _host.Monitor.Refresh(); // re-check them first; they become ready again if nothing is wrong
        _summaryText.Text = "Checking them again. Ticked access points that are ready can then be set up.";
    }

    private void UpdateSummary(IReadOnlyList<DeviceView> views)
    {
        if (_batchMacs is null || _batchCts is not null || _lastBatch is null)
        {
            _summary.Visible = false;
            return;
        }

        var batch = _batchMacs.Select(m => views.FirstOrDefault(v => v.Mac == m)).Where(v => v is not null).Select(v => v!).ToList();
        var adopted = batch.Count(v => v.Status == DeviceStatus.Adopted);
        var waiting = batch.Count(v => v.Status == DeviceStatus.WaitingForApproval);
        var failed = batch.Where(v => v.Job == JobState.Failed).ToList();
        var stopped = batch.Count(v => v.Job == JobState.Cancelled);

        string title;
        ResultTone tone;
        var lines = new List<string>();
        if (_lastBatchWasTest)
        {
            tone = ResultTone.Info;
            title = "Test finished. Nothing was changed.";
            lines.Add($"{batch.Count(v => v.LastReport?.Outcome == AdoptionOutcome.DryRunCompleted)} of {batch.Count} could be set up.");
        }
        else if (batch.Count > 0 && adopted == batch.Count)
        {
            tone = ResultTone.Success;
            title = batch.Count == 1 ? "The access point is adopted" : $"All {batch.Count} access points are adopted";
            lines.Add("Nothing else needs to be done at this site.");
        }
        else
        {
            tone = failed.Count > 0 ? ResultTone.Problem : ResultTone.Waiting;
            title = waiting + adopted > 0
                ? $"Sent {waiting + adopted} of {batch.Count} to the central controller"
                : "The access points weren't set up";
            if (waiting > 0)
            {
                lines.Add($"{waiting} waiting for approval. Copy the message below and send it to {_settings.SupportContact}. This list updates by itself when they approve.");
            }

            if (adopted > 0)
            {
                lines.Add($"{adopted} adopted.");
            }

            if (stopped > 0)
            {
                lines.Add($"{stopped} not started because you clicked Stop.");
            }

            foreach (var item in failed)
            {
                lines.Add($"…{item.Mac.ToString().Substring(12)}: {item.LastReport?.Title ?? "didn't finish"}.");
            }
        }

        var (back, fore) = Theme.ForTone(tone);
        _summary.BackColor = back;
        _summaryAccent = fore;
        _summaryTitle.ForeColor = fore;
        _summaryTitle.Text = title;
        _summaryText.Text = string.Join("\n", lines);
        _handOffButton.Visible = waiting > 0 && !_lastBatchWasTest;
        _retryButton.Visible = failed.Count > 0;
        _summary.Visible = true;
        _summary.Invalidate();
    }

    private void CopyHandOff()
    {
        var waiting = _host.Inventory.Snapshot().Where(v => v.Status == DeviceStatus.WaitingForApproval).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        if (TrySetClipboard(HandOff.Compose(waiting, _settings.SupportContact)))
        {
            _footerNote.Text = $"Message copied. Paste it into Teams or an email to {_settings.SupportContact}.";
        }
    }

    private void SaveReport()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save report",
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"AP-setup-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"UniFi Adoption Assistant {AdoptionReport.AssistantVersion} – {Theme.Organization} {Theme.Team}");
        text.AppendLine($"Computer: {Environment.MachineName}   Saved: {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        foreach (var view in _host.Inventory.Snapshot().OrderBy(v => v.Status))
        {
            text.AppendLine($"{DeviceRow.StatusLabel(view.Status),-22} {view.FriendlyModel,-20} {view.Mac}  {view.Address}  {view.Reason}");
        }

        if (_lastBatch is not null)
        {
            text.AppendLine();
            text.AppendLine("Last setup run:");
            foreach (var item in _lastBatch.Items)
            {
                text.AppendLine();
                text.AppendLine(item.Report.ToSupportText(null));
            }
        }

        try
        {
            File.WriteAllText(dialog.FileName, text.ToString(), Encoding.UTF8);
            _footerNote.Text = $"Report saved to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The report could not be saved: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // --------------------------------------------------------------------------------- misc

    private void CopySupportDetails()
    {
        var text = new StringBuilder();
        text.AppendLine($"UniFi Adoption Assistant {AdoptionReport.AssistantVersion}");
        text.AppendLine($"Computer: {Environment.MachineName}   Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var network = _host.Inventory.Network;
        text.AppendLine($"Network: {network.AdapterName} {network.SubnetText}   Controller reachable: {network.Controller?.PortReachable.ToString() ?? "unknown"}");
        text.AppendLine();
        foreach (var view in _host.Inventory.Snapshot())
        {
            text.AppendLine($"{view.Mac}  {view.Address}  {view.Model ?? "?"}  {view.Firmware ?? "?"}  {view.Status}: {view.Reason}");
        }

        var log = _log.Snapshot();
        text.AppendLine();
        text.AppendLine("Recent log:");
        foreach (var line in log.Skip(Math.Max(0, log.Count - 150)))
        {
            text.AppendLine(line);
        }

        if (TrySetClipboard(text.ToString()))
        {
            _footerNote.Text = $"Details copied. Paste them into an email or chat message to {_settings.SupportContact}.";
        }
    }

    private bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _footerNote.Text = "The clipboard is busy. Please try again.";
            return false;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.DefaultDirectory);
            Process.Start(new ProcessStartInfo { FileName = DiagnosticLog.DefaultDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _footerNote.Text = $"The log folder is {DiagnosticLog.DefaultDirectory}";
        }
    }

    private void OpenAdminSettings()
    {
        using var dialog = new ItSettingsForm(_settings, _load.SourcePath, _credentials, _hostKeys, _sessionCredential, _testMode);
        dialog.ShowDialog(this);
        _sessionCredential = dialog.SessionCredential;
        _testMode = dialog.TestMode;
        _log.Info($"Admin settings closed. Test mode: {_testMode}. Session login set: {_sessionCredential is not null}.");
        UpdateBanners();
        UpdateActionBar();
    }

    private void UpdateBanners()
    {
        _testBanner.Visible = _testMode;
        _settingsBanner.Visible = _load.Problems.Count > 0;
        _settingsBanner.Text = $"This copy of the assistant has a settings problem, so it won't change any access point. Contact {_settings.SupportContact}.";
        UpdateWrapWidths();
    }

    private void LoadDevicePhoto()
    {
        if (string.IsNullOrWhiteSpace(_settings.DeviceImagePath))
        {
            return;
        }

        var path = Path.IsPathRooted(_settings.DeviceImagePath) ? _settings.DeviceImagePath! : Path.Combine(AppContext.BaseDirectory, _settings.DeviceImagePath!);
        try
        {
            _illustration.Photo = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Device photo '{path}' could not be loaded: {ex.Message}");
        }
    }

    private static string Pretty(string hex) =>
        string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2))) + (hex.Length % 2 == 1 ? ":" + hex.Substring(hex.Length - 1) : string.Empty);

    /// <summary>Sets each label's maximum width so long text wraps to the window width.</summary>
    private void UpdateWrapWidths()
    {
        var full = _scroller.ClientSize.Width - _scroller.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (full < 120)
        {
            return;
        }

        SetWidths(_heroLabels, (int)((ClientSize.Width - _hero.Padding.Horizontal) * 0.8));
        SetWidths(_fullWidthLabels, full);
        foreach (var banner in new[] { _testBanner, _settingsBanner })
        {
            banner.MinimumSize = new Size(banner.MaximumSize.Width, 0);
        }

        SetWidths(_findLabels, full - _findCard.Padding.Horizontal - _illustration.Width - _illustration.Margin.Horizontal);
        SetWidths(_summaryLabels, full - _summary.Padding.Horizontal);
        _statusText.MaximumSize = new Size(Math.Max(80, full - 160), 0);
    }

    private static void SetWidths(IEnumerable<Label> labels, int width)
    {
        foreach (var label in labels)
        {
            label.MaximumSize = new Size(Math.Max(80, width - label.Margin.Horizontal), 0);
        }
    }

    private static Label Wrap(List<Label> group, string text, Font font, Color color, Padding margin)
    {
        var label = new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = margin, UseMnemonic = false };
        group.Add(label);
        return label;
    }
}
