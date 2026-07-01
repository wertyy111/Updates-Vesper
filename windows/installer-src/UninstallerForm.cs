using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class UninstallerForm : Form
{
    private readonly InstallerOptions _startupOptions;
    private readonly TextBox _pathTextBox = new();
    private readonly Button _browseButton = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _uninstallButton = new();
    private readonly Button _cancelButton = new();

    public UninstallerForm(InstallerOptions startupOptions)
    {
        _startupOptions = startupOptions with
        {
            RunUninstall = true,
            CloseApplications = true
        };

        SuspendLayout();

        Text = "Vesper Launcher Uninstall";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 320);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(243, 245, 248);
        ForeColor = Color.FromArgb(30, 30, 30);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);

        var title = new Label
        {
            Left = 28,
            Top = 22,
            Width = 500,
            Height = 34,
            Text = "Удаление Vesper Launcher",
            Font = new Font("Segoe UI Semibold", 17f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 25, 35)
        };

        var description = new Label
        {
            Left = 30,
            Top = 64,
            Width = 500,
            Height = 44,
            Text = "Укажи папку установки Vesper Launcher для выполнения штатного удаления Velopack.",
            ForeColor = Color.FromArgb(80, 95, 110)
        };

        var pathLabel = new Label
        {
            Left = 30,
            Top = 114,
            Width = 500,
            Height = 20,
            Text = "Папка для удаления",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 50, 65)
        };

        _pathTextBox.Left = 30;
        _pathTextBox.Top = 138;
        _pathTextBox.Width = 370;
        _pathTextBox.Height = 26;
        _pathTextBox.Text = InstallerPaths.ResolveInstallDirectoryForUninstall(_startupOptions.InstallDirectoryOverride);
        _pathTextBox.BackColor = Color.White;
        _pathTextBox.ForeColor = Color.Black;
        _pathTextBox.BorderStyle = BorderStyle.FixedSingle;

        _browseButton.Left = 412;
        _browseButton.Top = 135;
        _browseButton.Width = 118;
        _browseButton.Height = 30;
        _browseButton.Text = "Обзор...";
        _browseButton.FlatStyle = FlatStyle.Flat;
        _browseButton.FlatAppearance.BorderColor = Color.FromArgb(190, 200, 212);
        _browseButton.BackColor = Color.FromArgb(235, 240, 245);
        _browseButton.ForeColor = Color.FromArgb(40, 50, 65);
        _browseButton.Click += (_, _) => BrowseFolder();

        _progressBar.Left = 30;
        _progressBar.Top = 196;
        _progressBar.Width = 500;
        _progressBar.Height = 16;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Style = ProgressBarStyle.Continuous;

        _statusLabel.Left = 30;
        _statusLabel.Top = 222;
        _statusLabel.Width = 310;
        _statusLabel.Height = 22;
        _statusLabel.Text = "Готово к удалению.";
        _statusLabel.ForeColor = Color.FromArgb(80, 90, 100);

        _uninstallButton.Left = 312;
        _uninstallButton.Top = 258;
        _uninstallButton.Width = 104;
        _uninstallButton.Height = 32;
        _uninstallButton.Text = "Удалить";
        _uninstallButton.FlatStyle = FlatStyle.Flat;
        _uninstallButton.FlatAppearance.BorderColor = Color.FromArgb(180, 40, 55);
        _uninstallButton.BackColor = Color.FromArgb(220, 53, 69);
        _uninstallButton.ForeColor = Color.White;
        _uninstallButton.Click += async (_, _) => await UninstallAsync();

        _cancelButton.Left = 426;
        _cancelButton.Top = 258;
        _cancelButton.Width = 104;
        _cancelButton.Height = 32;
        _cancelButton.Text = "Отмена";
        _cancelButton.FlatStyle = FlatStyle.Flat;
        _cancelButton.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 212);
        _cancelButton.BackColor = Color.FromArgb(235, 238, 242);
        _cancelButton.ForeColor = Color.FromArgb(50, 50, 50);
        _cancelButton.Click += (_, _) => Close();

        AcceptButton = _uninstallButton;
        CancelButton = _cancelButton;

        Controls.Add(title);
        Controls.Add(description);
        Controls.Add(pathLabel);
        Controls.Add(_pathTextBox);
        Controls.Add(_browseButton);
        Controls.Add(_progressBar);
        Controls.Add(_statusLabel);
        Controls.Add(_uninstallButton);
        Controls.Add(_cancelButton);

        ResumeLayout(performLayout: false);
    }

    private void BrowseFolder()
    {
        using var folderBrowserDialog = new FolderBrowserDialog
        {
            Description = "Выбери папку установки Vesper Launcher для удаления",
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

    private async Task UninstallAsync()
    {
        var installPath = _pathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(this, "Укажи папку для удаления.", "Нет пути", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
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
            MessageBox.Show(this, "Некорректный путь: " + ex.Message, "Ошибка пути", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (MessageBox.Show(this, $"Удалить Vesper Launcher из папки \"{installPath}\"?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        SetUiEnabled(false);
        try
        {
            var options = _startupOptions with
            {
                InstallDirectoryOverride = installPath
            };

            await Task.Run(() => InstallerOperations.Uninstall(options, ReportProgress));
            _statusLabel.Text = "Удаление завершено.";
            _progressBar.Value = 100;
            MessageBox.Show(this, "Удаление завершено.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Ошибка удаления.";
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
        _uninstallButton.Enabled = enabled;
        _cancelButton.Enabled = enabled;
    }
}
