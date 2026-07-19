using SampleLibrary.Api;

namespace SampleApp;

public class AppType
{
    private readonly RootType root = new();

    public int Run()
    {
        return root.Value;
    }
}
