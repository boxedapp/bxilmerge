using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.IO;
using Microsoft.Win32.SafeHandles;
using BoxedAppSDK;

namespace BxIlMerge.Api
{
    public class Bx
    {
        public static void CreateVirtualFiles(Assembly assembly)
        {
            BoxedAppSDK.NativeMethods.BoxedAppSDK_Init();

            foreach (string embeddedResourceName in assembly.GetManifestResourceNames())
            {
                if (embeddedResourceName.StartsWith(@"bx\") &&
                    embeddedResourceName.Length > @"bx\".Length + Guid.NewGuid().ToString().Length &&
                    '\\' == embeddedResourceName[@"bx\".Length + Guid.NewGuid().ToString().Length])
                {
                    string virtualFileName = embeddedResourceName.Substring(@"bx\".Length + Guid.NewGuid().ToString().Length + 1);

                    using (SafeFileHandle virtualFileHandle = new SafeFileHandle(BoxedAppSDK.NativeMethods.BoxedAppSDK_CreateVirtualFile(
                        Path.Combine(Path.GetDirectoryName(assembly.Location), virtualFileName),
                        NativeMethods.EFileAccess.GenericWrite,
                        NativeMethods.EFileShare.Read,
                        IntPtr.Zero,
                        NativeMethods.ECreationDisposition.CreateAlways,
                        0,
                        IntPtr.Zero), true))
                    {
                        using (Stream virtualFileStream = new FileStream(virtualFileHandle, FileAccess.Write))
                        {
                            using (Stream embeddedResourceStream = assembly.GetManifestResourceStream(embeddedResourceName))
                            {
                                byte[] data = new byte[embeddedResourceStream.Length];
                                embeddedResourceStream.Read(data, 0, data.Length);
                                virtualFileStream.Write(data, 0, data.Length);
                            }
                        }
                    }
                }
            }
        }
    }
}
