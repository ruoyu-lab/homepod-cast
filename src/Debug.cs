namespace HomePodCast;

static class Debug
{
    static readonly bool Enabled = Environment.GetEnvironmentVariable("HPC_DEBUG") == "1";

    public static void Log(string msg)
    {
        if (Enabled) Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}
