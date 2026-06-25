using System.Windows;
using System.Windows.Controls;
using VesperLauncher.Launcher;

namespace VesperLauncher;

public partial class MainWindow
{
    private static RoutedEventArgs CreatePhotinoCommandEventArgs()
    {
        return new RoutedEventArgs();
    }

    private void ExecuteLaunchCommandFromPhotino()
    {
        LaunchButton_OnClick(LaunchButton, CreatePhotinoCommandEventArgs());
    }

    private void OpenProfileFolderCommandFromPhotino()
    {
        OpenProfileFolderButton_OnClick(OpenProfileFolderButton, CreatePhotinoCommandEventArgs());
    }

    private void PickAccountAvatarCommandFromPhotino()
    {
        ChangeAvatarButton_OnClick(ChangeAvatarButton, CreatePhotinoCommandEventArgs());
    }

    private void OpenLauncherGameDirectoryCommandFromPhotino()
    {
        LauncherOpenGameDirectoryButton_OnClick(LauncherOpenGameDirectoryButton, CreatePhotinoCommandEventArgs());
    }

    private void ClearSkinSelectionCommandFromPhotino()
    {
        ClearSkinSelectionButton_OnClick(ClearSkinSelectionButton, CreatePhotinoCommandEventArgs());
    }

    private void ImportSkinCommandFromPhotino()
    {
        ImportSkinButton_OnClick(ImportSkinButton, CreatePhotinoCommandEventArgs());
    }

    private void OpenSkinsFolderCommandFromPhotino()
    {
        OpenSkinsFolderButton_OnClick(OpenSkinsFolderButton, CreatePhotinoCommandEventArgs());
    }

    private void ResetBackgroundCommandFromPhotino()
    {
        ApplyDefaultBackgroundButton_OnClick(ApplyDefaultBackgroundButton, CreatePhotinoCommandEventArgs());
    }

    private void OpenBackgroundsFolderCommandFromPhotino()
    {
        OpenBackgroundsFolderButton_OnClick(OpenBackgroundsFolderButton, CreatePhotinoCommandEventArgs());
    }

    private void InstallSelectedModsCommandFromPhotino()
    {
        InstallSelectedModsButton_OnClick(InstallSelectedModsButton, CreatePhotinoCommandEventArgs());
    }

    private void ClearSelectedModsCommandFromPhotino()
    {
        ClearSelectedModsButton_OnClick(ClearSelectedModsButton, CreatePhotinoCommandEventArgs());
    }

    private void OpenModsFolderCommandFromPhotino()
    {
        OpenModsFolderButton_OnClick(OpenModsFolderButton, CreatePhotinoCommandEventArgs());
    }

    private void ApplyLauncherSettingsToggleCommandFromPhotino(string? field, bool value)
    {
        switch (field?.Trim().ToLowerInvariant())
        {
            case "usesystemjava":
                UseSystemJavaToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(UseSystemJavaToggleButton, CreatePhotinoCommandEventArgs());
                break;
            case "showjvmargs":
                ShowJvmArgsToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(ShowJvmArgsToggleButton, CreatePhotinoCommandEventArgs());
                break;
            case "autooptimizememory":
                AutoOptimizeMemoryToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(AutoOptimizeMemoryToggleButton, CreatePhotinoCommandEventArgs());
                break;
            case "autominimizeonlaunch":
                AutoMinimizeLauncherToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(AutoMinimizeLauncherToggleButton, CreatePhotinoCommandEventArgs());
                break;
            case "restorelauncheraftergameexit":
                RestoreLauncherAfterGameExitToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(RestoreLauncherAfterGameExitToggleButton, CreatePhotinoCommandEventArgs());
                break;
            case "clicksoundenabled":
                LauncherClickSoundToggleButton.IsChecked = value;
                LauncherSettingsToggleButton_OnClick(LauncherClickSoundToggleButton, CreatePhotinoCommandEventArgs());
                break;
        }
    }

    private void ToggleFavoriteModCommandFromPhotino(RecommendedModCatalogItem item)
    {
        ToggleFavoriteModButton_OnClick(new Button { Tag = item }, CreatePhotinoCommandEventArgs());
    }

    private void InstallCatalogModCommandFromPhotino(RecommendedModCatalogItem item)
    {
        InstallCatalogModButton_OnClick(new Button { Tag = item }, CreatePhotinoCommandEventArgs());
    }

    private void ConnectToFriendCommandFromPhotino(CloudFriendListItem friend)
    {
        ConnectToFriendButton_OnClick(new Button { Tag = friend }, CreatePhotinoCommandEventArgs());
    }
}

