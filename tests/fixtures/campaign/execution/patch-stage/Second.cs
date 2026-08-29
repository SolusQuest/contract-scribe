namespace PatchStage;

/// <summary>Provides a deterministic cross-file Patch target.</summary>
public static class SecondFixture
{
    public static int Third(string value) => value.Length + 2;
}
