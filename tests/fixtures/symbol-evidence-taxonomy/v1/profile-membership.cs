file class FileCase
{
}

internal class InternalCase
{
}

public class PublicCase
{
}

public class DerivableContainer
{
    private void PrivateCase()
    {
    }

    private protected void PrivateProtectedCase()
    {
    }

    protected void ProtectedCase()
    {
    }

    protected internal void ProtectedInternalCase()
    {
    }

    private class PrivateContainer
    {
        public void NestedPrivateContainerCase()
        {
        }
    }
}

public sealed class SealedContainer
{
    protected void SealedProtectedCase()
    {
    }
}

class ImplicitInternalCase
{
    void ImplicitPrivateCase()
    {
    }
}
