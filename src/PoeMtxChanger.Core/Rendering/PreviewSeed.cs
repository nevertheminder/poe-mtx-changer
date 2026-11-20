namespace PoeMtxChanger.Core.Rendering;

/// <summary>A process-independent seed for material nodes and particle emitters.</summary>
public static class PreviewSeed
{
    public static int Create(string identifier, int index)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char value in identifier) hash = (hash ^ value) * 16777619;
            return (int)((hash ^ (uint)index) * 16777619);
        }
    }
}
