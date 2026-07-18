using FlowMasterAgent.Models;
using FlowMasterAgent.Services;

namespace FlowMasterAgent.Forms;

public sealed class MainForm : Form
{
    private readonly ConfigService _config;
    private readonly CredentialService _credentials;
    private readonly UploadService _upload;
    private readonly FolderWatcherService _watcher;
    private readonly LogService _log;
    private readonly IReadOnlyList<UploadRoute> _routes;

    private NotifyIcon _tray = null!;
    private TabControl _tabs = null!;

    // FlowMaster tab (GrowLink route: "flowmaster-packline")
    private TextBox _txtGrowLinkUrl = null!;
    private TextBox _txtGrowLinkKey = null!;
    private TextBox _txtWatchFolder = null!;
    private Button _btnBrowse = null!;
    private Button _btnTestGrowLink = null!;

    // Synopta tab (CropLink route: "block-summary")
    private TextBox _txtCropLinkUrl = null!;
    private TextBox _txtCropLinkKey = null!;
    private TextBox _txtBlockSummaryFolder = null!;
    private Button _btnBrowseBlockSummary = null!;
    private Button _btnTestCropLink = null!;
    private Label _lblCropLinkSummary = null!;

    private Label _lblStatus = null!;
    private TextBox _txtLog = null!;
    private Button _btnSave = null!;
    private Label _lblSummary = null!;

    public MainForm(
        ConfigService config,
        CredentialService credentials,
        UploadService upload,
        FolderWatcherService watcher,
        LogService log)
    {
        _config = config;
        _credentials = credentials;
        _upload = upload;
        _watcher = watcher;
        _log = log;
        _routes = new FileRoutingService().GetDefaultRoutes();

        InitializeLayout();
        SetupTray();

        _log.OnLog += AppendLog;
        _watcher.StatusChanged += UpdateStatus;
        _upload.SummaryUpdated += OnSummaryUpdated;

        Load += (_, _) =>
        {
            PopulateForm();
            _log.Info("GrowLinkAgent started.");
            _watcher.Start();
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(2000, "GrowLinkAgent", "Still running in the background.", ToolTipIcon.Info);
            }
        };
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void InitializeLayout()
    {
        Text = "GrowLinkAgent";
        Size = new Size(760, 820);
        MinimumSize = new Size(480, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;

        // Row 0: tabs (Absolute height). Rows 1-3: save/status (AutoSize).
        // Rows 4-5: import summary. Row 6: log header. Row 7: log (Percent 100).
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(10, 8, 10, 8),
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 270));
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Row 0 — Tabs
        _tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };

        var growLinkPage = new TabPage("FlowMaster");
        growLinkPage.Controls.Add(BuildGrowLinkTab());
        _tabs.TabPages.Add(growLinkPage);

        var cropLinkPage = new TabPage("Synopta");
        cropLinkPage.Controls.Add(BuildCropLinkTab());
        _tabs.TabPages.Add(cropLinkPage);

        layout.Controls.Add(_tabs, 0, 0);

        // Row 1 — Save
        var btnRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        _btnSave = Btn("Save Configuration");
        _btnSave.Click += OnSave;
        btnRow.Controls.Add(_btnSave);
        layout.Controls.Add(btnRow, 0, 1);

        // Row 2 — Status
        var statusPanel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.Controls.Add(RightLabel("Status:"), 0, 0);
        _lblStatus = new Label
        {
            Text = "Idle",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(2, 4, 0, 4),
        };
        statusPanel.Controls.Add(_lblStatus, 1, 0);
        layout.Controls.Add(statusPanel, 0, 2);

        // Row 3 — Summary header
        var summaryHeader = new Label
        {
            Text = "Last Import Summary",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 2),
            TextAlign = ContentAlignment.BottomLeft,
        };
        layout.Controls.Add(summaryHeader, 0, 3);

        // Row 4 — Summary content
        _lblSummary = new Label
        {
            Text = "No uploads yet.",
            AutoSize = false,
            Height = 60,
            Dock = DockStyle.Top,
            Font = new Font("Consolas", 8.5f),
            ForeColor = Color.DimGray,
            Margin = new Padding(6, 0, 0, 4),
        };
        layout.Controls.Add(_lblSummary, 0, 4);

        // Row 5 — Log header
        var logHeader = new Label
        {
            Text = "Activity Log",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 2),
            TextAlign = ContentAlignment.BottomLeft,
        };
        layout.Controls.Add(logHeader, 0, 5);

        // Row 6 — Log textbox
        _txtLog = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.FromArgb(160, 255, 160),
            Font = new Font("Consolas", 8.5f),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 0),
        };
        layout.Controls.Add(_txtLog, 0, 6);

        Controls.Add(layout);
    }

    private Control BuildGrowLinkTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(14, 16, 14, 12),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        for (int i = 0; i < 5; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "FlowMaster sends packline data to GrowLink.",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 14),
        };
        panel.Controls.Add(intro, 0, 0);
        panel.SetColumnSpan(intro, 3);

        panel.Controls.Add(RightLabel("GrowLink Server URL:"), 0, 1);
        _txtGrowLinkUrl = Input();
        panel.Controls.Add(_txtGrowLinkUrl, 1, 1);
        panel.SetColumnSpan(_txtGrowLinkUrl, 2);

        panel.Controls.Add(RightLabel("GrowLink API Key:"), 0, 2);
        _txtGrowLinkKey = Input(password: true);
        panel.Controls.Add(_txtGrowLinkKey, 1, 2);
        panel.SetColumnSpan(_txtGrowLinkKey, 2);

        panel.Controls.Add(RightLabel("FlowMaster/Packline Watch Folder:"), 0, 3);
        _txtWatchFolder = Input();
        panel.Controls.Add(_txtWatchFolder, 1, 3);
        _btnBrowse = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(4, 6, 0, 10) };
        _btnBrowse.Click += OnBrowse;
        panel.Controls.Add(_btnBrowse, 2, 3);

        var testRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        _btnTestGrowLink = Btn("Test GrowLink Connection");
        _btnTestGrowLink.Click += OnTestGrowLink;
        testRow.Controls.Add(_btnTestGrowLink);
        panel.Controls.Add(testRow, 1, 4);
        panel.SetColumnSpan(testRow, 2);

        return panel;
    }

    private Control BuildCropLinkTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 6,
            Padding = new Padding(14, 16, 14, 12),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        for (int i = 0; i < 6; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Synopta sends Block Summary climate data to CropLink.",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 14),
        };
        panel.Controls.Add(intro, 0, 0);
        panel.SetColumnSpan(intro, 3);

        panel.Controls.Add(RightLabel("CropLink Server URL:"), 0, 1);
        _txtCropLinkUrl = Input();
        panel.Controls.Add(_txtCropLinkUrl, 1, 1);
        panel.SetColumnSpan(_txtCropLinkUrl, 2);

        panel.Controls.Add(RightLabel("CropLink API Key:"), 0, 2);
        _txtCropLinkKey = Input(password: true);
        panel.Controls.Add(_txtCropLinkKey, 1, 2);
        panel.SetColumnSpan(_txtCropLinkKey, 2);

        panel.Controls.Add(RightLabel("Block Summary Watch Folder:"), 0, 3);
        _txtBlockSummaryFolder = Input();
        panel.Controls.Add(_txtBlockSummaryFolder, 1, 3);
        _btnBrowseBlockSummary = new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(4, 6, 0, 10) };
        _btnBrowseBlockSummary.Click += OnBrowseBlockSummaryFolder;
        panel.Controls.Add(_btnBrowseBlockSummary, 2, 3);

        var testRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        _btnTestCropLink = Btn("Test CropLink Connection");
        _btnTestCropLink.Click += OnTestCropLink;
        testRow.Controls.Add(_btnTestCropLink);
        panel.Controls.Add(testRow, 1, 4);
        panel.SetColumnSpan(testRow, 2);

        _lblCropLinkSummary = new Label
        {
            Text = "No CropLink uploads yet.",
            AutoSize = false,
            Height = 100,
            Dock = DockStyle.Top,
            Font = new Font("Consolas", 8.5f),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 14, 0, 0),
        };
        panel.Controls.Add(_lblCropLinkSummary, 0, 5);
        panel.SetColumnSpan(_lblCropLinkSummary, 3);

        return panel;
    }

    // ── Tray ──────────────────────────────────────────────────────────────────

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GrowLinkAgent",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ── Form actions ──────────────────────────────────────────────────────────

    private void PopulateForm()
    {
        _txtWatchFolder.Text = _config.Config.WatchFolder;
        _txtBlockSummaryFolder.Text = _config.Config.BlockSummaryFolder;

        var flow = FindRoute("flowmaster-packline");
        if (flow is not null)
        {
            _txtGrowLinkUrl.Text = _config.GetRouteServerUrl("flowmaster-packline") ?? string.Empty;
            _txtGrowLinkKey.Text = _credentials.LoadRouteKey(GetSlot(flow)) ?? string.Empty;
        }

        var block = FindRoute("block-summary");
        if (block is not null)
        {
            _txtCropLinkUrl.Text = _config.GetRouteServerUrl("block-summary") ?? string.Empty;
            _txtCropLinkKey.Text = _credentials.LoadRouteKey(GetSlot(block)) ?? string.Empty;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        try
        {
            var routeConfigs = new List<RouteConfig>
            {
                new() { RouteId = "block-summary",       ServerUrl = _txtCropLinkUrl.Text.Trim(), Enabled = true },
                new() { RouteId = "flowmaster-packline", ServerUrl = _txtGrowLinkUrl.Text.Trim(), Enabled = true },
            };

            var cfg = new AppConfig
            {
                // ServerUrl is the pre-route legacy field. It is no longer editable from this
                // form, so carry forward whatever is already on disk rather than blanking it.
                ServerUrl          = _config.Config.ServerUrl,
                WatchFolder        = _txtWatchFolder.Text.Trim(),
                BlockSummaryFolder = _txtBlockSummaryFolder.Text.Trim(),
                UseRouteKeys       = true,
                RouteConfigs       = routeConfigs,
            };
            _config.Save(cfg);

            SaveOrRemoveRouteKey("flowmaster-packline", _txtGrowLinkKey.Text);
            SaveOrRemoveRouteKey("block-summary", _txtCropLinkKey.Text);

            _watcher.Stop();
            _watcher.Start();

            MessageBox.Show("Configuration saved.", "GrowLinkAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "GrowLinkAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnTestGrowLink(object? sender, EventArgs e) =>
        await TestRoute("flowmaster-packline", "GrowLink", _btnTestGrowLink, "Test GrowLink Connection");

    private async void OnTestCropLink(object? sender, EventArgs e)
    {
        var route = FindRoute("block-summary");
        if (route is null)
            return;

        _btnTestCropLink.Enabled = false;
        _btnTestCropLink.Text = "Testing…";
        try
        {
            var results = await _upload.PingRouteKeysAsync([route]);
            var result = results[0];

            if (result.Success)
                MessageBox.Show(
                    $"CropLink connection successful{Environment.NewLine}Organization: {result.OrganizationName}",
                    "GrowLinkAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(
                    $"CropLink authentication failed{Environment.NewLine}{result.Message}",
                    "GrowLinkAgent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "GrowLinkAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnTestCropLink.Enabled = true;
            _btnTestCropLink.Text = "Test CropLink Connection";
        }
    }

    private async Task TestRoute(string routeId, string displayName, Button button, string idleText)
    {
        var route = FindRoute(routeId);
        if (route is null)
            return;

        button.Enabled = false;
        button.Text = "Testing…";
        try
        {
            var results = await _upload.PingRouteKeysAsync([route]);
            var result = results[0];

            if (result.Success)
                MessageBox.Show($"{displayName} connection successful.", "GrowLinkAgent",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show($"{displayName} connection failed.\n{result.Message}", "GrowLinkAgent",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "GrowLinkAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            button.Enabled = true;
            button.Text = idleText;
        }
    }

    private UploadRoute? FindRoute(string routeId) =>
        _routes.FirstOrDefault(r => string.Equals(r.Id, routeId, StringComparison.OrdinalIgnoreCase));

    private static string GetSlot(UploadRoute route) =>
        string.IsNullOrWhiteSpace(route.KeySlot) ? route.Id : route.KeySlot;

    private void SaveOrRemoveRouteKey(string routeId, string keyValue)
    {
        var route = FindRoute(routeId);
        if (route is null)
            return;

        var slot = GetSlot(route);
        if (string.IsNullOrWhiteSpace(keyValue))
            _credentials.RemoveRouteKey(slot);
        else
            _credentials.SaveRouteKey(slot, keyValue.Trim());
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the Flow Master PDF/CSV export folder",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrEmpty(_txtWatchFolder.Text))
            dlg.InitialDirectory = _txtWatchFolder.Text;

        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtWatchFolder.Text = dlg.SelectedPath;
    }

    private void OnBrowseBlockSummaryFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the dedicated CropLink Block Summary export folder",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrEmpty(_txtBlockSummaryFolder.Text))
            dlg.InitialDirectory = _txtBlockSummaryFolder.Text;

        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtBlockSummaryFolder.Text = dlg.SelectedPath;
    }

    // ── Cross-thread helpers ───────────────────────────────────────────────────

    private void AppendLog(string line)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        _txtLog.AppendText(line + Environment.NewLine);
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.ScrollToCaret();
    }

    private void UpdateStatus(string status)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateStatus(status)); return; }
        _lblStatus.Text = status;
        _lblStatus.ForeColor = status.StartsWith("Watching") ? Color.ForestGreen : Color.DimGray;
    }

    private void OnSummaryUpdated(BatchSummary s)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => OnSummaryUpdated(s)); return; }

        var kg = s.KgImported > 0 ? $"{s.KgImported:F1} kg" : "—";
        var varieties = s.VarietiesImported > 0 ? s.VarietiesImported.ToString() : "—";
        var sync = s.LastSync.HasValue ? s.LastSync.Value.ToString("HH:mm:ss") : "—";

        _lblSummary.Text =
            $"PDF/CSV found: {s.Found,-5}  Last sync:   {sync}{Environment.NewLine}" +
            $"Uploaded:   {s.Succeeded,-5}  KG imported: {kg}{Environment.NewLine}" +
            $"Skipped:    {s.Skipped,-5}  Varieties:   {varieties}{Environment.NewLine}" +
            $"Failed:     {s.Failed}";
        _lblSummary.ForeColor = s.Failed > 0 ? Color.Firebrick : Color.DimGray;

        if (s.LastCropLinkTime.HasValue)
        {
            var cropLinkTime = s.LastCropLinkTime.Value.ToString("HH:mm:ss");
            _lblCropLinkSummary.Text =
                $"Last CropLink filename: {s.LastCropLinkFilename}{Environment.NewLine}" +
                $"Parsed readings:        {s.LastCropLinkReadingsParsed?.ToString() ?? "—"}{Environment.NewLine}" +
                $"Result:                 {s.LastCropLinkResult}{Environment.NewLine}" +
                $"HTTP status:            {s.LastCropLinkHttpStatus?.ToString() ?? "—"}{Environment.NewLine}" +
                $"Import ID:              {s.LastCropLinkImportId ?? "—"}{Environment.NewLine}" +
                $"Time:                   {cropLinkTime}";
            _lblCropLinkSummary.ForeColor = s.LastCropLinkResult == "Failed" ? Color.Firebrick : Color.DimGray;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _watcher.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Control factories ─────────────────────────────────────────────────────

    private static Label RightLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleRight,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 6, 6, 10),
    };

    private static TextBox Input(bool password = false) => new()
    {
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 6, 0, 10),
        PasswordChar = password ? '●' : '\0',
    };

    private static Button Btn(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(0, 0, 8, 0),
    };
}
