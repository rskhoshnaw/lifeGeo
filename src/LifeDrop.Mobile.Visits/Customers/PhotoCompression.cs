namespace LifeDrop.Mobile.Visits;

// Making a picture small before it goes to the server (plan step 5.58): the best quality that still fits is kept. The steps go from the finest to the coarsest
// and the first result at or below the target is used, so a simple picture keeps its quality and only a very detailed one is squeezed harder.
public static class PhotoCompression
{
    // Long side in pixels and JPEG quality (0..1), finest first.
    public static readonly (int LongSide, float Quality)[] Steps = [(1600, 0.85f), (1600, 0.75f), (1280, 0.7f), (1024, 0.6f)];
    public const int TargetBytes = 400 * 1024;

    // encode(step) returns the picture made with that step. The result is the first one within the target; when none fits, the smallest one made.
    public static byte[] Choose(Func<(int LongSide, float Quality), byte[]> encode, int targetBytes = TargetBytes)
    {
        byte[]? smallest = null;
        foreach (var step in Steps)
        {
            var bytes = encode(step);
            if (bytes.Length <= targetBytes) return bytes;
            if (smallest is null || bytes.Length < smallest.Length) smallest = bytes;
        }
        return smallest!;
    }
}
