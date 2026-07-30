using System.Windows.Threading;

namespace DiscordControlCenter.App.Services;

public sealed class UiDispatcher(Dispatcher dispatcher)
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
    }
}
