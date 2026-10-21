namespace Fixture;

/// <summary>Provides synthetic observation operations.</summary>
public static partial class App
{
    public static void A() { }
    public static void B() { }

    /// <summary>Provides independent nested operations.</summary>
    public static class Nested
    {
        public static void Run() { }
        public static void Run(int value) { }
    }
}

public sealed class Metadata
{
    public int Value { get; set; }
}
