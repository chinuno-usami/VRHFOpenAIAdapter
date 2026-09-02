using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchAssembly
{
    private const string WebClientTypeName = "HandsFrameClient.WebClient";
    private const string BridgeTypeName = "VRHF.OpenAIAdapter.Bridge";

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PatchAssembly <Assembly-CSharp.dll> <VRHF.OpenAIAdapter.dll>");
            return 2;
        }

        string targetPath = Path.GetFullPath(args[0]);
        string adapterPath = Path.GetFullPath(args[1]);
        string backupPath = targetPath + ".vrhf-original";
        string temporaryPath = targetPath + ".vrhf-patched";

        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(targetPath));
        resolver.AddSearchDirectory(Path.GetDirectoryName(adapterPath));
        ReaderParameters reader = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false, InMemory = true };
        bool changed;

        using (AssemblyDefinition target = AssemblyDefinition.ReadAssembly(targetPath, reader))
        using (AssemblyDefinition adapter = AssemblyDefinition.ReadAssembly(adapterPath, reader))
        {
            TypeDefinition webClient = target.MainModule.Types.SingleOrDefault(t => t.FullName == WebClientTypeName);
            if (webClient == null) throw new InvalidOperationException(WebClientTypeName + " was not found.");
            TypeDefinition bridge = adapter.MainModule.Types.SingleOrDefault(t => t.FullName == BridgeTypeName);
            if (bridge == null) throw new InvalidOperationException(BridgeTypeName + " was not found.");

            MethodDefinition bridgeGetUrl = FindBridgeMethod(bridge, "GetUrl");
            MethodDefinition bridgeGetOcrUrl = FindBridgeMethod(bridge, "GetOcrUrl");
            MethodReference importedGetUrl = target.MainModule.ImportReference(bridgeGetUrl);
            MethodReference importedGetOcrUrl = target.MainModule.ImportReference(bridgeGetOcrUrl);

            bool translationChanged = PatchTranslationUrl(webClient, importedGetUrl);
            bool ocrChanged = PatchDriveOcr(webClient, importedGetOcrUrl);
            changed = translationChanged || ocrChanged;
            if (!changed)
            {
                Console.WriteLine("Assembly is already patched for translation and VLM OCR.");
                return 0;
            }

            if (!File.Exists(backupPath))
            {
                File.Copy(targetPath, backupPath, false);
                Console.WriteLine("Backup: " + backupPath);
            }

            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            target.Write(temporaryPath);
        }

        File.Delete(targetPath);
        File.Move(temporaryPath, targetPath);
        Console.WriteLine("Patched translation and VLM OCR: " + targetPath);
        return 0;
    }

    private static MethodDefinition FindBridgeMethod(TypeDefinition bridge, string name)
    {
        MethodDefinition method = bridge.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic && !m.HasParameters);
        if (method == null) throw new InvalidOperationException(BridgeTypeName + "." + name + " was not found.");
        return method;
    }

    private static bool PatchTranslationUrl(TypeDefinition webClient, MethodReference bridgeGetUrl)
    {
        MethodDefinition getUrl = webClient.Methods.SingleOrDefault(m => m.Name == "GetGASUrl" && !m.HasParameters);
        if (getUrl == null) throw new InvalidOperationException("GetGASUrl was not found.");
        if (CallsBridge(getUrl, "GetUrl")) return false;

        getUrl.Body.ExceptionHandlers.Clear();
        getUrl.Body.Variables.Clear();
        getUrl.Body.InitLocals = false;
        getUrl.Body.Instructions.Clear();
        ILProcessor il = getUrl.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Call, bridgeGetUrl));
        il.Append(il.Create(OpCodes.Ret));
        return true;
    }

    private static bool PatchDriveOcr(TypeDefinition webClient, MethodReference bridgeGetOcrUrl)
    {
        TypeDefinition stateMachine = webClient.NestedTypes.SingleOrDefault(t => t.Name == "<PostToDriveOCR>d__22");
        if (stateMachine == null) throw new InvalidOperationException("PostToDriveOCR state machine was not found.");
        MethodDefinition moveNext = stateMachine.Methods.SingleOrDefault(m => m.Name == "MoveNext");
        if (moveNext == null) throw new InvalidOperationException("PostToDriveOCR.MoveNext was not found.");
        bool changed = false;

        if (!CallsBridge(moveNext, "GetOcrUrl"))
        {
            Instruction[] urlLoads = moveNext.Body.Instructions.Where(i =>
            {
                FieldReference field = i.Operand as FieldReference;
                return i.OpCode == OpCodes.Ldsfld && field != null && field.Name == "DriveOCRUrl" &&
                    field.DeclaringType.FullName == "HandsFrameConstants";
            }).ToArray();
            if (urlLoads.Length != 1)
                throw new InvalidOperationException("Expected one DriveOCRUrl load, found " + urlLoads.Length + ".");
            urlLoads[0].OpCode = OpCodes.Call;
            urlLoads[0].Operand = bridgeGetOcrUrl;
            changed = true;
        }

        Instruction[] timeoutSetters = moveNext.Body.Instructions.Where(i =>
        {
            MethodReference called = i.Operand as MethodReference;
            return i.OpCode == OpCodes.Callvirt && called != null && called.Name == "set_timeout";
        }).ToArray();
        if (timeoutSetters.Length != 1)
            throw new InvalidOperationException("Expected one OCR timeout setter, found " + timeoutSetters.Length + ".");
        Instruction timeoutValue = timeoutSetters[0].Previous;
        FieldReference timeoutField = timeoutValue.Operand as FieldReference;
        if (timeoutValue.OpCode == OpCodes.Ldsfld && timeoutField != null && timeoutField.Name == "WebRequestTimeout")
        {
            timeoutValue.OpCode = OpCodes.Ldc_I4;
            timeoutValue.Operand = 300;
            changed = true;
        }
        else if (timeoutValue.OpCode != OpCodes.Ldc_I4 || !(timeoutValue.Operand is int) || (int)timeoutValue.Operand != 300)
            throw new InvalidOperationException("Unexpected PostToDriveOCR timeout IL.");

        return changed;
    }

    private static bool CallsBridge(MethodDefinition method, string methodName)
    {
        return method.Body.Instructions.Any(i =>
        {
            MethodReference called = i.Operand as MethodReference;
            return i.OpCode == OpCodes.Call && called != null && called.Name == methodName &&
                called.DeclaringType.FullName == BridgeTypeName;
        });
    }
}
