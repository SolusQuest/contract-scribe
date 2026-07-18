namespace TaxonomyFixtures;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public interface IContract
{
    void Execute();
}

public interface IDerivedContract : IContract { }

public interface IStaticContract
{
    static abstract int StaticMember();
}

public partial record SampleRecord(string Name) : IDerivedContract, IStaticContract
{
    public required string Required { get; init; }
    public int this[int index] => index;
    public static int StaticMember() => 0;
    public void Execute() { }
    void IContract.Execute() { }
}

public record struct SampleRecordStruct(int Value);

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
