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
        // TODO: validate
        // if index is not null, this is an edit
        if (this.index != null)
        {
            GameConfigurationManager.GameConfigurations[(int)this.index] = config;
            TelemetryManager.ModifyGameConfiguration(config.Name);
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

        LaunchWindow.Instance?.RefreshGameConfigurationList();
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        instance = null;
        base.OnClosing(e);
    }
}
