using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AudioResearch;

// Optional independent decoder oracle. Uses only the user's existing installed FMOD runtime.
// No game process is opened, no audio is played and the runtime is not redistributed.
public sealed class FmodOracle : IDisposable
{
    private IntPtr _system;
    public FmodOracle(string library)
    {
        NativeLibrary.SetDllImportResolver(typeof(FmodOracle).Assembly,(name,assembly,paths)=>name=="poe_fmod_oracle"?NativeLibrary.Load(library):IntPtr.Zero);
        Check(Create(out _system,0x00020311),"System_Create");
        Check(SetOutput(_system,2),"System_SetOutput(NOSOUND)");
        Check(Init(_system,1,0,IntPtr.Zero),"System_Init");
    }
    public string DecodeHash(string fsb,int index,uint pcmSeek)
    {
        Check(CreateSound(_system,fsb,0x00002000,IntPtr.Zero,out var parent),"System_CreateSound(OPENONLY)");
        try
        {
            Check(GetSubSound(parent,index,out var sound),"Sound_GetSubSound");
            Check(GetLength(sound,out uint length,4),"Sound_GetLength(PCMBYTES)");
            Check(GetFormat(sound,out _,out _,out int channels,out int bits),"Sound_GetFormat");
            Check(SeekData(sound,pcmSeek),"Sound_SeekData");
            uint skip=checked(pcmSeek*(uint)channels*(uint)bits/8);uint remaining=length-skip;
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[65536];
            while(remaining>0)
            {
                uint desired=Math.Min(remaining,(uint)buffer.Length);
                int result=ReadData(sound,buffer,desired,out uint read);
                if(read>desired)throw new Exception("FMOD returned too much PCM.");
                if(read>0){hash.AppendData(buffer,0,(int)read);remaining-=read;}
                if(result!=0&&remaining>0)Check(result,"Sound_ReadData");
                if(read==0&&remaining>0)throw new Exception("Premature FMOD PCM end.");
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally{ReleaseSound(parent);}
    }
    private static void Check(int result,string operation){if(result!=0)throw new Exception($"{operation}: FMOD error {result}");}
    public void Dispose(){if(_system!=IntPtr.Zero){Release(_system);_system=IntPtr.Zero;}}
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_System_Create")]private static extern int Create(out IntPtr system,uint version);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_System_SetOutput")]private static extern int SetOutput(IntPtr system,int type);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_System_Init")]private static extern int Init(IntPtr system,int max,uint flags,IntPtr extra);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_System_CreateSound")]private static extern int CreateSound(IntPtr system,[MarshalAs(UnmanagedType.LPUTF8Str)]string file,uint mode,IntPtr extra,out IntPtr sound);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_System_Release")]private static extern int Release(IntPtr system);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_GetSubSound")]private static extern int GetSubSound(IntPtr parent,int index,out IntPtr sound);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_GetLength")]private static extern int GetLength(IntPtr sound,out uint length,uint unit);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_GetFormat")]private static extern int GetFormat(IntPtr sound,out int type,out int format,out int channels,out int bits);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_SeekData")]private static extern int SeekData(IntPtr sound,uint pcm);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_ReadData")]private static extern int ReadData(IntPtr sound,[Out]byte[] buffer,uint length,out uint read);
    [DllImport("poe_fmod_oracle",EntryPoint="FMOD_Sound_Release")]private static extern int ReleaseSound(IntPtr sound);
}
