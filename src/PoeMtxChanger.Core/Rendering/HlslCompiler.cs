using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PoeMtxChanger.Core.Rendering;

/// <summary>
/// Compiles composed material shaders with the system D3D compiler (d3dcompiler_47, part of Windows) and keeps the
/// bytecode in a local cache keyed by the source hash. Nothing here talks to the game process.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HlslCompiler(string? cacheDirectory)
{
    public sealed record Result(byte[]? Bytecode, string? Error, bool FromCache);

    public Result Compile(string hlsl, string entryPoint, string profile)
    {
        var source = Encoding.UTF8.GetBytes(hlsl);
        string? cacheFile = null;
        if (cacheDirectory != null)
        {
            var key = Convert.ToHexString(SHA256.HashData([.. source, .. Encoding.UTF8.GetBytes("|" + entryPoint + "|" + profile)]))[..32];
            cacheFile = Path.Combine(cacheDirectory, key + ".cso");
            try { if (File.Exists(cacheFile)) return new(File.ReadAllBytes(cacheFile), null, true); }
            catch (IOException) { }
        }

        int hr;
        ID3DBlob? code = null, errors = null;
        try
        {
            hr = D3DCompile(source, source.Length, "material.hlsl", 0, 0, entryPoint, profile, 0, 0, out code, out errors);
            string? message = errors is null ? null : Read(errors) is { } bytes ? Encoding.ASCII.GetString(bytes).TrimEnd('\0') : null;
            if (hr < 0 || code is null) return new(null, message ?? $"HRESULT 0x{hr:X8}", false);
            var bytecode = Read(code)!;
            if (cacheFile != null)
                try
                {
                    Directory.CreateDirectory(cacheDirectory!);
                    var temp = cacheFile + ".tmp";
                    File.WriteAllBytes(temp, bytecode);
                    File.Move(temp, cacheFile, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            return new(bytecode, null, false);
        }
        catch (DllNotFoundException) { return new(null, "d3dcompiler_47.dll is not available", false); }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }

    private static byte[]? Read(ID3DBlob blob)
    {
        var size = (int)blob.GetBufferSize();
        if (size <= 0) return null;
        var bytes = new byte[size];
        Marshal.Copy(blob.GetBufferPointer(), bytes, 0, size);
        return bytes;
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] nint GetBufferPointer();
        [PreserveSig] nint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] src, nint size, string? name, nint defines, nint include, string entry, string target,
        uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);
}
