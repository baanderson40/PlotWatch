using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.Text;
using FFXIVClientStructs.STD;

namespace PlotWatch.Diagnostics;

/// <summary>
/// Version-locked opt-in observation of the statically traced SystemTalk queue boundary.
/// No formatter invocation, gameplay-data writes, callback invocation, or retained game pointers.
/// </summary>
public sealed unsafe class HousingNativeProbe(IGameInteropProvider interop, ISigScanner scanner, IFramework framework,
    Func<HousingNativeContext> sampleContext) : IDisposable
{
    // Unique prologue in the SHA256-pinned executable. Located via SystemTalk Lua registration,
    // scene implementation vtable slot 23, then its call to the EventFramework formatting queue.
    // Absolute disassembly addresses are intentionally not used by the runtime adapter.
    private const string Signature = "48 89 5C 24 20 55 57 41 54 41 56 41 57 48 8D 6C 24 D1 48 81 EC 00 01 00 00";
    private static readonly byte[] Prologue = Convert.FromHexString(Signature.Replace(" ", ""));
    private delegate void QueueFormat(nint manager, nint input, nint parameters, nint callback, ulong callbackParameter);
    private Hook<QueueFormat>? hook;
    private readonly HousingNativeEvidence evidence = new();
    private byte[] successTemplate = [];
    private long endTick;
    private bool enabled;
    private int readFailures;
    private int offThread;
    private bool disposed;
    [ThreadStatic] private static bool inDetour;

    public string Status { get; private set; } = "Off; native success probe requires explicit opt-in.";

    public string Arm(byte[] template, long tick)
    {
        Stop();
        if (disposed || !framework.IsInFrameworkUpdateThread) return Status = "Unavailable: native probe must arm on framework thread.";
        if (template.Length is < 16 or > 1024) return Status = "Unavailable: exact localized success template missing/out of bounds.";
        try
        {
            if (hook is null)
            {
                var executable = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException();
                var version = File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!, "ffxivgame.ver")).Trim();
                using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var hash = Convert.ToHexString(SHA256.HashData(file));
                var layouts = sizeof(Utf8String) == 0x68 && sizeof(TextParameter) == 0x18 && sizeof(StdDeque<TextParameter>) == 0x28 &&
                    Marshal.OffsetOf<Utf8String>(nameof(Utf8String.BufUsed)).ToInt32() == 0x10 &&
                    Marshal.OffsetOf<Utf8String>(nameof(Utf8String.StringLength)).ToInt32() == 0x18 &&
                    Marshal.OffsetOf<TextParameter>(nameof(TextParameter.ValuePtr)).ToInt32() == 0x8 &&
                    Marshal.OffsetOf<TextParameter>(nameof(TextParameter.Type)).ToInt32() == 0x10 &&
                    Marshal.OffsetOf<StdDeque<TextParameter>>(nameof(StdDeque<TextParameter>.Map)).ToInt32() == 0x8 &&
                    Marshal.OffsetOf<StdDeque<TextParameter>>(nameof(StdDeque<TextParameter>.MySize)).ToInt32() == 0x20 &&
                    sizeof(EventState) == 0x30 && Marshal.OffsetOf<EventState>(nameof(EventState.EventId)).ToInt32() == 0x8 &&
                    Marshal.OffsetOf<EventFramework>(nameof(EventFramework.EventState1)).ToInt32() == 0x3F80 &&
                    Marshal.OffsetOf<EventFramework>(nameof(EventFramework.EventState2)).ToInt32() == 0x3FE0 &&
                    Marshal.OffsetOf<EventFramework>(nameof(EventFramework.Scene)).ToInt32() == 0x4018;
                if (!HousingNativeCompatibility.Allows(version, hash, layouts))
                    return Status = "Unavailable: game version, executable hash, or ClientStructs layout differs from inspected build. No hook installed.";
                var address = scanner.ScanText(Signature);
                Span<byte> actual = stackalloc byte[Prologue.Length];
                if (!TryReadBytes(address, actual) || !actual.SequenceEqual(Prologue))
                    return Status = "Unavailable: native queue prologue differs or is already hooked. No hook installed.";
                hook = interop.HookFromAddress<QueueFormat>(address, Detour);
            }
            successTemplate = template.ToArray();
            evidence.Arm(sampleContext());
            readFailures = offThread = 0;
            endTick = tick + 15_000;
            if (Environment.TickCount64 >= endTick)
            {
                evidence.Stop();
                return Status = "Unavailable: probe expired during compatibility checks; native hook not enabled.";
            }
            hook.Enable();
            enabled = true;
            return Status = "Armed: version/hash-gated SystemTalk formatting-queue probe; exact success-template bytes only. Runtime mapping unverified.";
        }
        catch (Exception)
        {
            Stop();
            return Status = "Unavailable: native queue probe initialization failed; no exception payload or paths exported.";
        }
    }

    private void Detour(nint manager, nint input, nint parameters, nint callback, ulong callbackParameter)
    {
        var nested = inDetour;
        inDetour = true;
        try
        {
            if (!nested)
            {
                try { Observe(manager, input, parameters); }
                catch (Exception) { Interlocked.Increment(ref readFailures); }
            }
            // Exactly one original call, untouched arguments. Observer exceptions cannot skip it.
            hook!.OriginalDisposeSafe(manager, input, parameters, callback, callbackParameter);
        }
        finally { inDetour = nested; }
    }

    private void Observe(nint manager, nint input, nint parameters)
    {
        if (!enabled || disposed || Environment.TickCount64 >= endTick) return;
        if (!framework.IsInFrameworkUpdateThread) { Interlocked.Increment(ref offThread); return; }
        if (!TryRead(input, out Utf8String text) || text.StringLength != successTemplate.Length ||
            text.BufUsed != text.StringLength + 1 || text.BufSize < text.BufUsed) return;
        Span<byte> bytes = stackalloc byte[successTemplate.Length];
        if (!TryReadBytes((nint)text.StringPtr.Value, bytes) || !HousingNativeCompatibility.MatchesSuccessTemplate(bytes, successTemplate)) return;

        // No string parsing/formatting/logging in the detour. All reads use ReadProcessMemory;
        // bad pointers fail without dereferencing arbitrary game memory in managed code.
        var context = sampleContext();
        var native = (EventFramework*)manager;
        var idsRead = TryRead((nint)(&native->EventState1.EventId), out uint event1) &
            TryRead((nint)(&native->EventState2.EventId), out uint event2) &
            TryRead((nint)(&native->Scene), out short scene);
        var copied = CopyParameters(parameters, out var failure);
        evidence.Capture(context, DateTimeOffset.UtcNow, event1, event2, scene, copied,
            idsRead ? failure : "scene context read failed");
    }

    private static HousingNativeParameter[] CopyParameters(nint address, out string? failure)
    {
        failure = null;
        if (!TryRead(address, out StdDeque<TextParameter> deque) || deque.MySize > 16 || deque.MapSize is 0 or > 1024 ||
            (deque.MapSize & (deque.MapSize - 1)) != 0 || deque.Map == null || deque.MyOff > ulong.MaxValue - deque.MySize)
        { failure = "deque bounds/read rejected"; return []; }
        var result = new HousingNativeParameter[(int)deque.MySize];
        for (var index = 0; index < result.Length; index++)
        {
            // MSVC deque block size is 1 for 0x18-byte TextParameter. Never invoke container methods.
            var block = (deque.MyOff + (ulong)index) & (deque.MapSize - 1);
            if (!TryRead(checked((nint)deque.Map + (nint)(block * 8)), out nint element) ||
                !TryRead(element, out TextParameter parameter))
            { failure = "parameter read failed"; return result.Take(index).ToArray(); }
            var self = (nint)parameter.ValuePtr == element;
            result[index] = new(index, (int)parameter.Type,
                parameter.Type == TextParameterType.Integer && self ? parameter.IntValue : null, self);
        }
        return result;
    }

    public void UpdateContext(HousingNativeContext current) => evidence.UpdateContext(current);

    public IReadOnlyList<string> Drain(HousingNativeContext current)
        => enabled ? evidence.Drain(current) : [];

    public string Stop()
    {
        enabled = false;
        if (hook?.IsEnabled == true) hook.Disable();
        evidence.Stop();
        successTemplate = [];
        var summary = $"Native queue probe stopped; duplicates={evidence.Duplicates}; dropped={evidence.Dropped}; context-invalidated={evidence.Invalidated}; read-failures={readFailures}; off-thread-skipped={offThread}. No bid writes.";
        if (hook is not null) Status = summary;
        return summary;
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop();
        disposed = true;
        hook?.Dispose();
    }

    private static bool TryRead<T>(nint address, out T result) where T : unmanaged
    {
        result = default;
        fixed (T* destination = &result)
            return address != 0 && ReadProcessMemory(new nint(-1), address, destination, (nuint)sizeof(T), out var read) && read == (nuint)sizeof(T);
    }

    private static bool TryReadBytes(nint address, Span<byte> destination)
    {
        fixed (byte* target = destination)
            return address != 0 && ReadProcessMemory(new nint(-1), address, target, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint bytesRead);
}
