using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace GuitarBridge;

internal interface IDefaultOutputPolicy
{
    string? Get(Role role);
    bool IsActive(string id);
    void Set(string id, Role role);
}

internal sealed class WindowsDefaultOutputPolicy : IDefaultOutputPolicy
{
    public string? Get(Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role); return device.ID; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
    }

    public bool IsActive(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDevice(id); return device.State == DeviceState.Active && device.DataFlow == DataFlow.Render; }
        catch (COMException) { return false; }
    }

    public void Set(string id, Role role)
    {
        if (!IsActive(id)) throw new InvalidOperationException("The Windows playback device is disconnected or unavailable.");
        // Windows exposes endpoint selection through this widely used private COM
        // interface. Keep the method order: SetDefaultEndpoint is vtable slot 13.
        // ABI reference: github.com/DanStevens/AudioEndPointController, PolicyConfig.h.
        object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
        try { Marshal.ThrowExceptionForHR(((IPolicyConfig)instance).SetDefaultEndpoint(id, role)); }
        finally { Marshal.ReleaseComObject(instance); }
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}

internal sealed record OutputRestoreState(string Target, Dictionary<Role, string> Originals);

internal sealed class DefaultOutputRouting
{
    private readonly IDefaultOutputPolicy policy;
    private readonly string? journal;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OutputRestoreState? prepared, active;
    private bool loaded;
    internal static readonly Role[] PlaybackRoles = { Role.Console, Role.Multimedia };

    internal DefaultOutputRouting(IDefaultOutputPolicy policy, string? journal = null) { this.policy = policy; this.journal = journal; }

    internal Task PrepareAsync(string target) => RunAsync(() =>
    {
        RestoreCore();
        if (!policy.IsActive(target)) throw new InvalidOperationException("The selected Windows output is unavailable.");
        var originals = new Dictionary<Role, string>();
        foreach (var role in PlaybackRoles)
            if (policy.Get(role) is string original && original != target) originals.Add(role, original);
        prepared = new OutputRestoreState(target, originals);
    });

    internal Task ActivateAsync() => RunAsync(() =>
    {
        if (prepared == null) return;
        active = prepared;
        prepared = null;
        // Persist before changing anything so the next launch can recover a crash.
        SaveJournal();
        foreach (var role in active.Originals.Keys) policy.Set(active.Target, role);
    });

    internal Task RestoreAsync() => RunAsync(RestoreCore);

    private async Task RunAsync(Action action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(action).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private void RestoreCore()
    {
        prepared = null;
        if (!loaded)
        {
            // A missing file means no previous change; malformed recovery data is
            // reported rather than silently overwriting the user's Windows defaults.
            if (journal != null && File.Exists(journal))
                active = JsonSerializer.Deserialize<OutputRestoreState>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Invalid Windows output recovery data.");
            loaded = true;
        }
        if (active == null) return;
        var failures = new List<Exception>();
        foreach (var pair in active.Originals.ToArray())
        {
            if (!PlaybackRoles.Contains(pair.Key)) { active.Originals.Remove(pair.Key); continue; }
            try
            {
                // Do not overwrite a Windows output selection made by the user
                // or by Windows after the routed device was removed.
                string? current = policy.Get(pair.Key);
                if (current == active.Target || current == null)
                {
                    if (!policy.IsActive(pair.Value)) throw new InvalidOperationException("The previous Windows output is disconnected. Reconnect it to restore the saved default.");
                    policy.Set(pair.Value, pair.Key);
                }
                active.Originals.Remove(pair.Key);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        SaveJournal();
        if (active.Originals.Count == 0) active = null;
        if (failures.Count != 0) throw new AggregateException("Could not restore the previous Windows output. The saved default will be retried on the next launch.", failures);
    }

    private void SaveJournal()
    {
        if (journal == null) return;
        if (active == null || active.Originals.Count == 0) { File.Delete(journal); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        string temporary = journal + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(active));
        File.Move(temporary, journal, overwrite: true);
    }
}
