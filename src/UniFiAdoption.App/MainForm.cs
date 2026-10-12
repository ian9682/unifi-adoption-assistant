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
    private readonly SettingsLoadResult _load;
    private readonly AssistantSettings _settings;
    private readonly DiagnosticLog _log = DiagnosticLog.CreateDefault();
    private readonly JsonHostKeyStore _hostKeys;
    private readonly DpapiCredentialStore _credentials = new();

    private readonly Panel _scroller = new();
    private readonly TableLayoutPanel _root = new();
    private readonly AccessPointIllustration _illustration = new();
    private readonly TextBox _suffixBox = new();
    private readonly Label _inputHint;
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
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

    // Labels whose wrap width follows the window width.
    private readonly List<Label> _fullWidthLabels = [];
    private readonly List<Label> _rightColumnLabels = [];
    private readonly List<Label> _resultLabels = [];

    private SshCredential? _sessionCredential;
    private bool _testMode;
    private CancellationTokenSource? _cts;
    private AdoptionReport? _lastReport;
    private Color _resultAccent = Theme.Accent;

    public MainForm(SettingsLoadResult load, bool forceTestMode)
    {
        _load = load;
        _settings = load.Settings;
        _testMode = _settings.TestMode || forceTestMode;
        _hostKeys = new JsonHostKeyStore(JsonHostKeyStore.DefaultPath, _settings.PinnedHostKeys);

        _inputHint = Wrap(_rightColumnLabels, "Example: if the label says 802AA812ABCD, type ABCD.", Theme.Small, Theme.Muted, new Padding(0, 4, 0, 14));
        _statusLabel = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.Muted, new Padding(0, 2, 0, 8));
        _testBanner = Wrap(_fullWidthLabels, "Test mode is on. The assistant will find and check the access point but will not change anything.", Theme.Body, Color.FromArgb(0x5C, 0x44, 0x00), new Padding(0, 0, 0, 12));
        _settingsBanner = Wrap(_fullWidthLabels, string.Empty, Theme.Body, Theme.ErrorText, new Padding(0, 0, 0, 12));
        _resultTitle = Wrap(_resultLabels, string.Empty, Theme.ResultTitle, Theme.Ink, new Padding(0, 0, 0, 6));
        _resultMessage = Wrap(_resultLabels, string.Empty, Theme.Body, Theme.Ink, new Padding(0, 0, 0, 6));
        _resultSteps = Wrap(_resultLabels, string.Empty, Theme.Body, Theme.Ink, new Padding(0, 2, 0, 4));
        _resultDetails = Wrap(_resultLabels, string.Empty, Theme.Mono, Theme.Muted, new Padding(0, 6, 0, 0));
        _footerNote = Wrap(_fullWidthLabels, string.Empty, Theme.Small, Theme.Muted, new Padding(0, 4, 0, 0));

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "UniFi Adoption Assistant";
        Font = Theme.Body;
        BackColor = Color.White;
        ForeColor = Theme.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 780);
        MinimumSize = new Size(700, 520);
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
        _scroller.Padding = new Padding(28, 20, 28, 20);
        _scroller.BackColor = Color.White;
        _scroller.Resize += (_, _) => UpdateWrapWidths(); // also fires when the window moves to a screen with different scaling

        _root.Dock = DockStyle.Top;
        _root.AutoSize = true;
        _root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _root.ColumnCount = 1;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.BackColor = Color.White;

        _root.Controls.Add(Wrap(_fullWidthLabels, "Set up a UniFi access point", Theme.Title, Theme.Ink, new Padding(0, 0, 0, 2)));
        _root.Controls.Add(Wrap(_fullWidthLabels,
            "This assistant finds the access point on this site's network and connects it to the company's controller.",
            Theme.Body, Theme.Muted, new Padding(0, 0, 0, 16)));

        _testBanner.BackColor = Color.FromArgb(0xFF, 0xF1, 0xB8);
        _testBanner.Padding = new Padding(10, 8, 10, 8);
        _root.Controls.Add(_testBanner);

        _settingsBanner.BackColor = Color.FromArgb(0xFD, 0xEC, 0xEA);
        _settingsBanner.Padding = new Padding(10, 8, 10, 8);
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
        Controls.Add(_scroller);
        AcceptButton = _runButton;
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
            Margin = new Padding(0, 0, 0, 12),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _illustration.Size = new Size(270, 300);
        _illustration.Margin = new Padding(0, 0, 28, 0);
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

        right.Controls.Add(Wrap(_rightColumnLabels,
            "1.  Plug the access point's cable into the POE port of its power adapter, and connect the adapter's LAN port to the router or network switch.\n" +
            "2.  Wait until the light on the access point is on. This takes about 2 minutes.\n" +
            "3.  Turn the access point over and find the MAC address on its label.",
            Theme.Body, Theme.Ink, new Padding(0, 4, 0, 18)));

        var inputLabel = Wrap(_rightColumnLabels, "Enter the last 4 characters of the MAC address", Theme.Heading, Theme.Ink, new Padding(0, 0, 0, 6));
        right.Controls.Add(inputLabel);

        _suffixBox.Font = Theme.Input;
        _suffixBox.Width = 170;
        _suffixBox.MaxLength = 5;
        _suffixBox.CharacterCasing = CharacterCasing.Upper;
        _suffixBox.TextAlign = HorizontalAlignment.Center;
        _suffixBox.BorderStyle = BorderStyle.FixedSingle;
        _suffixBox.Margin = new Padding(0);
        _suffixBox.AccessibleName = "Last 4 characters of the MAC address";
        _suffixBox.TextChanged += (_, _) => OnSuffixChanged();
        right.Controls.Add(_suffixBox);
        right.Controls.Add(_inputHint);

        _runButton.Text = "Find and Configure Access Point";
        _runButton.Font = Theme.Button;
        _runButton.AutoSize = true;
        _runButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _runButton.Padding = new Padding(18, 9, 18, 9);
        _runButton.Margin = new Padding(0, 0, 10, 0);
        _runButton.FlatStyle = FlatStyle.Flat;
        _runButton.FlatAppearance.BorderSize = 0;
        _runButton.FlatAppearance.MouseDownBackColor = Theme.AccentPressed;
        _runButton.FlatAppearance.MouseOverBackColor = Theme.AccentPressed;
        _runButton.BackColor = Theme.Accent;
        _runButton.ForeColor = Color.White;
        _runButton.UseVisualStyleBackColor = false;
        _runButton.Cursor = Cursors.Hand;
        _runButton.Click += OnRunClicked;

        _stopButton.Text = "Stop";
        _stopButton.Font = Theme.Body;
        _stopButton.AutoSize = true;
        _stopButton.Padding = new Padding(12, 7, 12, 7);
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
        };
        buttons.Controls.Add(_runButton);
        buttons.Controls.Add(_stopButton);
        right.Controls.Add(buttons);

        main.Controls.Add(right, 1, 0);
        return main;
    }

    private TableLayoutPanel BuildResultPanel()
    {
        _resultPanel.ColumnCount = 1;
        _resultPanel.AutoSize = true;
        _resultPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _resultPanel.Dock = DockStyle.Fill;
        _resultPanel.Padding = new Padding(20, 14, 16, 16);
        _resultPanel.Margin = new Padding(0, 8, 0, 16);
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
            Margin = new Padding(0, 4, 0, 10),
        };
        help.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        help.Controls.Add(Wrap(_fullWidthLabels, "Having trouble?", Theme.Heading, Theme.Ink, new Padding(0, 0, 0, 6)));
        help.Controls.Add(Wrap(_fullWidthLabels, string.Join("\n",
            "•  No light on the access point: check that its cable goes into the POE port of the power adapter, and that the adapter is plugged in and switched on.",
            "•  The access point and this computer must be on the same network. Use the office network, not a guest Wi-Fi network.",
            "•  If the access point was only just plugged in, wait 2 minutes and try again.",
            "•  Check the 4 characters again. The letters only go from A to F, so an O is always the number 0.",
            "•  Trying again is safe. The assistant only changes the access point whose characters you typed, and only after it has checked it.",
            "•  Don't press the reset button on the access point unless IT asks you to.",
            $"•  Still stuck? Click “Copy details for IT” and send the details to {_settings.SupportContact}."),
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
            Margin = new Padding(0, 6, 0, 0),
        };

        LinkLabel Link(string text, Action action)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Font = Theme.Body,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentPressed,
                Margin = new Padding(0, 0, 22, 0),
            };
            link.LinkClicked += (_, _) => action();
            return link;
        }

        footer.Controls.Add(Link("Copy details for IT", CopyDetails));
        footer.Controls.Add(Link("Open log folder", OpenLogFolder));
        _itSettingsLink.Text = "IT settings";
        _itSettingsLink.AutoSize = true;
        _itSettingsLink.Font = Theme.Body;
        _itSettingsLink.LinkColor = Theme.Accent;
        _itSettingsLink.Margin = new Padding(0, 0, 22, 0);
        _itSettingsLink.LinkClicked += (_, _) => OpenItSettings();
        footer.Controls.Add(_itSettingsLink);
        footer.Controls.Add(new Label { Text = $"Version {AdoptionReport.AssistantVersion}", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0) });
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
        _runButton.BackColor = running ? Theme.Line : Theme.Accent;
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
            _footerNote.Text = "Details copied. Paste them into an email or chat message to IT.";
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
        _log.Info($"IT settings closed. Test mode: {_testMode}. Session login set: {_sessionCredential is not null}.");
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
    private void UpdateWrapWidths()
    {
        var full = _scroller.ClientSize.Width - _scroller.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (full < 120)
        {
            return;
        }

        foreach (var label in _fullWidthLabels)
        {
            label.MaximumSize = new Size(Math.Max(80, full - label.Margin.Horizontal), 0);
        }

        // Banners fill the full width so their background reads as a strip.
        foreach (var banner in new[] { _testBanner, _settingsBanner })
        {
            banner.MinimumSize = new Size(banner.MaximumSize.Width, 0);
        }

        var right = full - _illustration.Width - _illustration.Margin.Horizontal;
        foreach (var label in _rightColumnLabels)
        {
            label.MaximumSize = new Size(Math.Max(80, right - label.Margin.Horizontal), 0);
        }

        var inner = full - _resultPanel.Padding.Horizontal;
        foreach (var label in _resultLabels)
        {
            label.MaximumSize = new Size(Math.Max(80, inner - label.Margin.Horizontal), 0);
        }
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
