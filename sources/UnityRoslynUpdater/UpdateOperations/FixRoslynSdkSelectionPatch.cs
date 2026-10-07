using System.Text.RegularExpressions;
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
/// highest version instead. Directories without a compiler (left behind by uninstalled
/// SDKs) sort last. It also replaces Unity's version regex: by default with one that
/// accepts prerelease SDKs (e.g. 11.0.100-rc.1.26425.128), matching UpdateSdkOperation,
/// which links the newest SDK including prereleases; or, when an SDK was requested with
/// --sdk, with one that only matches that SDK. The rest of the method (including its
/// exception-throwing fallback) is left untouched.
/// </summary>
internal sealed class FixRoslynSdkSelectionPatch : UnityPatch
{
    public const string JsonDiscriminator = "FixRoslynSdkSelection";

    private const string CompareMethodName = "__RoslynUpdater_CompareByVersionDescending";
    private const string GetVersionKeyMethodName = "__RoslynUpdater_GetVersionKey";
    private const string SdkVersionPattern = @"^[1-9][0-9]*\.[0-9]+\.[0-9]+(-.+)?$";

    public override bool Execute(UpdateContext context, ModuleDefinition module, TypeDefinition type)
    {
        // Unity resolves the path in more than one place (e.g. a static constructor
        // lambda and a domain-reload cleanup lambda), so every occurrence is patched.
        var targets = FindRoslynPathMethods(type).ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("Could not locate the Roslyn SDK path resolution method.");
            return false;
        }

        var pattern = context.RequestedSdkVersion is null
            ? SdkVersionPattern
            : $"^{Regex.Escape(context.Sdk.Name)}$";

        bool success = true;

        foreach (var target in targets)
            success &= PatchMethod(module, target, pattern);

        return success;
    }

    private static bool PatchMethod(ModuleDefinition module, MethodDefinition target, string pattern)
    {
        if (target.CilMethodBody is not { } body)
            return false;

        var instructions = body.Instructions;

        // Replace the pattern passed to 'new Regex(...)'. This is done on every run,
        // so re-running the tool with a different --sdk (or none) updates it.
        int regexIndex = instructions.ToList().FindIndex(i => i.OpCode.Code == CilCode.Newobj
            && i.Operand is IMethodDescriptor { Name.Value: ".ctor", DeclaringType.FullName: "System.Text.RegularExpressions.Regex" });

        if (regexIndex < 1 || instructions[regexIndex - 1].OpCode.Code != CilCode.Ldstr)
        {
            Console.WriteLine("Could not find the SDK version regex.");
            return false;
        }

        instructions[regexIndex - 1].Operand = pattern;

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

        // Reuse the NPath members Unity's own code calls: 'dir.FileName' and 'dir.ToString(SlashMode)'.
        var fileNameGetter = instructions
            .Select(i => i.Operand)
            .OfType<IMethodDescriptor>()
            .FirstOrDefault(m => m.Name == "get_FileName" && m.Signature is { HasThis: true, ParameterTypes.Count: 0 });

        if (fileNameGetter is null)
        {
            Console.WriteLine("Could not find NPath.FileName.");
            return false;
        }

        int toStringIndex = instructions.ToList().FindIndex(i => i.Operand is IMethodDescriptor
        {
            Name.Value: "ToString",
            Signature: { HasThis: true, ParameterTypes.Count: 1 }
        });

        if (toStringIndex < 1 || !instructions[toStringIndex - 1].IsLdcI4())
        {
            Console.WriteLine("Could not find NPath.ToString(SlashMode).");
            return false;
        }

        var toStringMethod = (IMethodDescriptor)instructions[toStringIndex].Operand!;
        var slashMode = instructions[toStringIndex - 1].GetLdcI4Constant();

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

        // The helpers are shared by every patched method in the same declaring type. Their bodies
        // are rebuilt on every run, so installations patched by older versions of this tool get updated.
        var getVersionKey = GetOrAddMethod(declaringType, GetVersionKeyMethodName, MethodSignature.CreateStatic(factory.Int32, [factory.String]), "path");
        var compareMethod = GetOrAddMethod(declaringType, CompareMethodName, MethodSignature.CreateStatic(factory.Int32, [elementTypeSig, elementTypeSig]), "a", "b");
        BuildGetVersionKey(getVersionKey, module);
        BuildCompare(module, compareMethod, fileNameGetter, toStringMethod, slashMode, getVersionKey);

        // Already patched by a previous run.
        if (instructions.Any(i => i.OpCode.Code == CilCode.Ldftn && i.Operand is IMethodDescriptor { Name.Value: CompareMethodName }))
            return true;

        // References are built against the module's own corlib rather than imported via
        // reflection, which would point them at this tool's runtime (and isn't AOT-safe).
        var corLibScope = factory.CorLibScope;
        var arrayType = new TypeReference(module, corLibScope, "System", "Array");
        var comparisonOpenType = new TypeReference(module, corLibScope, "System", "Comparison`1");

        // void Array.Sort<T>(T[] array, Comparison<T> comparison)
        var typeParameter = new GenericParameterSignature(GenericParameterType.Method, 0);
        var sortReference = new MemberReference(arrayType, "Sort", MethodSignature.CreateStatic(
            factory.Void,
            1,
            [new SzArrayTypeSignature(typeParameter), comparisonOpenType.MakeGenericInstanceType(isValueType: false, [typeParameter])]));
        var sortGeneric = sortReference.MakeGenericInstanceMethod([elementTypeSig]);

        var comparisonGeneric = comparisonOpenType.MakeGenericInstanceType(isValueType: false, [elementTypeSig]);
        var ctorSignature = MethodSignature.CreateInstance(factory.Void, [factory.Object, factory.IntPtr]);
        var ctorReference = new MemberReference(comparisonGeneric.ToTypeDefOrRef(), ".ctor", ctorSignature);

        var insertAt = stlocIndex + 1;
        instructions.Insert(insertAt + 0, CilOpCodes.Ldloc, dirsLocal);
        instructions.Insert(insertAt + 1, CilOpCodes.Ldnull);
        instructions.Insert(insertAt + 2, CilOpCodes.Ldftn, compareMethod);
        instructions.Insert(insertAt + 3, CilOpCodes.Newobj, ctorReference);
        instructions.Insert(insertAt + 4, CilOpCodes.Call, (IMethodDescriptor)sortGeneric);

        instructions.CalculateOffsets();
        instructions.OptimizeMacros();
        body.ComputeMaxStackOnBuild = true;

        return true;
    }

    private static MethodDefinition GetOrAddMethod(TypeDefinition type, string name, MethodSignature signature, params string[] parameterNames)
    {
        if (type.Methods.FirstOrDefault(m => m.Name == name) is { } existing)
            return existing;

        var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, signature);

        for (int i = 0; i < parameterNames.Length; i++)
            method.ParameterDefinitions.Add(new ParameterDefinition((ushort)(i + 1), parameterNames[i], default));

        type.Methods.Add(method);
        return method;
    }

    private static IEnumerable<MethodDefinition> FindRoslynPathMethods(TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody?.Instructions.Any(i => i.OpCode.Code == CilCode.Ldstr && (string?)i.Operand == "Roslyn/bincore") == true)
                yield return method;
        }

        foreach (var nested in type.NestedTypes)
        {
            foreach (var method in FindRoslynPathMethods(nested))
                yield return method;
        }
    }

    private static bool IsStoreLocal(CilCode code) =>
        code is CilCode.Stloc or CilCode.Stloc_S or CilCode.Stloc_0 or CilCode.Stloc_1 or CilCode.Stloc_2 or CilCode.Stloc_3;

    // private static int __RoslynUpdater_GetVersionKey(string path)
    // {
    //     // Uninstalling an SDK can leave its (empty) directory behind, which must sort last.
    //     if (!File.Exists(Path.Combine(path, "Roslyn/bincore/csc.dll"))) return -1;
    //     string name = Path.GetFileName(path);
    //     int dash = name.IndexOf('-');
    //     string core = name;
    //     if (dash >= 0) core = name.Substring(0, dash);
    //     string[] parts = core.Split('.');
    //     if (parts.Length != 3) return -1;
    //     if (!int.TryParse(parts[0], out int major)) return -1;
    //     if (!int.TryParse(parts[1], out int minor)) return -1;
    //     if (!int.TryParse(parts[2], out int patch)) return -1;
    //     // A prerelease ranks just below the release with the same version number.
    //     return (major * 1_000_000 + minor * 1_000 + patch) * 2 + (dash < 0 ? 1 : 0);
    // }
    private static void BuildGetVersionKey(MethodDefinition method, ModuleDefinition module)
    {
        var factory = module.CorLibTypeFactory;
        var corLibScope = factory.CorLibScope;
        method.CilMethodBody = new CilMethodBody();
        var body = method.CilMethodBody;

        var nameLocal = new CilLocalVariable(factory.String);
        var dashLocal = new CilLocalVariable(factory.Int32);
        var coreLocal = new CilLocalVariable(factory.String);
        var partsLocal = new CilLocalVariable(new SzArrayTypeSignature(factory.String));
        var majorLocal = new CilLocalVariable(factory.Int32);
        var minorLocal = new CilLocalVariable(factory.Int32);
        var patchLocal = new CilLocalVariable(factory.Int32);
        body.LocalVariables.Add(nameLocal);
        body.LocalVariables.Add(dashLocal);
        body.LocalVariables.Add(coreLocal);
        body.LocalVariables.Add(partsLocal);
        body.LocalVariables.Add(majorLocal);
        body.LocalVariables.Add(minorLocal);
        body.LocalVariables.Add(patchLocal);

        var pathType = new TypeReference(module, corLibScope, "System.IO", "Path");
        var fileType = new TypeReference(module, corLibScope, "System.IO", "File");
        var pathCombineMethod = new MemberReference(pathType, "Combine", MethodSignature.CreateStatic(factory.String, [factory.String, factory.String]));
        var pathGetFileNameMethod = new MemberReference(pathType, "GetFileName", MethodSignature.CreateStatic(factory.String, [factory.String]));
        var fileExistsMethod = new MemberReference(fileType, "Exists", MethodSignature.CreateStatic(factory.Boolean, [factory.String]));
        var indexOfMethod = new MemberReference(factory.String.Type, "IndexOf", MethodSignature.CreateInstance(factory.Int32, [factory.Char]));
        var substringMethod = new MemberReference(factory.String.Type, "Substring", MethodSignature.CreateInstance(factory.String, [factory.Int32, factory.Int32]));
        var splitMethod = new MemberReference(factory.String.Type, "Split", MethodSignature.CreateInstance(new SzArrayTypeSignature(factory.String), [new SzArrayTypeSignature(factory.Char)]));
        var intTryParseMethod = new MemberReference(factory.Int32.Type, "TryParse", MethodSignature.CreateStatic(factory.Boolean, [factory.String, new ByReferenceTypeSignature(factory.Int32)]));

        var il = body.Instructions;
        var failLabel = new CilInstructionLabel();
        var splitLabel = new CilInstructionLabel();

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldstr, "Roslyn/bincore/csc.dll");
        il.Add(CilOpCodes.Call, pathCombineMethod);
        il.Add(CilOpCodes.Call, fileExistsMethod);
        il.Add(CilOpCodes.Brfalse, failLabel);

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, pathGetFileNameMethod);
        il.Add(CilOpCodes.Stloc, nameLocal);

        il.Add(CilOpCodes.Ldloc, nameLocal);
        il.Add(CilOpCodes.Ldc_I4, (int)'-');
        il.Add(CilOpCodes.Callvirt, indexOfMethod);
        il.Add(CilOpCodes.Stloc, dashLocal);
        il.Add(CilOpCodes.Ldloc, nameLocal);
        il.Add(CilOpCodes.Stloc, coreLocal);

        il.Add(CilOpCodes.Ldloc, dashLocal);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Blt, splitLabel);
        il.Add(CilOpCodes.Ldloc, nameLocal);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Ldloc, dashLocal);
        il.Add(CilOpCodes.Callvirt, substringMethod);
        il.Add(CilOpCodes.Stloc, coreLocal);

        splitLabel.Instruction = il.Add(CilOpCodes.Ldloc, coreLocal);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Newarr, factory.Char.ToTypeDefOrRef());
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Ldc_I4, (int)'.');
        il.Add(CilOpCodes.Stelem_I2);
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
            il.Add(CilOpCodes.Call, intTryParseMethod);
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
        il.Add(CilOpCodes.Ldc_I4_2);
        il.Add(CilOpCodes.Mul);
        il.Add(CilOpCodes.Ldloc, dashLocal);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Clt);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Ret);

        failLabel.Instruction = il.Add(CilOpCodes.Ldc_I4_M1);
        il.Add(CilOpCodes.Ret);

        il.CalculateOffsets();
        il.OptimizeMacros();
        body.ComputeMaxStackOnBuild = true;
    }

    // private static int __RoslynUpdater_CompareByVersionDescending(NPath a, NPath b)
    // {
    //     int result = __RoslynUpdater_GetVersionKey(b.ToString(slashMode)) - __RoslynUpdater_GetVersionKey(a.ToString(slashMode));
    //     // Break ties between prereleases of the same version (e.g. rc.1 vs rc.2).
    //     return result != 0 ? result : string.CompareOrdinal(b.FileName, a.FileName);
    // }
    private static void BuildCompare(ModuleDefinition module, MethodDefinition method, IMethodDescriptor fileNameGetter, IMethodDescriptor toStringMethod, int slashMode, MethodDefinition getVersionKey)
    {
        var factory = module.CorLibTypeFactory;
        var compareOrdinalMethod = new MemberReference(factory.String.Type, "CompareOrdinal", MethodSignature.CreateStatic(factory.Int32, [factory.String, factory.String]));

        method.CilMethodBody = new CilMethodBody();
        var il = method.CilMethodBody.Instructions;
        var returnLabel = new CilInstructionLabel();

        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Ldc_I4, slashMode);
        il.Add(CilOpCodes.Callvirt, toStringMethod);
        il.Add(CilOpCodes.Call, getVersionKey);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldc_I4, slashMode);
        il.Add(CilOpCodes.Callvirt, toStringMethod);
        il.Add(CilOpCodes.Call, getVersionKey);
        il.Add(CilOpCodes.Sub);
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Brtrue, returnLabel);
        il.Add(CilOpCodes.Pop);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Callvirt, fileNameGetter);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Callvirt, fileNameGetter);
        il.Add(CilOpCodes.Call, compareOrdinalMethod);
        returnLabel.Instruction = il.Add(CilOpCodes.Ret);

        il.CalculateOffsets();
        il.OptimizeMacros();
        method.CilMethodBody.ComputeMaxStackOnBuild = true;
    }
}
