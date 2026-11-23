using System.Reflection;
using HalconDotNet;

namespace HObjectMemory.Lifetime.Tests;

[TestClass]
public sealed class IHObjectHolderTests
{
    [TestMethod]
    public void HObjectHolder_PublicInstanceMembers_MatchIHObjectHolder()
    {
        Type implementation = typeof(HObjectHolder);
        Type contract = typeof(IHObjectHolder);

        Assert.IsTrue(contract.IsAssignableFrom(implementation));

        MethodInfo[] contractMethods = contract
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(IDisposable).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Distinct()
            .ToArray();

        int interfaceWrappers = contractMethods.Count(method => method.GetParameters().Any(parameter =>
            parameter.IsOut && parameter.ParameterType == typeof(HObjectWeakHandle<HObject>).MakeByRefType()));
        Assert.AreEqual(598, interfaceWrappers);

        string[] contractSignatures = contractMethods.Select(GetSignature).ToArray();
        string[] implementationSignatures = implementation
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(GetSignature)
            .ToArray();

        CollectionAssert.AreEquivalent(contractSignatures, implementationSignatures);
    }

    [TestMethod]
    public void InterfaceReference_PreservesCoreLifecycleContract()
    {
        TestHelpers.RequireHalconRuntime();
        IHObjectHolder holder = new HObjectHolder();

        holder.GenImageConst(out HObjectWeakHandle<HObject> image, "byte", 24, 24);

        Assert.IsFalse(holder.IsDisposed);
        Assert.AreEqual(1, holder.TrackedObjectCount);
        Assert.AreEqual(1, holder.GetStrongRefCount(image));

        using (HObjectHandle<HObject> strong = holder.Borrow(image))
        {
            Assert.AreEqual(2, holder.GetStrongRefCount(image));
            Assert.IsTrue(strong.Value.IsInitialized());
        }

        Assert.IsTrue(holder.TryBorrow(image, out HObjectHandle<HObject> promoted));
        promoted.Dispose();

        Assert.IsTrue(holder.Release(image));
        Assert.IsFalse(image.IsAlive);
        Assert.AreEqual(0, holder.TrackedObjectCount);

        holder.Dispose();
        Assert.IsTrue(holder.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => holder.GenImageConst(out _, "byte", 8, 8));
    }

    private static string GetSignature(MethodInfo method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
        {
            string modifier = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            return $"{modifier}{GetTypeName(parameter.ParameterType)} {parameter.Name}";
        }));

        return $"{GetTypeName(method.ReturnType)} {method.Name}({parameters})";
    }

    private static string GetTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return GetTypeName(type.GetElementType()!);
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int tickIndex = name.IndexOf('`');
        if (tickIndex >= 0)
        {
            name = name[..tickIndex];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(GetTypeName))}>";
    }
}
