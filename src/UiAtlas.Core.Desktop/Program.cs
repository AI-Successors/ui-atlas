using System.Windows;

namespace UiAtlas.Core.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml")
        });
        var evidenceIndex = Array.IndexOf(args, "--evidence");
        var graphTokens = evidenceIndex >= 0 ? args[..evidenceIndex] : args;
        var evidenceTokens = evidenceIndex >= 0 && evidenceIndex + 1 < args.Length
            ? args[(evidenceIndex + 1)..]
            : [];
        var graphPath = JoinPathTokens(graphTokens);
        var evidencePath = JoinPathTokens(evidenceTokens);
        var window = new ExplorerWindow(graphPath, evidencePath);
        app.Run(window);
    }

    private static string? JoinPathTokens(ReadOnlySpan<string> tokens)
    {
        if (tokens.Length == 0) return null;
        var value = string.Join(" ", tokens.ToArray()).Trim();
        return value.Length == 0 ? null : value.Trim('"');
    }
}
