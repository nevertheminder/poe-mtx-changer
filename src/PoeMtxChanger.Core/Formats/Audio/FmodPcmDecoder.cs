using System.Runtime.InteropServices;

namespace PoeMtxChanger.Core.Formats.Audio;

/// <summary>Optional preview decoder using the user's installed FMOD 2.03 runtime, never redistributed.</summary>
public sealed class FmodPcmDecoder : IDisposable
{
    private IntPtr _library,_system;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CreateSystem(out IntPtr system,uint version);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetOutput(IntPtr system,int output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(IntPtr system,int channels,uint flags,IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Release(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CreateSound(IntPtr system,[MarshalAs(UnmanagedType.LPUTF8Str)]string path,uint mode,IntPtr info,out IntPtr sound);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SubSound(IntPtr parent,int index,out IntPtr sound);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Format(IntPtr sound,out int type,out int format,out int channels,out int bits);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ReadData(IntPtr sound,[Out]byte[] buffer,uint length,out uint read);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetLength(IntPtr sound,out uint length,uint unit);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SeekData(IntPtr sound,uint frame);
    private T Export<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library,"FMOD_"+name));
    public FmodPcmDecoder(string dllPath)
    {
        try
        {
            _library=NativeLibrary.Load(Path.GetFullPath(dllPath));
            Check(Export<CreateSystem>("System_Create")(out _system,0x00020311));
            Check(Export<SetOutput>("System_SetOutput")(_system,2)); // NOSOUND; decoded data is played by the app separately
            Check(Export<Init>("System_Init")(_system,1,0,IntPtr.Zero));
        }
        catch{Dispose();throw;}
    }
    public byte[] DecodeWave(string fsbPath,SampleInfo sample,int maxSeconds=12)
    {
        Check(Export<CreateSound>("System_CreateSound")(_system,fsbPath,0x2000,IntPtr.Zero,out var parent)); // OPENONLY
        try
        {
            Check(Export<SubSound>("Sound_GetSubSound")(parent,sample.Index,out var sound));
            Check(Export<Format>("Sound_GetFormat")(sound,out _,out int format,out int channels,out int bits));
            if(channels!=sample.Channels||bits is not (8 or 16 or 24 or 32)||format is <1 or >5)throw new NotSupportedException("Unknown PCM format from FMOD.");
            Check(Export<GetLength>("Sound_GetLength")(sound,out uint decodedBytes,4));
            int size=checked((int)Math.Min(decodedBytes,(long)sample.Frequency*Math.Clamp(maxSeconds,1,30)*channels*bits/8));
            if(size>64*1024*1024)throw new InvalidDataException("Preview audio is too large.");
            Check(Export<SeekData>("Sound_SeekData")(sound,0));
            byte[] pcm=new byte[size],buffer=new byte[65536];uint count=0;
            while(count<size)
            {
                uint desired=(uint)Math.Min(buffer.Length,size-count);
                int result=Export<ReadData>("Sound_ReadData")(sound,buffer,desired,out uint read);
                if(read>desired)throw new InvalidDataException("FMOD returned invalid PCM length.");
                Buffer.BlockCopy(buffer,0,pcm,(int)count,(int)read);count+=read;
                if(result!=0&&count<size)Check(result);
                if(read==0&&count<size)throw new InvalidDataException("FMOD PCM ended prematurely.");
            }
            using var output=new MemoryStream();using var w=new BinaryWriter(output);
            w.Write("RIFF"u8);w.Write(36u+count);w.Write("WAVEfmt "u8);w.Write(16u);w.Write((ushort)(format==5?3:1));w.Write((ushort)channels);
            w.Write(sample.Frequency);w.Write(sample.Frequency*channels*bits/8);w.Write((ushort)(channels*bits/8));w.Write((ushort)bits);
            w.Write("data"u8);w.Write(count);w.Write(pcm,0,(int)count);return output.ToArray();
        }
        finally{Export<Release>("Sound_Release")(parent);}
    }
    private static void Check(int result){if(result!=0)throw new InvalidDataException("FMOD: decoding error "+result+". A compatible installed game library is required.");}
    public void Dispose()
    {
        if(_system!=IntPtr.Zero){Export<Release>("System_Release")(_system);_system=IntPtr.Zero;}
        if(_library!=IntPtr.Zero){NativeLibrary.Free(_library);_library=IntPtr.Zero;}
    }
}
