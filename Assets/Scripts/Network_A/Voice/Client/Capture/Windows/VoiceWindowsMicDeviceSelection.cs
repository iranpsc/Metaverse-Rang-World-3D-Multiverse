#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
    // This stores a per-process preference only. It never changes the Windows system default.
    internal static class VoiceWindowsMicDeviceSelection
    {
        internal sealed class Device
        {
            internal string Id;
            internal string Name;
        }

        private static string preferredId = string.Empty;
        private static string preferredName = string.Empty;

        internal static string PreferredEndpointId { get { return preferredId; } }

        internal static bool TryGetPreferred(out string id, out string name)
        {
            id = preferredId;
            name = preferredName;
            return id.Length > 0 || name.Length > 0;
        }

        internal static void Select(Device device)
        {
            preferredId = device != null ? device.Id ?? string.Empty : string.Empty;
            preferredName = device != null ? device.Name ?? string.Empty : string.Empty;
            Debug.Log("VME2_WIN_MIC_DEVICE_UI_SELECTION | selected=" +
                      ((preferredId.Length == 0 && preferredName.Length == 0) ? "windows_default" : preferredName));
        }

        // Enumerate active Windows capture endpoints with their real endpoint IDs.
        // UnityEngine.Microphone.devices is not sufficient to distinguish duplicate names.
        internal static bool TryEnumerate(out List<Device> devices, out string error)
        {
            devices = new List<Device>();
            error = string.Empty;
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.EnumAudioEndpoints(1, 1, out collection);
                if (hr < 0 || collection == null)
                {
                    error = "EnumAudioEndpoints HRESULT=0x" + hr.ToString("X8");
                    return false;
                }
                hr = collection.GetCount(out uint count);
                if (hr < 0)
                {
                    error = "IMMDeviceCollection.GetCount HRESULT=0x" + hr.ToString("X8");
                    return false;
                }
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice endpoint = null;
                    IPropertyStore store = null;
                    IntPtr idPointer = IntPtr.Zero;
                    try
                    {
                        hr = collection.Item(i, out endpoint);
                        if (hr < 0 || endpoint == null) continue;
                        hr = endpoint.GetId(out idPointer);
                        if (hr < 0 || idPointer == IntPtr.Zero) continue;
                        string id = Marshal.PtrToStringUni(idPointer) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        string name = id;
                        hr = endpoint.OpenPropertyStore(0, out store);
                        if (hr >= 0 && store != null)
                        {
                            PROPERTYKEY key = new PROPERTYKEY
                            {
                                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                                pid = 14
                            };
                            PROPVARIANT value;
                            hr = store.GetValue(ref key, out value);
                            if (hr >= 0)
                            {
                                try
                                {
                                    string result = value.vt == 31 && value.data != IntPtr.Zero
                                        ? Marshal.PtrToStringUni(value.data)
                                        : null;
                                    if (!string.IsNullOrWhiteSpace(result)) name = result;
                                }
                                finally { PropVariantClear(ref value); }
                            }
                        }
                        devices.Add(new Device { Id = id, Name = name });
                    }
                    finally
                    {
                        if (idPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(idPointer);
                        Release(store);
                        Release(endpoint);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // Some Unity/Mono WindowsPlayer builds cannot cast the COM
                // IMMDeviceCollection returned by EnumAudioEndpoints. Unity's
                // own microphone list is already working in those builds.
                string comError = ex.GetType().Name + ": " + ex.Message;
                devices.Clear();
                try
                {
                    string[] unityNames = Microphone.devices;
                    if (unityNames != null)
                    {
                        foreach (string candidate in unityNames)
                        {
                            if (string.IsNullOrWhiteSpace(candidate)) continue;
                            devices.Add(new Device { Id = string.Empty, Name = candidate });
                        }
                    }
                }
                catch (Exception unityException)
                {
                    error = comError + " | UnityMicrophone=" + unityException.GetType().Name +
                            ": " + unityException.Message;
                    devices.Clear();
                    return false;
                }
                if (devices.Count > 0)
                {
                    Debug.LogWarning("VME2_WIN_MIC_DEVICE_LIST_COM_FALLBACK=PASS" +
                        " | reason=" + comError + " | unityDevices=" + devices.Count +
                        " | selectionByNativeFriendlyName=True");
                    error = string.Empty;
                    return true;
                }
                error = comError + " | UnityMicrophone.devices returned no devices";
                return false;
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }

        private static void Release(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
            catch { }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT value);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr data;
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-C0A5C3B8D5C2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr paramsPtr,
                [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);
            [PreserveSig] int OpenPropertyStore(int accessMode, out IPropertyStore properties);
            [PreserveSig] int GetId(out IntPtr id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }
    }
}
#endif
