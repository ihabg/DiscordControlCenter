using System.Windows;
using System.Windows.Controls;

namespace DiscordControlCenter.App.Controls;

public static class DialogChrome
{
    public static readonly DependencyProperty CloseOnClickProperty =
        DependencyProperty.RegisterAttached(
            "CloseOnClick",
            typeof(bool),
            typeof(DialogChrome),
            new PropertyMetadata(false, OnCloseOnClickChanged));

    public static bool GetCloseOnClick(DependencyObject element) =>
        (bool)element.GetValue(CloseOnClickProperty);

    public static void SetCloseOnClick(DependencyObject element, bool value) =>
        element.SetValue(CloseOnClickProperty, value);

    private static void OnCloseOnClickChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not Button button)
        {
            return;
        }

        if ((bool)args.OldValue)
        {
            button.Click -= OnCloseClick;
        }

        if ((bool)args.NewValue)
        {
            button.Click += OnCloseClick;
        }
    }

    private static void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button button)
        {
            Window.GetWindow(button)?.Close();
        }
    }
}
