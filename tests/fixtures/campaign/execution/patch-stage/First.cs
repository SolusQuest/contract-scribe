namespace PatchStage;

/// <summary>Provides deterministic same-file Patch targets.</summary>
public static class FirstFixture
{
    public static int First(string value) => value.Length;

    public static int Second(string value) => value.Length + 1;
}
