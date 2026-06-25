public readonly record struct StartupOptions(bool PhysicsEnabled)
{
    public static StartupOptions Parse(string[] args)
    {
        bool physicsEnabled = !args.Contains("--disable-physics");
        return new StartupOptions(physicsEnabled);
    }
}
