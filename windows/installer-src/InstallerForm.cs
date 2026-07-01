using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class InstallerForm : Form
{
    private readonly InstallerOptions _startupOptions;
    private readonly TextBox _pathTextBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly Button _cancelButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly CheckBox _launchAfterInstallCheckBox = new();

    public InstallerForm(InstallerOptions startupOptions)
    {
        _startupOptions = startupOptions;

        SuspendLayout();

        Text = "Vesper Launcher Setup";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 500);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(243, 245, 248);
        ForeColor = Color.FromArgb(30, 30, 30);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);

        var heroPanel = new GradientPanel
        {
            Left = 0,
            Top = 0,
            Width = 780,
            Height = 154,
            StartColor = Color.FromArgb(225, 230, 238),
            EndColor = Color.FromArgb(243, 245, 248)
        };

        var title = new Label
        {
            Left = 34,
            Top = 36,
            Width = 712,
            Height = 36,
            Text = "Установка Vesper",
            Font = new Font("Segoe UI Semibold", 19f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 25, 35),
            BackColor = Color.Transparent
        };

        var subtitle = new Label
        {
            Left = 34,
            Top = 78,
            Width = 712,
            Height = 42,
            Text = "Выбери диск и папку. Обновления.",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(80, 95, 110),
            BackColor = Color.Transparent
        };

        heroPanel.Controls.Add(title);
        heroPanel.Controls.Add(subtitle);

        var bodyPanel = new Panel
        {
            Left = 28,
            Top = 180,
            Width = 724,
            Height = 218,
            BackColor = Color.White
        };

        var pathLabel = new Label
        {
            Left = 22,
            Top = 22,
            Width = 660,
            Height = 22,
            Text = "Папка установки",
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 50, 65),
            BackColor = Color.Transparent
        };

        _pathTextBox.Left = 22;
        _pathTextBox.Top = 56;
        _pathTextBox.Width = 548;
        _pathTextBox.Height = 30;
        _pathTextBox.Text = InstallerPaths.ResolveInstallDirectory(_startupOptions.InstallDirectoryOverride);
        _pathTextBox.BackColor = Color.FromArgb(245, 247, 250);
        _pathTextBox.ForeColor = Color.Black;
        _pathTextBox.BorderStyle = BorderStyle.FixedSingle;

        _browseButton.Left = 586;
        _browseButton.Top = 54;
        _browseButton.Width = 116;
        _browseButton.Height = 34;
        _browseButton.Text = "Обзор...";
        _browseButton.FlatStyle = FlatStyle.Flat;
        _browseButton.FlatAppearance.BorderColor = Color.FromArgb(190, 200, 212);
        _browseButton.BackColor = Color.FromArgb(235, 240, 245);
        _browseButton.ForeColor = Color.FromArgb(40, 50, 65);
        _browseButton.Click += (_, _) => BrowseFolder();

        var hint = new Label
        {
            Left = 22,
            Top = 102,
            Width = 660,
            Height = 42,
            Text = "Можно выбрать D:, E: или любую другую папку. Внутри будет запущен официальный Velopack setup с этим путем.",
            ForeColor = Color.FromArgb(90, 105, 120),
            BackColor = Color.Transparent
        };

        _launchAfterInstallCheckBox.Left = 22;
        _launchAfterInstallCheckBox.Top = 158;
        _launchAfterInstallCheckBox.Width = 330;
        _launchAfterInstallCheckBox.Height = 26;
        _launchAfterInstallCheckBox.Text = "Запустить лаунчер после установки";
        _launchAfterInstallCheckBox.Checked = _startupOptions.LaunchAfterInstall;
        _launchAfterInstallCheckBox.ForeColor = Color.FromArgb(30, 30, 30);
        _launchAfterInstallCheckBox.BackColor = Color.Transparent;

        bodyPanel.Controls.Add(pathLabel);
        bodyPanel.Controls.Add(_pathTextBox);
        bodyPanel.Controls.Add(_browseButton);
        bodyPanel.Controls.Add(hint);
        bodyPanel.Controls.Add(_launchAfterInstallCheckBox);

        _progressBar.Left = 28;
        _progressBar.Top = 418;
        _progressBar.Width = 724;
        _progressBar.Height = 16;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Style = ProgressBarStyle.Continuous;

        _statusLabel.Left = 28;
        _statusLabel.Top = 444;
        _statusLabel.Width = 430;
        _statusLabel.Height = 24;
        _statusLabel.Text = "Готово к установке.";
        _statusLabel.ForeColor = Color.FromArgb(80, 90, 100);

        _installButton.Left = 526;
        _installButton.Top = 438;
        _installButton.Width = 106;
        _installButton.Height = 34;
        _installButton.Text = "Установить";
        _installButton.FlatStyle = FlatStyle.Flat;
        _installButton.FlatAppearance.BorderColor = Color.FromArgb(20, 100, 170);
        _installButton.BackColor = Color.FromArgb(30, 125, 200);
        _installButton.ForeColor = Color.White;
        _installButton.Click += async (_, _) => await InstallAsync();

        _cancelButton.Left = 646;
        _cancelButton.Top = 438;
        _cancelButton.Width = 106;
        _cancelButton.Height = 34;
        _cancelButton.Text = "Отмена";
        _cancelButton.FlatStyle = FlatStyle.Flat;
        _cancelButton.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 212);
        _cancelButton.BackColor = Color.FromArgb(235, 238, 242);
        _cancelButton.ForeColor = Color.FromArgb(50, 50, 50);
        _cancelButton.Click += (_, _) => Close();

        AcceptButton = _installButton;
        CancelButton = _cancelButton;

        Controls.Add(heroPanel);
        Controls.Add(bodyPanel);
        Controls.Add(_progressBar);
        Controls.Add(_statusLabel);
        Controls.Add(_installButton);
        Controls.Add(_cancelButton);

        ResumeLayout(performLayout: false);
    }

    private void BrowseFolder()
    {
        using var folderBrowserDialog = new FolderBrowserDialog
        {
            Description = "Выбери папку для установки Vesper Launcher",
            UseDescriptionForTitle = true,
            SelectedPath = _pathTextBox.Text
        };

        if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
        {
            var path = folderBrowserDialog.SelectedPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var root = Path.GetPathRoot(full);
                    if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.Combine(full, "Vesper Launcher");
                    }
                }
                catch
                {
                    // Ignore path formatting errors.
                }
            }
            _pathTextBox.Text = path;
        }
    }

    private async Task InstallAsync()
    {
        var installPath = _pathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(this, "Укажи папку установки.", "Нет пути", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            return;
        }

        try
        {
            var full = Path.GetFullPath(installPath.Trim('"'));
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                full = Path.Combine(full, "Vesper Launcher");
            }
            installPath = full;
            _pathTextBox.Text = installPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Некорректный путь установки: " + ex.Message, "Ошибка пути", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetUiEnabled(false);
        try
        {
            var options = _startupOptions with
            {
                InstallDirectoryOverride = installPath,
                LaunchAfterInstall = _launchAfterInstallCheckBox.Checked
            };

            var installResult = await Task.Run(() => InstallerOperations.Install(options, ReportProgress));
            _statusLabel.Text = "Установка завершена.";
            _progressBar.Value = 100;

            if (options.LaunchAfterInstall)
            {
                InstallerOperations.TryLaunch(installResult.ExecutablePath);
            }

            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Ошибка установки.";
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetUiEnabled(true);
        }
    }

    private void ReportProgress(InstallProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            Invoke(() => ReportProgress(progress));
            return;
        }

        _statusLabel.Text = progress.Status;
        _progressBar.Value = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, progress.Percent));
    }

    private void SetUiEnabled(bool enabled)
    {
        _pathTextBox.Enabled = enabled;
        _browseButton.Enabled = enabled;
        _cancelButton.Enabled = enabled;
        _launchAfterInstallCheckBox.Enabled = enabled;
        _installButton.Enabled = enabled;
    }

    private sealed class GradientPanel : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color StartColor { get; init; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color EndColor { get; init; }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var brush = new LinearGradientBrush(ClientRectangle, StartColor, EndColor, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(brush, ClientRectangle);
            base.OnPaint(e);
        }
    }
}
