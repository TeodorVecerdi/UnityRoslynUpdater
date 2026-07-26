using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace UnityRoslynUpdater;

/// <summary>
/// Unity's BeeScriptCompilation resolves the Roslyn compiler to use by scanning
/// Editor/Data/DotNetSdk/sdk for version-numbered directories and returning the FIRST
/// one that looks like a version. Since UpdateSdkOperation links DotNetSdk to the
/// entire system .NET install (which can contain several SDK versions), that "first
/// match" is not necessarily the newest one, and Unity can end up using an older
/// Roslyn/C# version than the one this tool just installed.
///
/// This patch sorts that directory array by parsed version (descending) right after
/// it is obtained, so the existing "return on first match" loop naturally picks the
/// highest version instead. It intentionally leaves the rest of the method (including
/// its exception-throwing fallback) untouched.
/// </summary>
internal sealed class FixRoslynSdkSelectionPatch : UnityPatch
{
    public const string JsonDiscriminator = "FixRoslynSdkSelection";

    public override bool Execute(ModuleDefinition module, TypeDefinition type)
    {
        var target = FindRoslynPathMethod(type);

        if (target?.CilMethodBody is not { } body)
        {
            Console.WriteLine("Could not locate the Roslyn SDK path resolution method.");
            return false;
        }

        var instructions = body.Instructions;

        // Find the local variable holding the NPath[] returned by NPath.Directories(...).
        CilLocalVariable? dirsLocal = null;
        TypeSignature? elementTypeSig = null;

        foreach (var local in body.LocalVariables)
        {
            if (local.VariableType is SzArrayTypeSignature arraySig)
            {
                dirsLocal = local;
                elementTypeSig = arraySig.BaseType;
                break;
            }
        }

        if (dirsLocal is null || elementTypeSig is null)
        {
            Console.WriteLine("Could not find the SDK directory array local variable.");
            return false;
        }

        var elementType = elementTypeSig.Resolve();
        if (elementType is null)
        {
            Console.WriteLine("Could not resolve the NPath type.");
            return false;
        }

        var fileNameGetter = elementType.Methods.FirstOrDefault(m => m.Name == "get_FileName" && !m.IsStatic && m.Parameters.Count == 0);
        if (fileNameGetter is null)
        {
            Console.WriteLine("Could not find NPath.FileName.");
            return false;
        }

        bool hasDirectoriesCall = instructions.Any(i => i.Operand is IMethodDescriptor { Name.Value: "Directories" });
        if (!hasDirectoriesCall)
        {
            Console.WriteLine("Could not find a call to NPath.Directories.");
            return false;
        }

        int stlocIndex = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (IsStoreLocal(instructions[i].OpCode.Code) && instructions[i].GetLocalVariable(body.LocalVariables) == dirsLocal)
            {
                stlocIndex = i;
                break;
            }
        }

        if (stlocIndex < 0)
        {
            Console.WriteLine("Could not find the store instruction for the SDK directory array.");
            return false;
        }

        var declaringType = target.DeclaringType!;
        var factory = module.CorLibTypeFactory;

        var intTryParse = module.DefaultImporter.ImportMethod(
            typeof(int).GetMethod(nameof(int.TryParse), [typeof(string), typeof(int).MakeByRefType()])!);

        var getVersionKey = new MethodDefinition(
            "__RoslynUpdater_GetVersionKey",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(factory.Int32, factory.String));
        getVersionKey.ParameterDefinitions.Add(new ParameterDefinition(1, "name", default));
        declaringType.Methods.Add(getVersionKey);
        BuildGetVersionKey(getVersionKey, module, intTryParse);

        var compareMethod = new MethodDefinition(
            "__RoslynUpdater_CompareByVersionDescending",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodSignature.CreateStatic(factory.Int32, elementTypeSig, elementTypeSig));
        compareMethod.ParameterDefinitions.Add(new ParameterDefinition(1, "a", default));
        compareMethod.ParameterDefinitions.Add(new ParameterDefinition(2, "b", default));
        declaringType.Methods.Add(compareMethod);
        BuildCompare(compareMethod, fileNameGetter, getVersionKey);

        var sortMethodInfo = typeof(Array).GetMethods()
            .First(m => m.Name == nameof(Array.Sort)
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.Name.StartsWith("Comparison"));
        var sortImported = (IMethodDefOrRef)module.DefaultImporter.ImportMethod(sortMethodInfo);
        var sortGeneric = sortImported.MakeGenericInstanceMethod(elementTypeSig);

        var comparisonOpenType = module.DefaultImporter.ImportType(typeof(Comparison<>));
        var comparisonGeneric = comparisonOpenType.MakeGenericInstanceType(elementTypeSig);
        var ctorSignature = MethodSignature.CreateInstance(factory.Void, factory.Object, factory.IntPtr);
        var ctorReference = new MemberReference(comparisonGeneric.ToTypeDefOrRef(), ".ctor", ctorSignature);
        var ctorImported = (IMethodDefOrRef)module.DefaultImporter.ImportMethod(ctorReference);

        var insertAt = stlocIndex + 1;
        instructions.Insert(insertAt + 0, CilOpCodes.Ldloc, dirsLocal);
        instructions.Insert(insertAt + 1, CilOpCodes.Ldnull);
        instructions.Insert(insertAt + 2, CilOpCodes.Ldftn, compareMethod);
        instructions.Insert(insertAt + 3, CilOpCodes.Newobj, ctorImported);
        instructions.Insert(insertAt + 4, CilOpCodes.Call, (IMethodDescriptor)sortGeneric);

        instructions.CalculateOffsets();
        instructions.OptimizeMacros();
        body.ComputeMaxStackOnBuild = true;

        return true;
    }

    private static MethodDefinition? FindRoslynPathMethod(TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody?.Instructions.Any(i => i.OpCode.Code == CilCode.Ldstr && (string?)i.Operand == "Roslyn/bincore") == true)
                return method;
        }

        foreach (var nested in type.NestedTypes)
        {
            if (FindRoslynPathMethod(nested) is { } found)
                return found;
        }

        return null;
    }

    private static bool IsStoreLocal(CilCode code) =>
        code is CilCode.Stloc or CilCode.Stloc_S or CilCode.Stloc_0 or CilCode.Stloc_1 or CilCode.Stloc_2 or CilCode.Stloc_3;

    // private static int __RoslynUpdater_GetVersionKey(string name)
    // {
    //     string[] parts = name.Split('.');
    //     if (parts.Length != 3) return -1;
    //     if (!int.TryParse(parts[0], out int major)) return -1;
    //     if (!int.TryParse(parts[1], out int minor)) return -1;
    //     if (!int.TryParse(parts[2], out int patch)) return -1;
    //     return major * 1_000_000 + minor * 1_000 + patch;
    // }
    private static void BuildGetVersionKey(MethodDefinition method, ModuleDefinition module, IMethodDescriptor intTryParse)
    {
        var factory = module.CorLibTypeFactory;
        method.CilMethodBody = new CilMethodBody();
        var body = method.CilMethodBody;

        var partsLocal = new CilLocalVariable(new SzArrayTypeSignature(factory.String));
        var majorLocal = new CilLocalVariable(factory.Int32);
        var minorLocal = new CilLocalVariable(factory.Int32);
        var patchLocal = new CilLocalVariable(factory.Int32);
        body.LocalVariables.Add(partsLocal);
        body.LocalVariables.Add(majorLocal);
        body.LocalVariables.Add(minorLocal);
        body.LocalVariables.Add(patchLocal);

        var il = body.Instructions;
        var failLabel = new CilInstructionLabel();

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Newarr, factory.Char.ToTypeDefOrRef());
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Ldc_I4, (int)'.');
        il.Add(CilOpCodes.Stelem_I2);
        var splitMethod = module.DefaultImporter.ImportMethod(typeof(string).GetMethod(nameof(string.Split), [typeof(char[])])!);
        il.Add(CilOpCodes.Callvirt, splitMethod);
        il.Add(CilOpCodes.Stloc, partsLocal);

        il.Add(CilOpCodes.Ldloc, partsLocal);
        il.Add(CilOpCodes.Ldlen);
        il.Add(CilOpCodes.Conv_I4);
        il.Add(CilOpCodes.Ldc_I4_3);
        il.Add(CilOpCodes.Bne_Un, failLabel);

        void EmitTryParseComponent(int index, CilLocalVariable local)
        {
            il.Add(CilOpCodes.Ldloc, partsLocal);
            il.Add(CilOpCodes.Ldc_I4, index);
            il.Add(CilOpCodes.Ldelem_Ref);
            il.Add(CilOpCodes.Ldloca, local);
            il.Add(CilOpCodes.Call, intTryParse);
            il.Add(CilOpCodes.Brfalse, failLabel);
        }

        EmitTryParseComponent(0, majorLocal);
        EmitTryParseComponent(1, minorLocal);
        EmitTryParseComponent(2, patchLocal);

        il.Add(CilOpCodes.Ldloc, majorLocal);
        il.Add(CilOpCodes.Ldc_I4, 1_000_000);
        il.Add(CilOpCodes.Mul);
        il.Add(CilOpCodes.Ldloc, minorLocal);
        il.Add(CilOpCodes.Ldc_I4, 1_000);
        il.Add(CilOpCodes.Mul);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Ldloc, patchLocal);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Ret);

        var failInstr = il.Add(CilOpCodes.Ldc_I4_M1);
        failLabel.Instruction = failInstr;
        il.Add(CilOpCodes.Ret);

        il.CalculateOffsets();
        il.OptimizeMacros();
        body.ComputeMaxStackOnBuild = true;
    }

    // private static int __RoslynUpdater_CompareByVersionDescending(NPath a, NPath b)
    //     => __RoslynUpdater_GetVersionKey(b.FileName) - __RoslynUpdater_GetVersionKey(a.FileName);
    private static void BuildCompare(MethodDefinition method, MethodDefinition fileNameGetter, MethodDefinition getVersionKey)
    {
        method.CilMethodBody = new CilMethodBody();
        var il = method.CilMethodBody.Instructions;

        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Callvirt, fileNameGetter);
        il.Add(CilOpCodes.Call, getVersionKey);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Callvirt, fileNameGetter);
        il.Add(CilOpCodes.Call, getVersionKey);
        il.Add(CilOpCodes.Sub);
        il.Add(CilOpCodes.Ret);

        il.CalculateOffsets();
        il.OptimizeMacros();
        method.CilMethodBody.ComputeMaxStackOnBuild = true;
    }
}
