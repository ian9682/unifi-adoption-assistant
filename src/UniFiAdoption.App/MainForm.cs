using System.Diagnostics;
using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Discovery;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Networking;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

internal sealed class MainForm : Form
{
    private const int BadgeColumn = 40; // step badge (26) plus its right margin

    private readonly SettingsLoadResult _load;
    private readonly AssistantSettings _settings;
    private readonly DiagnosticLog _log = DiagnosticLog.CreateDefault();
    private readonly JsonHostKeyStore _hostKeys;
    private readonly DpapiCredentialStore _credentials = new();

    private readonly BrandBar _brandBar = new();
    private readonly HeroPanel _hero = new();
    private readonly Panel _scroller = new();
    private readonly TableLayoutPanel _root = new();
    private readonly TableLayoutPanel _card = new();
    private readonly AccessPointIllustration _illustration = new();
    private readonly TextBox _suffixBox = new();
    private readonly Label _inputHint;
    private readonly PrimaryButton _runButton = new();
    private readonly PrimaryButton _stopButton = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _statusLabel;
    private readonly Label _testBanner;
    private readonly Label _settingsBanner;
    private readonly TableLayoutPanel _resultPanel = new();
    private readonly Label _resultTitle;
    private readonly Label _resultMessage;
    private readonly Label _resultSteps;
    private readonly Label _resultDetails;
    private readonly Label _footerNote;
    private readonly LinkLabel _itSettingsLink = new();

    // Labels whose wrap width follows the window width, grouped by where they sit.
    private readonly List<Label> _fullWidthLabels = [];
    private readonly List<Label> _heroLabels = [];
    private readonly List<Label> _stepLabels = [];
    private readonly List<Label> _cardLabels = [];
    private readonly List<Label> _resultLabels = [];

    private SshCredential? _sessionCredential;
    private bool _testMode;
    private CancellationTokenSource? _cts;
    private AdoptionReport? _lastReport;
    private Color _resultAccent = Theme.Brand;

    public MainForm(SettingsLoadResult load, bool forceTestMode)
    {
        _load = load;
        _settings = load.Settings;
        _testMode = _settings.TestMode || forceTestMode;
        _hostKeys = new JsonHostKeyStore(JsonHostKeyStore.DefaultPath, _settings.PinnedHostKeys);

        _inputHint = Wrap(_cardLabels, "Example: if the label says 802AA812ABCD, type ABCD.", Theme.Small, Theme.Muted, new Padding(0, 6, 0, 16));
        _statusLabel = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.Muted, new Padding(0, 2, 0, 8));
        _testBanner = Wrap(_fullWidthLabels, "Test mode is on. The assistant will find and check the access point but will not change anything.", Theme.Body, Color.FromArgb(0x5C, 0x44, 0x00), new Padding(0, 0, 0, 14));
        _settingsBanner = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.ErrorText, new Padding(0, 0, 0, 14));
        _resultTitle = Wrap(_resultLabels, string.Empty, Theme.ResultTitle, Theme.Ink, new Padding(0, 0, 0, 6));
        _resultMessage = Wrap(_resultLabels, string.Empty, Theme.Body, Theme.Ink, new Padding(0, 0, 0, 6));
        _resultSteps = Wrap(_resultLabels, string.Empty, Theme.Body, Theme.Ink, new Padding(0, 2, 0, 4));
        _resultDetails = Wrap(_resultLabels, string.Empty, Theme.Mono, Theme.Muted, new Padding(0, 6, 0, 0));
        _footerNote = Wrap(_fullWidthLabels, string.Empty, Theme.Small, Theme.Muted, new Padding(0, 4, 0, 0));

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"UniFi Adoption Assistant – {Theme.Organization} {Theme.Team}";
        Font = Theme.Body;
        BackColor = Color.White;
        ForeColor = Theme.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 820);
        MinimumSize = new Size(720, 540);
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

        _log.Info($"Assistant {AdoptionReport.AssistantVersion} started. Settings: {_load.SourcePath ?? "built-in defaults"}.");
        foreach (var problem in _load.Problems)
        {
            _log.Error($"Settings problem: {problem}");
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Fit smaller screens (for example 1366x768 laptops at 125% scaling).
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            Height = area.Height;
            Top = area.Top;
        }

        UpdateWrapWidths();
        UpdateBanners();
        _suffixBox.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts is not null)
        {
            var answer = MessageBox.Show(this, "The setup is still running. Stop it and close the assistant?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cts.Cancel();
        }

        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        _scroller.Dock = DockStyle.Fill;
        _scroller.AutoScroll = true;
        _scroller.Padding = new Padding(32, 24, 32, 20);
        _scroller.BackColor = Color.White;
        _scroller.Resize += (_, _) => UpdateWrapWidths(); // also fires when the window moves to a screen with different scaling

        _root.Dock = DockStyle.Top;
        _root.AutoSize = true;
        _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _root.ColumnCount = 1;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.BackColor = Color.White;

        _testBanner.BackColor = Color.FromArgb(0xFF, 0xF1, 0xB8);
        _testBanner.Padding = new Padding(12, 9, 12, 9);
        _root.Controls.Add(_testBanner);

        _settingsBanner.BackColor = Color.FromArgb(0xFD, 0xEC, 0xEA);
        _settingsBanner.Padding = new Padding(12, 9, 12, 9);
        _root.Controls.Add(_settingsBanner);

        _root.Controls.Add(BuildMainRow());

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Height = 10;
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 4, 0, 4);
        _progress.Visible = false;
        _root.Controls.Add(_progress);
        _statusLabel.Visible = false;
        _root.Controls.Add(_statusLabel);

        _root.Controls.Add(BuildResultPanel());
        _root.Controls.Add(BuildHelpSection());
        _root.Controls.Add(BuildFooter());
        _root.Controls.Add(_footerNote);
        _scroller.Controls.Add(_root);

        // Docking order: the control added last takes the outermost edge, so the brand bar sits on
        // top, the green hero band under it, and the scrolling content fills the rest.
        Controls.Add(_scroller);
        Controls.Add(BuildHero());
        Controls.Add(BuildBrandBar());
        AcceptButton = _runButton;
    }

    private BrandBar BuildBrandBar()
    {
        _brandBar.Dock = DockStyle.Top;
        _brandBar.AutoSize = true;
        _brandBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _brandBar.ColumnCount = 2;
        _brandBar.RowCount = 1;
        _brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _brandBar.Padding = new Padding(30, 12, 32, 12);

        Control logo = BrandAssets.Logo is { } image
            ? new PictureBox
            {
                Image = image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(200, 50),
                Margin = Padding.Empty,
                BackColor = Color.White,
                AccessibleName = Theme.Organization,
            }
            : new Label { Text = Theme.Organization, Font = Theme.Title, ForeColor = Theme.Brand, AutoSize = true, Margin = Padding.Empty };
        _brandBar.Controls.Add(logo, 0, 0);

        var team = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            BackColor = Color.White,
        };
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
        _hero.Padding = new Padding(32, 22, 32, 24);

        var title = Wrap(_heroLabels, "Connect a UniFi access point", Theme.Title, Theme.OnBrand, new Padding(0, 0, 0, 6));
        var subtitle = Wrap(_heroLabels,
            $"Points the access point at your site to {Theme.Organization}'s centralized UniFi controller, so the {Theme.Team.Replace(" ", "\u00A0")} can manage it remotely.",
            Theme.Body, Theme.OnBrandSubtle, new Padding(0));
        title.BackColor = Color.Transparent;
        subtitle.BackColor = Color.Transparent;
        _hero.Controls.Add(title);
        _hero.Controls.Add(subtitle);
        return _hero;
    }

    private TableLayoutPanel BuildMainRow()
    {
        var main = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _illustration.Size = new Size(260, 290);
        _illustration.Margin = new Padding(0, 0, 32, 0);
        main.Controls.Add(_illustration, 0, 0);

        var right = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.Controls.Add(BuildSteps());
        right.Controls.Add(BuildInputCard());
        main.Controls.Add(right, 1, 0);
        return main;
    }

    private TableLayoutPanel BuildSteps()
    {
        string[] steps =
        [
            "Connect the access point's cable to the POE port of its power adapter, and the adapter's LAN port to the site's router or switch.",
            "Wait about 2 minutes, until the light on the access point is on.",
            "Read the MAC address on the label on the back of the access point.",
            "Make sure this computer is on the same office network (cable or staff Wi-Fi, not guest Wi-Fi).",
        ];

        var list = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 16),
        };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < steps.Length; i++)
        {
            list.Controls.Add(new StepBadge(i + 1) { Margin = new Padding(0, 0, BadgeColumn - 26, 10) }, 0, i);
            list.Controls.Add(Wrap(_stepLabels, steps[i], Theme.Body, Theme.Ink, new Padding(0, 3, 0, 10)), 1, i);
        }

        return list;
    }

    private TableLayoutPanel BuildInputCard()
    {
        _card.ColumnCount = 1;
        _card.AutoSize = true;
        _card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _card.Dock = DockStyle.Fill;
        _card.BackColor = Theme.BrandTint;
        _card.Padding = new Padding(22, 18, 22, 20);
        _card.Margin = Padding.Empty;
        _card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _card.Controls.Add(Wrap(_cardLabels, "Enter the last 4 characters of the MAC address", Theme.Heading, Theme.Ink, new Padding(0, 0, 0, 8)));

        _suffixBox.Font = Theme.Input;
        _suffixBox.Width = 170;
        _suffixBox.MaxLength = 5;
        _suffixBox.CharacterCasing = CharacterCasing.Upper;
        _suffixBox.TextAlign = HorizontalAlignment.Center;
        _suffixBox.BorderStyle = BorderStyle.FixedSingle;
        _suffixBox.Margin = Padding.Empty;
        _suffixBox.AccessibleName = "Last 4 characters of the MAC address";
        _suffixBox.TextChanged += (_, _) => OnSuffixChanged();
        _card.Controls.Add(_suffixBox);
        _card.Controls.Add(_inputHint);

        _runButton.Text = "Find and Configure Access Point";
        _runButton.Font = Theme.Button;
        _runButton.AutoSize = true;
        _runButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _runButton.Padding = new Padding(22, 11, 22, 11);
        _runButton.Margin = new Padding(0, 0, 10, 0);
        _runButton.Click += OnRunClicked;

        _stopButton.Text = "Stop";
        _stopButton.Subdued = true;
        _stopButton.Font = Theme.Body;
        _stopButton.AutoSize = true;
        _stopButton.Padding = new Padding(16, 9, 16, 9);
        _stopButton.Visible = false;
        _stopButton.Click += (_, _) =>
        {
            _stopButton.Enabled = false;
            _statusLabel.Text = "Stopping…";
            _cts?.Cancel();
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty,
            BackColor = Theme.BrandTint,
        };
        buttons.Controls.Add(_runButton);
        buttons.Controls.Add(_stopButton);
        _card.Controls.Add(buttons);
        return _card;
    }

    private TableLayoutPanel BuildResultPanel()
    {
        _resultPanel.ColumnCount = 1;
        _resultPanel.AutoSize = true;
        _resultPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _resultPanel.Dock = DockStyle.Fill;
        _resultPanel.Padding = new Padding(22, 16, 18, 18);
        _resultPanel.Margin = new Padding(0, 8, 0, 18);
        _resultPanel.Visible = false;
        _resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _resultPanel.Controls.Add(_resultTitle);
        _resultPanel.Controls.Add(_resultMessage);
        _resultPanel.Controls.Add(_resultSteps);
        _resultPanel.Controls.Add(_resultDetails);
        _resultPanel.Paint += (_, e) =>
        {
            using var bar = new SolidBrush(_resultAccent);
            e.Graphics.FillRectangle(bar, 0, 0, (int)Math.Round(5 * e.Graphics.DpiX / 96f), _resultPanel.Height);
        };
        return _resultPanel;
    }

    private TableLayoutPanel BuildHelpSection()
    {
        var help = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 12),
        };
        help.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        help.Controls.Add(Wrap(_fullWidthLabels, "Troubleshooting", Theme.Heading, Theme.Brand, new Padding(0, 0, 0, 8)));
        help.Controls.Add(Wrap(_fullWidthLabels, string.Join("\n",
            "•  No light on the access point: check that its cable is in the POE port of the power adapter and that the adapter has power.",
            "•  Not found: this computer and the access point must be on the same network segment. Guest or isolated Wi-Fi will not work.",
            "•  If the access point was only just plugged in, wait 2 minutes and try again.",
            "•  The MAC address only uses 0 to 9 and A to F, so an O on the label is always the number 0.",
            "•  Running it again is safe. Only the access point you identify is changed, and only after it has been verified.",
            "•  Login refused: the access point was set up before. Enter its login under Admin settings, or factory-reset it if agreed with the ITO Networks Team.",
            $"•  Still stuck? Click “Copy support details” and send them to {_settings.SupportContact}."),
            Theme.Body, Theme.Ink, new Padding(0)));
        return help;
    }

    private FlowLayoutPanel BuildFooter()
    {
        var footer = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 0),
        };

        LinkLabel Link(string text, Action action)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Font = Theme.Body,
                LinkColor = Theme.Brand,
                ActiveLinkColor = Theme.BrandDeep,
                VisitedLinkColor = Theme.Brand,
                Margin = new Padding(0, 0, 24, 0),
            };
            link.LinkClicked += (_, _) => action();
            return link;
        }

        footer.Controls.Add(Link("Copy support details", CopyDetails));
        footer.Controls.Add(Link("Open log folder", OpenLogFolder));
        _itSettingsLink.Text = "Admin settings";
        _itSettingsLink.AutoSize = true;
        _itSettingsLink.Font = Theme.Body;
        _itSettingsLink.LinkColor = Theme.Brand;
        _itSettingsLink.ActiveLinkColor = Theme.BrandDeep;
        _itSettingsLink.Margin = new Padding(0, 0, 24, 0);
        _itSettingsLink.LinkClicked += (_, _) => OpenItSettings();
        footer.Controls.Add(_itSettingsLink);
        footer.Controls.Add(new Label
        {
            Text = $"{Theme.Organization} {Theme.Team}  |  Version {AdoptionReport.AssistantVersion}",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 2, 0, 0),
        });
        return footer;
    }

    private void OnSuffixChanged()
    {
        _illustration.Suffix = _suffixBox.Text;
        _inputHint.ForeColor = Theme.Muted;
        _inputHint.Text = "Example: if the label says 802AA812ABCD, type ABCD.";
    }

    private async void OnRunClicked(object? sender, EventArgs e)
    {
        if (_cts is not null)
        {
            return;
        }

        var validation = MacSuffix.Validate(_suffixBox.Text);
        if (!validation.IsValid)
        {
            _inputHint.ForeColor = Theme.ErrorText;
            _inputHint.Text = validation.Problem;
            _suffixBox.Focus();
            _suffixBox.SelectAll();
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        var progress = new Progress<WorkflowProgress>(p =>
        {
            _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, p.Percent));
            if (_stopButton.Enabled)
            {
                _statusLabel.Text = p.Message;
            }
        });

        var request = new AdoptionRequest(_suffixBox.Text, _testMode, _sessionCredential);
        var workflow = CreateWorkflow();
        var token = _cts.Token;
        try
        {
            var report = await Task.Run(() => workflow.RunAsync(request, progress, token));
            ShowReport(report);
        }
        catch (Exception ex)
        {
            // RunAsync reports its own failures; this only guards against something truly unexpected.
            _log.Error("The run failed outside the workflow.", ex);
            MessageBox.Show(this, $"Something unexpected went wrong: {ex.Message}\n\nPlease tell {_settings.SupportContact}.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private AdoptionWorkflow CreateWorkflow()
    {
        var dependencies = new WorkflowDependencies(
            new SystemLocalNetworkProvider(),
            new UbntDiscoveryScanner(),
            new WindowsArpScanner(),
            new NetworkControllerProbe(),
            new TcpSshBannerProbe(),
            new SshNetConnector(),
            _hostKeys,
            _credentials);
        return new AdoptionWorkflow(_settings, dependencies, _log);
    }

    private void SetRunning(bool running)
    {
        _runButton.Enabled = !running;
        _suffixBox.ReadOnly = running;
        _stopButton.Visible = running;
        _stopButton.Enabled = running;
        _progress.Visible = running;
        _statusLabel.Visible = running;
        _itSettingsLink.Enabled = !running;
        if (running)
        {
            _progress.Value = 0;
            _statusLabel.Text = "Starting…";
            _resultPanel.Visible = false;
            _footerNote.Text = string.Empty;
        }
    }

    private void ShowReport(AdoptionReport report)
    {
        _lastReport = report;
        var (back, fore) = Theme.ForTone(report.Tone);
        _resultAccent = fore;
        _resultPanel.BackColor = back;
        _resultTitle.ForeColor = fore;
        _resultTitle.Text = report.Title;
        _resultMessage.Text = report.Message;
        _resultSteps.Text = string.Join("\n", report.NextSteps.Select(s => "•  " + s));
        _resultSteps.Visible = report.NextSteps.Count > 0;

        var details = new List<string>();
        if (report.Device is { } device)
        {
            details.Add($"Access point: {device.Mac}  at {device.IpAddress ?? "unknown address"}");
            if (device.Model is not null || device.Firmware is not null)
            {
                details.Add($"Model: {device.Model ?? "unknown"}    Firmware: {device.Firmware ?? "unknown"}");
            }

            if (device.StatusText is not null)
            {
                details.Add($"Status reported by the access point: {device.StatusText}");
            }
        }

        if (report.PlannedCommands.Count > 0)
        {
            details.Add("A real run would send:");
            details.AddRange(report.PlannedCommands.Select(c => "  " + c));
        }

        _resultDetails.Text = string.Join("\n", details);
        _resultDetails.Visible = details.Count > 0;
        _resultPanel.Visible = true;
        _resultPanel.Invalidate();
        _resultPanel.AccessibleName = $"{report.Title}. {report.Message}";
        UpdateWrapWidths();

        if (report.Outcome == AdoptionOutcome.InvalidInput)
        {
            _inputHint.ForeColor = Theme.ErrorText;
            _inputHint.Text = report.Message;
        }

        _scroller.ScrollControlIntoView(_resultPanel);
    }

    private void CopyDetails()
    {
        var snapshot = _log.Snapshot();
        var recentLog = snapshot.Skip(Math.Max(0, snapshot.Count - 120));
        var text = (_lastReport?.ToSupportText(_log.FilePath) ?? $"UniFi Adoption Assistant {AdoptionReport.AssistantVersion}\nNo setup has been run yet.\n") +
                   "\nRecent log:\n" + string.Join(Environment.NewLine, recentLog);
        try
        {
            Clipboard.SetText(text);
            _footerNote.Text = $"Details copied. Paste them into an email or chat message to {_settings.SupportContact}.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _footerNote.Text = "The clipboard is busy. Please try again.";
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.DefaultDirectory);

            // Opens the folder in File Explorer. No command line is built from any input.
            Process.Start(new ProcessStartInfo { FileName = DiagnosticLog.DefaultDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _footerNote.Text = $"The log folder is {DiagnosticLog.DefaultDirectory}";
        }
    }

    private void OpenItSettings()
    {
        using var dialog = new ItSettingsForm(_settings, _load.SourcePath, _credentials, _hostKeys, _sessionCredential, _testMode);
        dialog.ShowDialog(this);
        _sessionCredential = dialog.SessionCredential;
        _testMode = dialog.TestMode;
        _log.Info($"Admin settings closed. Test mode: {_testMode}. Session login set: {_sessionCredential is not null}.");
        UpdateBanners();
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

        var path = Path.IsPathRooted(_settings.DeviceImagePath)
            ? _settings.DeviceImagePath
            : Path.Combine(AppContext.BaseDirectory, _settings.DeviceImagePath);
        try
        {
            // Read into memory so the file is not locked while the assistant runs.
            _illustration.Photo = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Device photo '{path}' could not be loaded: {ex.Message}");
        }
    }

    /// <summary>Sets each label's maximum width so long text wraps to the window width.</summary>
    /// <summary>Sets each label's maximum width so long text wraps to the window width.</summary>
    private void UpdateWrapWidths()
    {
        var full = _scroller.ClientSize.Width - _scroller.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (full < 120)
        {
            return;
        }

        // Keep hero text clear of the leaf on the right.
        var hero = (int)((ClientSize.Width - _hero.Padding.Horizontal) * 0.8);
        SetWidths(_heroLabels, hero);
        SetWidths(_fullWidthLabels, full);
        foreach (var banner in new[] { _testBanner, _settingsBanner })
        {
            banner.MinimumSize = new Size(banner.MaximumSize.Width, 0);
        }

        var right = full - _illustration.Width - _illustration.Margin.Horizontal;
        SetWidths(_stepLabels, right - LogicalToDeviceUnitsSafe(BadgeColumn));
        SetWidths(_cardLabels, right - _card.Padding.Horizontal);
        SetWidths(_resultLabels, full - _resultPanel.Padding.Horizontal);
    }

    private static void SetWidths(IEnumerable<Label> labels, int width)
    {
        foreach (var label in labels)
        {
            label.MaximumSize = new Size(Math.Max(80, width - label.Margin.Horizontal), 0);
        }
    }

    private int LogicalToDeviceUnitsSafe(int value)
    {
        using var g = CreateGraphics();
        return (int)Math.Round(value * g.DpiX / 96f);
    }

    private static Label Wrap(List<Label> group, string text, Font font, Color color, Padding margin)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = true,
            Margin = margin,
            UseMnemonic = false,
        };
        group.Add(label);
        return label;
    }
}
