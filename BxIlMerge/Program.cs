using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.IO;
using BxIlMerge.Api;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;

namespace BxIlMerge
{
    class Program
    {
        const string _copyrights = "(c) Softanics, Artem A. Razin. All rights reserved.";
        const string _productName = "BoxedApp ILMerge";
        static string _banner = string.Format("{0} [Version {1}]\r\n{2}\r\n", new object[] { _productName, FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).FileVersion, _copyrights });
        const string _usage = "Usage:\r\nbxilmerge /out:<output assembly with embedded files> <input assembly> <unmanaged DLLs and other files to embed>...";

        const string _outPrefix = "/out:";

        static void Main(string[] args)
        {
            Console.WriteLine(_banner);

            // Process arguments

            string inputAssemblyPath = null;
            string outAssemblyPath = null;
            List<string> filesToEmbedPaths = new List<string>();

            foreach (string arg in args)
            {
                if (arg.StartsWith(_outPrefix))
                {
                    if (null != outAssemblyPath)
                    {
                        Console.Write("Error: multiple output files specified\r\n\r\n");
                        PrintUsage();
                        return;
                    }

                    outAssemblyPath = arg.Substring(_outPrefix.Length);
                }
                else
                {
                    if (null == inputAssemblyPath)
                    {
                        inputAssemblyPath = arg;
                    }
                    else
                    { 
                        filesToEmbedPaths.Add(arg);
                    }
                }
            }

            if (null == inputAssemblyPath)
            {
                Console.Write("Error: no <input assembly> specified\r\n\r\n");
                PrintUsage();
                return;
            }

            if (null == outAssemblyPath)
            {
                Console.Write("Error: no <output assembly with embedded files> specified\r\n\r\n");
                PrintUsage();
                return;
            }

            if (0 == filesToEmbedPaths.Count)
            {
                Console.Write("Error: no <unmanaged DLLs and other files to embed...> specified\r\n\r\n");
                PrintUsage();
                return;
            }

            using (AssemblyDefinition inputAssembly = AssemblyDefinition.ReadAssembly(inputAssemblyPath, new ReaderParameters() { SymbolReaderProvider = new PdbReaderProvider(), ReadSymbols = true }))
            {
                // Add method that calls virtual files creation method
                MethodDefinition callCreateVirtualFilesMethod = CreateCallCreateVirtualFilesMethod(inputAssembly);

                // Add method that loads assembly from embedded resources
                MethodDefinition loadAssemblyFromEmbeddedResourceMethod = CreateLoadAssemblyFromEmbeddedResourceMethod(inputAssembly);

                // Add event handler of AppDomain.AssemblyResolve:
                // static Assembly currentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
                MethodDefinition assemblyResolveMethod = CreateAssemblyResolveMethod(inputAssembly, loadAssemblyFromEmbeddedResourceMethod);

                // Add each file as embedded resource

                var resources = inputAssembly.MainModule.Resources;

                foreach (string fileToEmbedPath in filesToEmbedPaths)
                {
                    // Generate unique name for embedded resource
                    string embeddedResourceName;
                    do
                    {
                        embeddedResourceName = string.Format("bx\\{0}\\{1}", Guid.NewGuid(), Path.GetFileName(fileToEmbedPath));
                    } while (null != resources.FirstOrDefault(x => x.Name == embeddedResourceName));

                    resources.Add(new EmbeddedResource(embeddedResourceName, ManifestResourceAttributes.Public, File.OpenRead(fileToEmbedPath)));
                }

                // Add code that creates virtual files to the entry point
                CreateModuleConstructor(inputAssembly, assemblyResolveMethod, callCreateVirtualFilesMethod);

                // Write output assembly

                CustomAttribute debuggableAttribute = new CustomAttribute(
                inputAssembly.MainModule.ImportReference(
                    typeof(DebuggableAttribute).GetConstructor(new[] { typeof(bool), typeof(bool) })));

                debuggableAttribute.ConstructorArguments.Add(new CustomAttributeArgument(
                    inputAssembly.MainModule.ImportReference(typeof(bool)), true));

                debuggableAttribute.ConstructorArguments.Add(new CustomAttributeArgument(
                    inputAssembly.MainModule.ImportReference(typeof(bool)), true));

                inputAssembly.CustomAttributes.Add(debuggableAttribute);

                inputAssembly.Write(outAssemblyPath, new WriterParameters() { SymbolWriterProvider = new PdbWriterProvider(), WriteSymbols = true });
            }
        }

        static MethodDefinition CreateCallCreateVirtualFilesMethod(AssemblyDefinition assembly)
        {
            MethodDefinition method = new MethodDefinition(
                "callCreateVirtualFiles_" + Guid.NewGuid().ToString(),
                MethodAttributes.Static,
                assembly.MainModule.TypeSystem.Void);

            ILProcessor il = method.Body.GetILProcessor();

            il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly"))));
            il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(Bx).GetMethod("CreateVirtualFiles"))));
            il.Append(il.Create(OpCodes.Ret));

            TypeDefinition moduleClass = assembly.MainModule.Types.First(t => t.Name == "<Module>");

            moduleClass.Methods.Add(method);

            return method;
        }

        static MethodDefinition CreateAssemblyResolveMethod(AssemblyDefinition assembly, MethodDefinition loadAssemblyFromEmbeddedResourceMethod)
        {
            MethodDefinition method = new MethodDefinition(
                "currentDomain_AssemblyResolve_" + Guid.NewGuid().ToString(),
                MethodAttributes.Static,
                assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly)));

            // Parameters of the event handler
            method.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Object));
            method.Parameters.Add(new ParameterDefinition(assembly.MainModule.ImportReference(typeof(System.ResolveEventArgs))));

            // Variable #0 - to store assembly name
            method.Body.Variables.Add(new VariableDefinition(assembly.MainModule.TypeSystem.String));

            ILProcessor il = method.Body.GetILProcessor();

            // Get assembly name
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(System.ResolveEventArgs).GetProperty("Name").GetGetMethod())));
            il.Append(il.Create(OpCodes.Stloc_0));

            Instruction nextCheckInstruction = null;

            foreach (System.Reflection.Assembly bxAssembly in new System.Reflection.Assembly[] { typeof(BxIlMerge.Api.Bx).Assembly, typeof(BoxedAppSDK.NativeMethods).Assembly })
            {
                var resources = assembly.MainModule.Resources;

                // Add this bx related assembly to embedded resources
                string embeddedResourceName = Guid.NewGuid().ToString();
                resources.Add(new EmbeddedResource(embeddedResourceName, ManifestResourceAttributes.Public, File.OpenRead(bxAssembly.Location)));

                if (null != nextCheckInstruction)
                    il.Append(nextCheckInstruction);

                nextCheckInstruction = il.Create(OpCodes.Nop);
                Instruction foundAssemblyBranchStartInstruction = il.Create(OpCodes.Nop);

                // Compare this bx related assembly name with requested assembly name
                il.Append(il.Create(OpCodes.Ldstr, bxAssembly.FullName));
                il.Append(il.Create(OpCodes.Ldloc_0));
                il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(System.String).GetMethod("CompareTo", new Type[] { typeof(string) }))));
                il.Append(il.Create(OpCodes.Brfalse, foundAssemblyBranchStartInstruction));
                il.Append(il.Create(OpCodes.Br, nextCheckInstruction));
                il.Append(foundAssemblyBranchStartInstruction);
                il.Append(il.Create(OpCodes.Ldstr, embeddedResourceName));
                il.Append(il.Create(OpCodes.Call, loadAssemblyFromEmbeddedResourceMethod));
                il.Append(il.Create(OpCodes.Ret));
            }

            if (null != nextCheckInstruction)
                il.Append(nextCheckInstruction);

            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ret));

            TypeDefinition moduleClass = assembly.MainModule.Types.First(t => t.Name == "<Module>");

            moduleClass.Methods.Add(method);

            return method;
        }

        static MethodDefinition CreateModuleConstructor(AssemblyDefinition assembly, MethodDefinition assemblyResolveMethod, MethodDefinition callCreateVirtualFilesMethod)
        {
            MethodDefinition cctor = new MethodDefinition(
                ".cctor",
                MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                assembly.MainModule.TypeSystem.Void);

            ILProcessor il = cctor.Body.GetILProcessor();

            // Add event handler:
            // AppDomain.CurrentDomain.AssemblyResolve += currentDomain_AssemblyResolve;
            il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(System.AppDomain).GetProperty("CurrentDomain").GetGetMethod())));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldftn, assemblyResolveMethod));
            il.Append(il.Create(OpCodes.Newobj, assembly.MainModule.ImportReference(typeof(System.ResolveEventHandler).GetConstructor(new Type[] { typeof(object), typeof(IntPtr) }))));
            il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(System.AppDomain).GetEvent("AssemblyResolve").GetAddMethod())));

            // Then create virtual files
            il.Append(il.Create(OpCodes.Call, callCreateVirtualFilesMethod));

            il.Append(il.Create(OpCodes.Ret));

            TypeDefinition moduleClass = assembly.MainModule.Types.First(t => t.Name == "<Module>");

            if (null != moduleClass.Methods.FirstOrDefault(m => m.Name == ".cctor"))
            {
                // TODO: may be instead modify it?
                throw new Exception(".cctor already exists");
            }

            moduleClass.Methods.Add(cctor);

            return cctor;
        }

        static MethodDefinition CreateLoadAssemblyFromEmbeddedResourceMethod(AssemblyDefinition assembly)
        {
            MethodDefinition method = new MethodDefinition(
                "currentDomain_AssemblyResolve_" + Guid.NewGuid().ToString(),
                MethodAttributes.Static,
                assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly)));

            // Parameter #0 - name of embedded resource
            method.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));

            // Variable #0 - embedded resource stream
            method.Body.Variables.Add(new VariableDefinition(assembly.MainModule.ImportReference(typeof(Stream))));
            // Variable #1 - byte array to store embedded resource stream content
            method.Body.Variables.Add(new VariableDefinition(new ArrayType(assembly.MainModule.TypeSystem.Byte)));

            ILProcessor il = method.Body.GetILProcessor();

            // Get embedded resource stream
            il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly"))));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetManifestResourceStream", new Type[] { typeof(string) }))));
            il.Append(il.Create(OpCodes.Stloc_0));

            // Get length of embedded resource stream, and create byte array of this length
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(Stream).GetProperty("Length").GetGetMethod())));
            il.Append(il.Create(OpCodes.Newarr, assembly.MainModule.TypeSystem.Byte));
            il.Append(il.Create(OpCodes.Stloc_1));

            // Read entire content from the stream to the array
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldlen));
            il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(typeof(Stream).GetMethod("Read", new Type[] { typeof(byte[]), typeof(int), typeof(int) }))));
            il.Append(il.Create(OpCodes.Pop)); // we don't need a result

            // Load assembly from the array
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Call, assembly.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("Load", new Type[] { typeof(byte[]) }))));
            il.Append(il.Create(OpCodes.Ret));

            TypeDefinition moduleClass = assembly.MainModule.Types.First(t => t.Name == "<Module>");

            moduleClass.Methods.Add(method);

            return method;
        }

        static void PrintUsage()
        {
            Console.Write(_usage);
        }
    }
}
