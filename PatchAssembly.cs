using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchAssembly
{
    private const string WebClientTypeName = "HandsFrameClient.WebClient";
    private const string VariableFrameTypeName = "VRHandsFrameVariableFrame";
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
            TypeDefinition variableFrame = target.MainModule.Types.SingleOrDefault(t => t.FullName == VariableFrameTypeName);
            if (variableFrame == null) throw new InvalidOperationException(VariableFrameTypeName + " was not found.");
            TypeDefinition bridge = adapter.MainModule.Types.SingleOrDefault(t => t.FullName == BridgeTypeName);
            if (bridge == null) throw new InvalidOperationException(BridgeTypeName + " was not found.");

            MethodDefinition bridgeGetUrl = FindBridgeMethod(bridge, "GetUrl");
            MethodDefinition bridgeGetOcrUrl = FindBridgeMethod(bridge, "GetOcrUrl");
            MethodReference importedGetUrl = target.MainModule.ImportReference(bridgeGetUrl);
            MethodReference importedGetOcrUrl = target.MainModule.ImportReference(bridgeGetOcrUrl);

            bool translationChanged = PatchTranslationUrl(webClient, importedGetUrl);
            bool ocrChanged = PatchDriveOcr(webClient, importedGetOcrUrl);
            bool oscChanged = PatchOscDelay(variableFrame);
            changed = translationChanged || ocrChanged || oscChanged;
            if (!changed)
            {
                Console.WriteLine("Assembly is already patched for translation, VLM OCR, and OSC chatbox delay.");
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
        Console.WriteLine("Patched translation, VLM OCR, and OSC chatbox delay: " + targetPath);
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

    private static bool PatchOscDelay(TypeDefinition variableFrame)
    {
        TypeDefinition stateMachine = variableFrame.NestedTypes.SingleOrDefault(t => t.Name == "<TakeHandsFrame>d__35");
        if (stateMachine == null) throw new InvalidOperationException("TakeHandsFrame state machine was not found.");
        MethodDefinition moveNext = stateMachine.Methods.SingleOrDefault(m => m.Name == "MoveNext");
        if (moveNext == null) throw new InvalidOperationException("TakeHandsFrame.MoveNext was not found.");

        FieldDefinition oscTimerField = variableFrame.Fields.SingleOrDefault(f => f.Name == "OSCTimer");
        if (oscTimerField == null) throw new InvalidOperationException("VRHandsFrameVariableFrame.OSCTimer was not found.");

        Instruction oscTextStore = moveNext.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Stfld &&
            i.Operand is FieldReference fr &&
            fr.Name == "OSCText" &&
            i.Previous != null &&
            i.Previous.OpCode == OpCodes.Ldfld &&
            i.Previous.Operand is FieldReference respData &&
            respData.Name == "responseData");

        if (oscTextStore == null)
            throw new InvalidOperationException("Expected OSCText store after translation responseData, but none found.");

        Instruction next = oscTextStore.Next;
        if (next != null &&
            (next.OpCode == OpCodes.Ldloc_1 || next.OpCode == OpCodes.Ldloc) &&
            next.Next != null &&
            next.Next.OpCode == OpCodes.Ldc_R4 &&
            next.Next.Next != null &&
            next.Next.Next.OpCode == OpCodes.Stfld &&
            next.Next.Next.Operand is FieldReference nextFr &&
            nextFr.Name == "OSCTimer")
        {
            return false;
        }

        ILProcessor il = moveNext.Body.GetILProcessor();
        Instruction loadTarget = il.Create(OpCodes.Ldloc_1);
        Instruction loadTimer = il.Create(OpCodes.Ldc_R4, 10.0f);
        Instruction storeTimer = il.Create(OpCodes.Stfld, oscTimerField);

        il.InsertAfter(oscTextStore, loadTarget);
        il.InsertAfter(loadTarget, loadTimer);
        il.InsertAfter(loadTimer, storeTimer);
        return true;
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
