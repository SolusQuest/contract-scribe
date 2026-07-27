namespace RelationProfiles;

public class PublicBase
{
    public virtual void Override()
    {
    }
}

public class PublicOverride : PublicBase
{
    public override void Override()
    {
    }
}

internal class InternalOverride : PublicBase
{
    public override void Override()
    {
    }
}

public interface IRelationContract
{
    void Implicit();

    void Explicit();
}

public class PublicImplementation : IRelationContract
{
    public void Implicit()
    {
    }

    void IRelationContract.Explicit()
    {
    }
}

internal class InternalImplementation : IRelationContract
{
    public void Implicit()
    {
    }

    void IRelationContract.Explicit()
    {
    }
}

public interface IInheritedContract
{
    void Inherited();
}

public interface IPublicDerivedContract : IInheritedContract
{
}

internal interface IInternalDerivedContract : IInheritedContract
{
}
