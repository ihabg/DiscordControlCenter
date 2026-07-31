namespace DiscordControlCenter.UiHarness;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new HarnessWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"The isolated UI harness could not create its test window. {exception.GetType().Name}: {exception.Message}",
                "UI harness startup failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
