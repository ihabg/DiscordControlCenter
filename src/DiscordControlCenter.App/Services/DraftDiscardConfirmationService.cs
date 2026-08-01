using System.Threading;
using System.Windows;
using DiscordControlCenter.App.Views;

namespace DiscordControlCenter.App.Services;

public sealed class DraftDiscardConfirmationService : IDraftDiscardConfirmationService
{
    private static int _dialogOpen;

    public bool ConfirmDiscard(string actionDescription)
    {
        if (Interlocked.Exchange(ref _dialogOpen, 1) != 0) return false;
        try
        {
            var dialog = new DraftDiscardConfirmationWindow(actionDescription)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            return dialog.ShowDialog() == true;
        }
        finally { Volatile.Write(ref _dialogOpen, 0); }
    }
}
