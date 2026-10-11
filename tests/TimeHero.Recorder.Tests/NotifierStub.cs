namespace TimeHero.App;
public static class Notifier {
  public static List<string> Sent = new();
  public static void Init(Action<string> a) {}
  public static void Reminder(string t, string b) {}
  public static void Info(string t, string b) { lock (Sent) Sent.Add(t + ": " + b); }
}
