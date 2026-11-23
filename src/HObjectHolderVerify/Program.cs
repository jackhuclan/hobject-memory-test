using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HalconDotNet;
using HObjectMemory.Lifetime;

namespace HObjectHolderVerify;

internal static class Program
{
    private static int s_failures;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
        if (!condition)
        {
            s_failures++;
        }
    }

    private static int Main()
    {
        Check(VerifySignatures(), "598 个包装方法签名一致（返回类型/参数名/顺序/调用语义不变，仅 out HObject 替换为弱句柄）");

        VerifySingleOutputLifecycle();
        VerifyExceptionSafety();
        VerifyMultipleOutputs();
        VerifyExternalDispose();
        VerifyAfterHolderDispose();
        VerifyConcurrency();

        Console.WriteLine(s_failures == 0 ? "ALL PASS" : $"FAILED: {s_failures}");
        return s_failures == 0 ? 0 : 1;
    }

    private static void VerifySingleOutputLifecycle()
    {
        Console.WriteLine("\n-- 单输出生命周期 --");
        IHObjectHolder holder = new HObjectHolder();
        holder.GenImageConst(out HObjectWeakHandle<HObject> imageWeak, "byte", 32, 32);

        Check(imageWeak.IsAlive, "算子输出后弱句柄存活");
        Check(holder.TrackedObjectCount == 1, "容器持有一个强引用锚点");
        Check(holder.GetStrongRefCount(imageWeak) == 1, "初始强引用计数为 1");

        HObjectHandle<HObject> strong = holder.Borrow(imageWeak);
        Check(holder.GetStrongRefCount(imageWeak) == 2, "提升后强引用计数为 2");

        HObject rawImage = strong.Value;
        strong.Dispose();
        Check(holder.GetStrongRefCount(imageWeak) == 1, "释放提升句柄后回到 1");
        Check(imageWeak.IsAlive, "容器锚点仍保持对象存活");
        Check(rawImage.IsInitialized(), "底层 HObject 尚未释放");

        holder.Dispose();
        holder.Dispose();
        Check(holder.IsDisposed, "重复 Dispose 后容器状态为已释放");
        Check(holder.TrackedObjectCount == 0, "锚点全部清空");
        Check(!imageWeak.IsAlive, "容器释放后弱句柄失效");
        Check(holder.GetStrongRefCount(imageWeak) == 0, "控制块强引用计数归零");
        Check(!rawImage.IsInitialized(), "底层 HObject 被确定性释放");
        Check(!holder.TryBorrow(imageWeak, out HObjectHandle<HObject> expired) && expired.IsEmpty, "失效弱句柄不能提升");
    }

    private static void VerifyExceptionSafety()
    {
        Console.WriteLine("\n-- 异常安全 --");
        using IHObjectHolder holder = new HObjectHolder();
        HOperatorSet.GenImageConst(out HObject image, "byte", 16, 16);

        int before = holder.TrackedObjectCount;
        HObjectWeakHandle<HObject> badRegion = HObjectWeakHandle<HObject>.Empty;
        bool threw = false;
        try
        {
            holder.Threshold(image, out badRegion, new HTuple("bad_min_gray"), 128);
        }
        catch (HalconException)
        {
            threw = true;
        }

        Check(threw, "算子异常原样抛出");
        Check(!badRegion.IsAlive, "异常路径 out 参数被置为空弱句柄");
        Check(holder.TrackedObjectCount == before, "异常路径不登记锚点");
        image.Dispose();
    }

    private static void VerifyMultipleOutputs()
    {
        Console.WriteLine("\n-- 多 out HObject --");
        using IHObjectHolder holder = new HObjectHolder();
        holder.GenImageConst(out HObjectWeakHandle<HObject> source, "byte", 32, 32);
        using (HObjectHandle<HObject> sourceHandle = holder.Borrow(source))
        {
            holder.Watersheds(sourceHandle.Value, out HObjectWeakHandle<HObject> basins, out HObjectWeakHandle<HObject> watersheds);

            Check(holder.TrackedObjectCount == 3, "一次算子登记两个新锚点");
            Check(basins.IsAlive && watersheds.IsAlive, "两个弱句柄均存活");

            Check(holder.Release(basins), "Release 可提前释放单个锚点");
            Check(!basins.IsAlive, "被 Release 的对象立即释放");
            Check(watersheds.IsAlive, "另一个对象不受影响");
            Check(!holder.Release(basins), "重复 Release 返回 false");
        }
    }

    private static void VerifyExternalDispose()
    {
        Console.WriteLine("\n-- HObject 已被外部释放 --");
        using IHObjectHolder holder = new HObjectHolder();
        holder.GenImageConst(out HObjectWeakHandle<HObject> weak, "byte", 8, 8);
        HObjectHandle<HObject> strong = holder.Borrow(weak);

        strong.Value.Dispose();
        Check(!weak.IsAlive, "外部 Dispose 后弱句柄立即失效");
        Check(!holder.TryBorrow(weak, out HObjectHandle<HObject> promoted) && promoted.IsEmpty, "外部 Dispose 后不能提升出已释放对象");

        strong.Dispose();
        Check(!weak.IsAlive, "强句柄释放后仍保持失效状态");
    }

    private static void VerifyAfterHolderDispose()
    {
        Console.WriteLine("\n-- 容器释放后的调用 --");
        IHObjectHolder holder = new HObjectHolder();
        holder.Dispose();

        bool threw = false;
        try
        {
            holder.GenImageConst(out HObjectWeakHandle<HObject> weak, "byte", 8, 8);
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }

        Check(threw, "已释放容器调用包装方法抛出 ObjectDisposedException");
    }

    private static void VerifyConcurrency()
    {
        Console.WriteLine("\n-- 多线程访问 --");
        using IHObjectHolder holder = new HObjectHolder();
        HObjectWeakHandle<HObject>[] weaks = new HObjectWeakHandle<HObject>[64];

        Parallel.For(0, weaks.Length, i => holder.GenImageConst(out weaks[i], "byte", 8, 8));

        Check(holder.TrackedObjectCount == weaks.Length, "并发登记锚点数量正确");
        Check(weaks.All(w => w.IsAlive), "并发输出均存活");

        holder.Dispose();
        Check(weaks.All(w => !w.IsAlive), "并发登记的对象在容器释放后全部失效");
    }

    private static bool VerifySignatures()
    {
        string originalPath = Path.Combine(AppContext.BaseDirectory, "halcondotnetxl.dll");
        string wrapperPath = Path.Combine(AppContext.BaseDirectory, "HObjectMemory.Lifetime.dll");
        if (!File.Exists(originalPath) || !File.Exists(wrapperPath))
        {
            Console.WriteLine($"缺少签名比对所需程序集：{originalPath} / {wrapperPath}");
            return false;
        }

        List<string> original = ReadSignatures(originalPath, "HalconDotNet.HOperatorSet", isWrapper: false);
        List<string> wrapper = ReadSignatures(wrapperPath, "HObjectMemory.Lifetime.HObjectHolder", isWrapper: true);
        original.Sort(StringComparer.Ordinal);
        wrapper.Sort(StringComparer.Ordinal);

        Console.WriteLine($"原始方法 {original.Count} 个，包装方法 {wrapper.Count} 个");
        if (original.Count != wrapper.Count)
        {
            return false;
        }

        string[] mismatches = original.Where((signature, i) => signature != wrapper[i]).Take(5).ToArray();
        foreach (string mismatch in mismatches)
        {
            Console.WriteLine("  签名差异：" + mismatch);
        }

        return mismatches.Length == 0;
    }

    private static List<string> ReadSignatures(string path, string targetType, bool isWrapper)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader peReader = new(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        MetadataSignatureProvider provider = new();
        List<string> result = [];

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            if (GetFullName(reader, typeHandle) != targetType)
            {
                continue;
            }

            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                MethodSignature<string> signature = method.DecodeSignature(provider, genericContext: null);
                List<string> parameters = [];
                bool hasOutHObject = false;

                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    Parameter parameter = reader.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber == 0)
                    {
                        continue;
                    }

                    string name = reader.GetString(parameter.Name);
                    string rawType = signature.ParameterTypes[parameter.SequenceNumber - 1];
                    bool isOut = (parameter.Attributes & ParameterAttributes.Out) != 0;
                    bool isRef = rawType.EndsWith('&') && !isOut;

                    if (isWrapper && isOut && rawType == "HObjectMemory.Lifetime.HObjectWeakHandle`1<HalconDotNet.HObject>&")
                    {
                        rawType = "HalconDotNet.HObject&";
                    }

                    if (isOut && rawType == "HalconDotNet.HObject&")
                    {
                        hasOutHObject = true;
                    }

                    string modifier = isOut ? "out " : isRef ? "ref " : "";
                    parameters.Add($"{name}:{modifier}{rawType}");
                }

                if (hasOutHObject)
                {
                    result.Add($"{signature.ReturnType} {reader.GetString(method.Name)}({string.Join(", ", parameters)})");
                }
            }
        }

        return result;
    }

    private static string GetFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return GetFullName(reader, declaring) + "." + name;
        }

        string ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private sealed class MetadataSignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) => GetFullName(r, handle);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = r.GetTypeReference(handle);
            string name = r.GetString(type.Name);
            string ns = r.GetString(type.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        public string GetTypeFromSpecification(MetadataReader r, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => r.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static string GetFullName(MetadataReader r, TypeDefinitionHandle handle)
        {
            TypeDefinition type = r.GetTypeDefinition(handle);
            string name = r.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return GetFullName(r, declaring) + "." + name;
            }

            string ns = r.GetString(type.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
    }
}
