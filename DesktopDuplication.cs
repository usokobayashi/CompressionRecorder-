//==================================================
// DesktopDuplication
// 画面の録画に ffmpeg の ddagrab(Desktop Duplication API)を使うための情報を集める。
// gdigrab は画面を取り込むたびに画面上のカーソルがちらつき、表示倍率によってはカーソルを
// ずれた位置(左上寄り)に描くため、使えるモニターでは ddagrab で録画する。
// ddagrab が番号で指定できるのは既定のGPU(アダプター0)の出力だけなので、モニター名で番号を探す。
//==================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace uskCompressionRecorder;

internal static class DesktopDuplication
{
    // Rotation は DXGI_MODE_ROTATION(1:回転なし 2:90度 3:180度 4:270度)
    public readonly record struct Output(int Index, Rectangle Bounds, int Rotation)
    {
        // ddagrab は回転前の向きで撮るため、画面の向きへ戻すフィルター(回転がなければ空)
        public string Unrotate => Rotation switch { 2 => "transpose=1,", 3 => "hflip,vflip,", 4 => "transpose=2,", _ => string.Empty };
    }

    private static readonly ConcurrentDictionary<int, Task<bool>> Usable = new();

    //--------------
    // Find
    //--------------
    // モニター名(\\.\DISPLAY1 など)に対応する、アダプター0の出力の番号と範囲(画素)。見つからなければ null
    public static Output? Find(string deviceName)
    {
        IDXGIFactory1? factory = null; IDXGIAdapter1? adapter = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var pointer) < 0) return null;
            factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            if (factory.EnumAdapters1(0, out adapter) < 0) return null;
            for (uint index = 0; adapter.EnumOutputs(index, out var output) >= 0; index++)
            {
                try
                {
                    if (output.GetDesc(out var desc) < 0) continue;
                    if (desc.AttachedToDesktop != 0 && string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        var r = desc.DesktopCoordinates;
                        return new Output((int)index, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), desc.Rotation);
                    }
                }
                finally { Marshal.ReleaseComObject(output); }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (adapter is not null) Marshal.ReleaseComObject(adapter);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    //--------------
    // IsUsableAsync
    //--------------
    // その出力から実際に1コマ撮れるかを確かめる。結果は出力ごとに覚え、2回目以降は待たない
    public static Task<bool> IsUsableAsync(string ffmpeg, int index) => Usable.GetOrAdd(index, i => Task.Run(async () =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx={i}:framerate=5,hwdownload,format=bgra -frames:v 1 -f null -") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            if (p is null) return false;
            _ = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }));

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // 呼ばない関数も、表(vtable)の並びを合わせるために順番どおり宣言する
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out OutputDesc desc);
    }
}
