using System;
using System.ComponentModel;
using System.Windows;

namespace CompilePalX;

/// <summary>
/// Interaction logic for GameConfigurationWindow.xaml
/// </summary>
public partial class GameConfigurationWindow
{
    private static GameConfigurationWindow? instance;
    public static GameConfigurationWindow Instance => instance ??= new GameConfigurationWindow();
    private int? index;

    private GameConfigurationWindow(GameConfiguration? gc = null)
    {
        InitializeComponent();
        gc ??= new GameConfiguration();
        this.DataContext = gc;
    }

    public void Open(GameConfiguration? gc = null, int? index = null)
    {
        gc ??= new GameConfiguration();
        this.DataContext = gc;
        this.index = index;
        Show();
        Focus();
    }


    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var config = (GameConfiguration)this.DataContext;
        bool editedLoadedGame = false;

        // TODO: validate
        // if index is not null, this is an edit
        if (this.index != null)
        {
            var replaced = GameConfigurationManager.GameConfigurations[(int)this.index];
            GameConfigurationManager.GameConfigurations[(int)this.index] = config;
            TelemetryManager.ModifyGameConfiguration(config.Name);

            // This window edits a clone, so saving leaves the list holding a different object than the
            // one the app is running against. Editing the loaded game therefore changed nothing until
            // the user went back to the selector and relaunched it - and now that the game can be
            // edited straight from the main window, there is no relaunch to accidentally fix it.
            // Reference equality on purpose: two configurations can compare equal by value.
            if (ReferenceEquals(GameConfigurationManager.GameConfiguration, replaced))
            {
                GameConfigurationManager.GameConfiguration = config;
                editedLoadedGame = true;
            }
        }
        else
        {
            GameConfigurationManager.GameConfigurations.Add(config);
            TelemetryManager.NewGameConfiguration(config.Name);
        }

        GameConfigurationManager.SaveGameConfigurations();

        // compiler/executable paths may have just changed; drop cached tools++/launcher resolutions so
        // they're recomputed against the saved config instead of silently reusing stale ones
        ToolsPlusPlusDetector.Invalidate();
        GameExeResolver.Invalidate();

        // after the caches are dropped: this rebuilds the parameter lists, which consult them
        if (editedLoadedGame)
            MainWindow.Instance?.LoadGameConfiguration(config);

        LaunchWindow.Instance?.RefreshGameConfigurationList();
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        instance = null;
        base.OnClosing(e);
    }
}
