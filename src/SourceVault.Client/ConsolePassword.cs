namespace SourceVault.Client;

/// <summary>Asks for the operator's SourceVault password in a console window, without echoing it.</summary>
public static class ConsolePassword
{
    public static string? Ask()
    {
        Console.Write("SourceVault password: ");
        if (Console.IsInputRedirected) { var line = Console.ReadLine(); Console.WriteLine(); return string.IsNullOrEmpty(line) ? null : line; }
        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return typed.Length == 0 ? null : typed.ToString(); }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Backspace) { if (typed.Length > 0) typed.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && typed.Length < 256) typed.Append(key.KeyChar);
        }
    }
}
