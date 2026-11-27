using System.Drawing;
using System.Windows.Forms;
using NebulaServer.Services.Core;
using static NebulaServer.Desktop.DesktopTheme;

namespace NebulaServer.Desktop;

internal sealed partial class MainForm
{
    private Label _licenseSettingsStatus = null!;
    private TextBox _licenseKeyBox = null!;
    private Button _licenseActivateButton = null!;
    private Button _licenseVerifyButton = null!;
    private Control _downloadActivationBanner = null!;
    private Label _downloadActivationText = null!;
    private bool? _lastRenderedLicenseState;
    private string? _lastRenderedLicenseKey;

    private Control BuildDownloadActivationBanner()
    {
        var banner = Card("License activation required");
        _downloadActivationText = Label(
            "Downloads are locked until you activate a valid license key from NebulaLicensingServer.",
            9);
        _downloadActivationText.ForeColor = Accent;
        var activateNow = Button("Activate license…", () => EnsureActivatedForDownload(), primary: true);
        var openSettings = Button("License settings", () => Navigate("Settings"));
        banner.Controls.Add(_downloadActivationText);
        banner.Controls.Add(Row(activateNow, openSettings));
        _downloadActivationBanner = banner;
        return banner;
    }

    private Control BuildLicenseSettingsCard()
    {
        var license = _services.GetRequiredService<LicenseService>();
        var card = Card("License & Activation");
        _licenseSettingsStatus = Label("Checking license status…", 10, true);
        card.Controls.Add(_licenseSettingsStatus);

        var serverInfo = license.IsLicensingServerConfigured
            ? $"Licensing server: {license.LicensingServerBaseUrl}"
            : "Licensing server: Not configured (set Licensing:BaseUrl in appsettings.json)";
        var serverLabel = Label(serverInfo, 9);
        serverLabel.ForeColor = Muted;
        card.Controls.Add(serverLabel);

        card.Controls.Add(Label("Machine hash (for binding)", 9, true));
        card.Controls.Add(new TextBox
        {
            Text = license.MachineHash,
            ReadOnly = true,
            Dock = DockStyle.Top,
            Font = EnterpriseFonts.Create(8, mono: true),
            Margin = new Padding(0, 0, 0, 12),
            AccessibleName = "Machine hash"
        });

        card.Controls.Add(Label("License key", 9, true));
        _licenseKeyBox = new TextBox
        {
            PlaceholderText = "NBL-XXXX-XXXX-XXXX-XXXX",
            Dock = DockStyle.Top,
            Font = EnterpriseFonts.Create(9, mono: true),
            Margin = new Padding(0, 0, 0, 12),
            AccessibleName = "License key"
        };
        if (!string.IsNullOrWhiteSpace(license.Binding?.LicenseKey))
            _licenseKeyBox.Text = license.Binding.LicenseKey;
        card.Controls.Add(_licenseKeyBox);

        _licenseActivateButton = Button("Activate license", () => { }, primary: true);
        _licenseActivateButton.Click += async (_, _) => await BusyAsync(_licenseActivateButton, async () =>
        {
            var result = await license.ActivateAndPersistAsync(_licenseKeyBox.Text, _closing.Token);
            UpdateLicenseUi(force: true);
            if (!result.IsValid)
                throw new InvalidOperationException(result.Message);

            _status.Text = "License activated. Downloads are now unlocked.";
            MessageBox.Show(this,
                result.ExpiresAt.HasValue
                    ? $"License activated!\nExpires: {result.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : "License activated!",
                "Tanzeel — Activation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });

        _licenseVerifyButton = Button("Verify now", () => { });
        _licenseVerifyButton.Click += async (_, _) => await BusyAsync(_licenseVerifyButton, async () =>
        {
            var key = string.IsNullOrWhiteSpace(_licenseKeyBox.Text)
                ? license.Binding?.LicenseKey ?? string.Empty
                : _licenseKeyBox.Text;
            var result = await license.ActivateAndPersistAsync(key, _closing.Token);
            UpdateLicenseUi(force: true);
            if (!result.IsValid)
                throw new InvalidOperationException(result.Message);

            MessageBox.Show(this, "License is valid and active.", "Tanzeel — License",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        card.Controls.Add(Row(_licenseActivateButton, _licenseVerifyButton));
        UpdateLicenseUi(force: true);
        return card;
    }

    private void UpdateLicenseUi(bool force = false)
    {
        if (IsDisposed) return;

        var license = _services.GetRequiredService<LicenseService>();
        var isActivated = license.IsActivated;
        var currentKey = _services.GetRequiredService<NebulaServer.Services.Server.ServerStateService>().Get().LicenseKey;

        if (!force && _lastRenderedLicenseState == isActivated &&
            string.Equals(_lastRenderedLicenseKey, currentKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastRenderedLicenseState = isActivated;
        _lastRenderedLicenseKey = currentKey;

        if (_downloadActivationBanner != null)
        {
            _downloadActivationBanner.Visible = !isActivated;
            if (!isActivated && _downloadActivationText != null)
            {
                _downloadActivationText.Text = license.IsLicensingServerConfigured
                    ? "Downloads are locked until you activate a valid license key from NebulaLicensingServer."
                    : "Downloads are locked: Licensing:BaseUrl is not configured in appsettings.json.";
            }
        }

        if (_licenseSettingsStatus != null)
        {
            var expiresText = license.Binding?.ExpiresAt is { } exp && exp != default
                ? $" · Expires {exp.ToLocalTime():yyyy-MM-dd}"
                : string.Empty;
            _licenseSettingsStatus.Text = isActivated
                ? $"✓ Active ({LicenseService.Mask(currentKey)}{expiresText})"
                : "✕ Not activated — downloads are disabled until activation";
            _licenseSettingsStatus.ForeColor = isActivated ? Color.FromArgb(53, 208, 127) : Accent;
        }

        if (_licenseKeyBox != null && string.IsNullOrWhiteSpace(_licenseKeyBox.Text) && !string.IsNullOrWhiteSpace(currentKey))
            _licenseKeyBox.Text = currentKey;
    }

    private bool EnsureActivatedForDownload()
    {
        var license = _services.GetRequiredService<LicenseService>();
        if (license.IsActivated)
            return true;

        return ShowActivationDialog();
    }

    private bool ShowActivationDialog()
    {
        var license = _services.GetRequiredService<LicenseService>();

        using var dialog = new Form
        {
            Text = "Activate Tanzeel License",
            Width = 520,
            Height = 320,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Background,
            ForeColor = Ink,
            Font = EnterpriseFonts.Create(10)
        };

        var layout = Stack();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(22);

        layout.Controls.Add(Label("Activation required to download", 14, true));
        var hint = Label(
            license.IsLicensingServerConfigured
                ? $"Enter your license key issued by {license.LicensingServerBaseUrl} to unlock downloads."
                : "Licensing:BaseUrl is empty in appsettings.json. Configure the licensing server URL and restart, then enter your license key.",
            9);
        hint.ForeColor = Muted;
        hint.MaximumSize = new Size(460, 0);
        layout.Controls.Add(hint);

        var keyBox = new TextBox
        {
            PlaceholderText = "NBL-XXXX-XXXX-XXXX-XXXX",
            Dock = DockStyle.Top,
            Font = EnterpriseFonts.Create(10, mono: true),
            Margin = new Padding(0, 8, 0, 8),
            Text = license.Binding?.LicenseKey ?? string.Empty
        };
        layout.Controls.Add(keyBox);

        var errorLabel = Label(string.Empty, 9);
        errorLabel.ForeColor = Color.Firebrick;
        errorLabel.MaximumSize = new Size(460, 0);
        errorLabel.Visible = false;
        layout.Controls.Add(errorLabel);

        var activated = false;
        Button activateBtn = null!;
        Button cancelBtn = null!;

        activateBtn = Button("Activate", () => { }, primary: true);
        cancelBtn = Button("Cancel", () => dialog.Close());

        activateBtn.Click += async (_, _) =>
        {
            errorLabel.Visible = false;
            activateBtn.Enabled = false;
            cancelBtn.Enabled = false;
            activateBtn.Text = "Activating…";
            try
            {
                var result = await license.ActivateAndPersistAsync(keyBox.Text, _closing.Token);
                UpdateLicenseUi(force: true);
                if (!result.IsValid)
                {
                    errorLabel.Text = result.Message;
                    errorLabel.Visible = true;
                    return;
                }

                activated = true;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (Exception ex)
            {
                errorLabel.Text = ex.Message;
                errorLabel.Visible = true;
            }
            finally
            {
                if (!dialog.IsDisposed)
                {
                    activateBtn.Enabled = true;
                    cancelBtn.Enabled = true;
                    activateBtn.Text = "Activate";
                }
            }
        };

        layout.Controls.Add(Row(activateBtn, cancelBtn));
        dialog.Controls.Add(layout);
        dialog.AcceptButton = activateBtn;
        dialog.CancelButton = cancelBtn;
        dialog.ShowDialog(this);
        return activated;
    }
}
