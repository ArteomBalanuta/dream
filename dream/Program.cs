public static class Program
{
    public static void Main(string[] args)
    {
        StartupOptions options = StartupOptions.Parse(args);

        using (var game = new Game(options))
        {
            game.Run();
        }
    }
}
