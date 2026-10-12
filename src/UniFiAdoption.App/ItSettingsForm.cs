using UniFiAdoption.Core.Configuration;
using UniFiAdoption.Core.Logging;
using UniFiAdoption.Core.Security;
using UniFiAdoption.Core.Ssh;
using UniFiAdoption.Core.Workflow;

namespace UniFiAdoption.App;

/// <summary>
/// Options for IT, usually used while guiding someone remotely. It never displays a stored
/// password, and nothing here lets anyone type commands for the access point.
/// </summary>
internal sealed class ItSettingsForm : Form
{
    private readonly ICredentialStore _store;
    private readonly IHostKeyStore _hostKeys;
    private readonly TextBox _username = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _remember = new();
    private readonly CheckBox _testMode = new();
    private readonly Label _loginStatus = new();

    public ItSettingsForm(
        AssistantSettings settings, string? settingsPath, ICredentialStore store, IHostKeyStore hostKeys,
        SshCredential? sessionCredential, bool testMode)
    {
        _store = store;
        _hostKeys = hostKeys;
        SessionCredential = sessionCredential;
        TestMode = testMode;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "IT settings";
        Font = Theme.Body;
        BackColor = Color.White;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 640);
        AutoScroll = true;

        const int TextWidth = 540;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(24, 18, 24, 18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        Label Para(string text, Font font, Color color, int top = 0, int bottom = 6) => new()
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, top, 0, bottom),
            UseMnemonic = false,
        };

        // Device login --------------------------------------------------------------------------
        layout.Controls.Add(Para("Access point login", Theme.Heading, Theme.Ink));
        layout.Controls.Add(Para(
            "Only needed if the access point was set up before and no longer accepts its factory login. " +
            "The password is never shown again and is never written to the log.",
            Theme.Small, Theme.Muted, bottom: 10));

        layout.Controls.Add(Para("User name", Theme.Body, Theme.Ink, bottom: 2));
        _username.Width = 300;
        _username.Text = sessionCredential?.Username ?? store.SavedUsername ?? string.Empty;
        _username.AccessibleName = "Access point user name";
        layout.Controls.Add(_username);

        layout.Controls.Add(Para("Password", Theme.Body, Theme.Ink, top: 8, bottom: 2));
        _password.Width = 300;
        _password.UseSystemPasswordChar = true;
        _password.AccessibleName = "Access point password";
        layout.Controls.Add(_password);

        _remember.Text = "Remember on this computer (encrypted for this Windows account)";
        _remember.AutoSize = true;
        _remember.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_remember);

        var loginButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        var useLogin = new Button { Text = "Use this login", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        useLogin.Click += (_, _) => UseLogin();
        var removeLogin = new Button { Text = "Remove login", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        removeLogin.Click += (_, _) => RemoveLogin();
        loginButtons.Controls.Add(useLogin);
        loginButtons.Controls.Add(removeLogin);
        layout.Controls.Add(loginButtons);

        _loginStatus.AutoSize = true;
        _loginStatus.MaximumSize = new Size(TextWidth, 0);
        _loginStatus.ForeColor = Theme.Muted;
        _loginStatus.Font = Theme.Small;
        _loginStatus.Margin = new Padding(0, 6, 0, 18);
        layout.Controls.Add(_loginStatus);
        UpdateLoginStatus();

        // Test mode -----------------------------------------------------------------------------
        layout.Controls.Add(Para("Test mode", Theme.Heading, Theme.Ink));
        _testMode.Text = "Find and check the access point, but don't change anything";
        _testMode.AutoSize = true;
        _testMode.Checked = testMode;
        _testMode.Margin = new Padding(0, 0, 0, 4);
        _testMode.CheckedChanged += (_, _) => TestMode = _testMode.Checked;
        layout.Controls.Add(_testMode);
        layout.Controls.Add(Para(
            "Test mode still logs in and reads the access point's status, and it shows the exact commands a real run would send.",
            Theme.Small, Theme.Muted, bottom: 18));

        // Fingerprints --------------------------------------------------------------------------
        layout.Controls.Add(Para("Access point fingerprints", Theme.Heading, Theme.Ink));
        layout.Controls.Add(Para(
            "This computer remembers each access point's security fingerprint and refuses to continue if it changes. " +
            "A factory reset changes it. Clear the saved fingerprints only if you know the access point was reset.",
            Theme.Small, Theme.Muted));
        var forget = new Button { Text = "Forget saved fingerprints…", AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 2, 0, 18) };
        forget.Click += (_, _) => ForgetFingerprints();
        layout.Controls.Add(forget);

        // About ---------------------------------------------------------------------------------
        layout.Controls.Add(Para("About this copy", Theme.Heading, Theme.Ink));
        layout.Controls.Add(Para(
            $"Version: {AdoptionReport.AssistantVersion}\n" +
            $"Controller address: {settings.InformUrl}\n" +
            $"Settings: {settingsPath ?? "built-in defaults (no settings.json found)"}\n" +
            $"Factory login allowed: {(settings.AllowFactoryDefaultCredentials ? "yes" : "no")}\n" +
            $"Logs: {DiagnosticLog.DefaultDirectory}",
            Theme.Small, Theme.Muted, bottom: 18));

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(14, 4, 14, 4), DialogResult = DialogResult.OK };
        layout.Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(layout);
        ResumeLayout(performLayout: true);
    }

    public SshCredential? SessionCredential { get; private set; }

    public bool TestMode { get; private set; }

    private void UseLogin()
    {
        var username = _username.Text.Trim();
        var password = _password.Text;
        if (username.Length == 0 || password.Length == 0)
        {
            _loginStatus.ForeColor = Theme.ErrorText;
            _loginStatus.Text = "Enter both a user name and a password.";
            return;
        }

        SessionCredential = new SshCredential(username, password, CredentialSource.ItProvidedThisSession);
        if (_remember.Checked)
        {
            try
            {
                _store.Save(username, password);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _loginStatus.ForeColor = Theme.ErrorText;
                _loginStatus.Text = $"The login will be used now, but it could not be saved: {ex.Message}";
                _password.Clear();
                return;
            }
        }

        _password.Clear();
        UpdateLoginStatus();
    }

    private void RemoveLogin()
    {
        SessionCredential = null;
        try
        {
            _store.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loginStatus.ForeColor = Theme.ErrorText;
            _loginStatus.Text = $"The saved login could not be removed: {ex.Message}";
            return;
        }

        _password.Clear();
        UpdateLoginStatus();
    }

    private void UpdateLoginStatus()
    {
        _loginStatus.ForeColor = Theme.Muted;
        var parts = new List<string>();
        parts.Add(SessionCredential is null
            ? "No login entered for this session."
            : $"The next setup will try the login for '{SessionCredential.Username}' first.");
        parts.Add(_store.HasSavedCredential
            ? $"A login for '{_store.SavedUsername ?? "unknown"}' is saved on this computer."
            : "No login is saved on this computer.");
        _loginStatus.Text = string.Join(" ", parts);
    }

    private void ForgetFingerprints()
    {
        var answer = MessageBox.Show(this,
            "Forget the security fingerprints of every access point this computer has set up?\n\n" +
            "Only do this if an access point was factory reset.",
            "IT settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            _hostKeys.Clear();
            MessageBox.Show(this, "Saved fingerprints were cleared.", "IT settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
