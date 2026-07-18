namespace TaxonomyFixtures;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public interface IContract
{
    void Execute();
}

public interface IDerivedContract : IContract { }

public interface ILeftContract : IContract { }
public interface IRightContract : IContract { }
public interface IDiamondContract : ILeftContract, IRightContract { }

public interface IAlphaContract
{
    void Execute();
}

public interface IBetaContract
{
    void Execute();
}

public interface IStaticContract
{
    static abstract int StaticMember();
}

public interface IDefaultContract
{
    void DefaultMember() { }
    static virtual int StaticVirtualMember() => 0;
}

public interface IShapeContract
{
    int Value { get; }
    int this[int index] { get; }
    event EventHandler? Changed;
}

public interface IDerivedShapeContract : IShapeContract { }

public partial record SampleRecord(string Name) : IDerivedContract, IStaticContract
{
    public required string Required { get; init; }
    public int this[int index] => index;
    public static int StaticMember() => 0;
    public void Execute() { }
    void IContract.Execute() { }
}

public record struct SampleRecordStruct(int Value);

public record ExplicitCopyRecord
{
    public ExplicitCopyRecord(ExplicitCopyRecord original) { }
}

public class PrimaryConstructorClass(int value)
{
    public int Value => value;
}

public struct PrimaryConstructorStruct(int value)
{
    public int Value => value;
}

public class Base
{
    protected virtual void ProtectedMember() { }
}

public sealed class Derived : Base
{
    protected sealed override void ProtectedMember() { }
}

public sealed class SealedBase
{
    protected void NotReachable() { }
    ~SealedBase() { }
}

public class StaticConstructorShape
{
    static StaticConstructorShape() { }
}

public class NestedReachability
{
    protected class ProtectedNested
    {
        public void ReachableMember() { }
    }

    protected internal class ProtectedInternalNested
    {
        public void ReachableMember() { }
    }

    private protected class PrivateProtectedNested
    {
        public void NotReachableMember() { }
    }
}

public sealed class DiamondImplementation : IDiamondContract
{
    public void Execute() { }
}

public sealed class MultiInterfaceImplementation : IAlphaContract, IBetaContract
{
    public void Execute() { }
}

public class ShapeBase
{
    public virtual int Value => 0;
    public virtual int this[int index] => index;
    public virtual event EventHandler? Changed;
}

public sealed class ShapeDerived : ShapeBase, IShapeContract
{
    public override int Value => 1;
    public override int this[int index] => index + 1;
    public override event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

public sealed class ExplicitShape : IShapeContract
{
    int IShapeContract.Value => 0;
    int IShapeContract.this[int index] => index;
    event EventHandler? IShapeContract.Changed
    {
        add { }
        remove { }
    }
}

public delegate int SampleDelegate(int value);

public ref struct SampleRefStruct<T>
{
    public T Value;
}

public static class SampleExtensions
{
    public static void Extend<T>(this T value) { }
}

public struct SampleStruct { }

public enum SampleEnum
{
    One
}

public class MemberShapes
{
    public const int Constant = 1;
    public int Field;
    public event EventHandler? Changed;
    public string Property { get; set; } = string.Empty;

    public MemberShapes() { }
    ~MemberShapes() { }
    public static MemberShapes operator +(MemberShapes left, MemberShapes right) => left;
    public static implicit operator int(MemberShapes value) => 0;
    public async Task AsyncMethod() => await Task.CompletedTask;
    public IEnumerable<int> IteratorMethod() { yield return 1; }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
