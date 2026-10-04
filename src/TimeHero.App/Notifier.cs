using Microsoft.Toolkit.Uwp.Notifications;

namespace TimeHero.App;

/// <summary>Notifiche toast native di Windows.</summary>
public static class Notifier
{
    private static Action<string>? _onAction;

    /// <param name="onAction">Riceve il valore "action" dei pulsanti del toast ("pause", "finish", "open").</param>
    public static void Init(Action<string> onAction)
    {
        _onAction = onAction;
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("action", out var action))
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _onAction?.Invoke(action));
        };
    }

    /// <summary>Promemoria "attività in corso": sostituisce il precedente invece di accumularsi.</summary>
    public static void Reminder(string title, string body)
    {
        new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .AddButton(new ToastButton().SetContent("Pausa").AddArgument("action", "pause").SetBackgroundActivation())
            .AddButton(new ToastButton().SetContent("Fine").AddArgument("action", "finish").SetBackgroundActivation())
            .AddButton(new ToastButton().SetContent("Apri").AddArgument("action", "open"))
            .Show(t =>
            {
                t.Tag = "reminder";
                t.Group = "timehero";
                t.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });
    }

    public static void Info(string title, string body)
    {
        new ToastContentBuilder().AddText(title).AddText(body)
            .Show(t => { t.Tag = "info"; t.Group = "timehero"; t.ExpirationTime = DateTimeOffset.Now.AddMinutes(1); });
    }
}
