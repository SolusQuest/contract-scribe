namespace EndToEnd;

public class BaseFixture
{
    public virtual void Run()
    {
    }
}

public sealed class Fixture : BaseFixture
{
    public override void Run()
    {
    }
}
