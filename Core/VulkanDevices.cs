using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KytyLauncher.Core
{
    /// <summary>
    /// The Vulkan physical devices in the order vkEnumeratePhysicalDevices gives them: the index the emulator's --gpu takes.
    /// Read through the system's vulkan-1.dll (the graphics driver's loader); empty where there is none.
    /// </summary>
    public static class VulkanDevices
    {
        public sealed class Device
        {
            public int Index { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
            public override string ToString() => $"{Index}: {Name} ({Kind})";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkInstanceCreateInfo
        {
            public int sType;          // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1
            public IntPtr pNext;
            public uint flags;
            public IntPtr pApplicationInfo;
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
        }

        [DllImport("vulkan-1.dll")] static extern int vkCreateInstance(ref VkInstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
        [DllImport("vulkan-1.dll")] static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
        [DllImport("vulkan-1.dll")] static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[] devices);
        [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

        static readonly string[] Kinds = { "other", "integrated", "dedicated", "virtual", "CPU" };

        public static List<Device> List()
        {
            var result = new List<Device>();
            try
            {
                var info = new VkInstanceCreateInfo { sType = 1 };
                if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0) return result;
                try
                {
                    uint count = 0;
                    if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return result;
                    var devices = new IntPtr[count];
                    if (vkEnumeratePhysicalDevices(instance, ref count, devices) < 0) return result;
                    // VkPhysicalDeviceProperties: apiVersion, driverVersion, vendorID, deviceID, deviceType (offset 16),
                    // deviceName[256] (offset 20), then the UUID, limits and sparse properties (under 1 KB in all).
                    var props = Marshal.AllocHGlobal(4096);
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            vkGetPhysicalDeviceProperties(devices[i], props);
                            var type = Marshal.ReadInt32(props, 16);
                            var bytes = new byte[256];
                            Marshal.Copy(props + 20, bytes, 0, 256);
                            var end = Array.IndexOf(bytes, (byte)0);
                            result.Add(new Device
                            {
                                Index = i,
                                Name = Encoding.UTF8.GetString(bytes, 0, end < 0 ? 256 : end),
                                Kind = type >= 0 && type < Kinds.Length ? Kinds[type] : "other",
                            });
                        }
                    }
                    finally { Marshal.FreeHGlobal(props); }
                }
                finally { vkDestroyInstance(instance, IntPtr.Zero); }
            }
            catch { }
            return result;
        }
    }
}
