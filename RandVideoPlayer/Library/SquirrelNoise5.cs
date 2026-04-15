using System;

namespace RandVideoPlayer.Library;

// Squirrel Eiserloh's "Squirrel3"/"SquirrelNoise5" integer noise hash.
// Deterministic, no internal state — ideal for reproducible shuffles.
// Reference: "Math for Game Programmers: Noise-Based RNG" (GDC 2017).
public static class SquirrelNoise5
{
    private const uint NOISE1 = 0xd2a80a3f;
    private const uint NOISE2 = 0xa884f197;
    private const uint NOISE3 = 0x6C736F4B; // "Lk sK" ish
    private const uint NOISE4 = 0xB79F3ABB;
    private const uint NOISE5 = 0x1b56c4f5;

    public static uint Get(int positionX, uint seed = 0)
    {
        uint mangled = (uint)positionX;
        mangled *= NOISE1;
        mangled += seed;
        mangled ^= (mangled >> 9);
        mangled += NOISE2;
        mangled ^= (mangled >> 11);
        mangled *= NOISE3;
        mangled ^= (mangled >> 13);
        mangled += NOISE4;
        mangled ^= (mangled >> 15);
        mangled *= NOISE5;
        mangled ^= (mangled >> 17);
        return mangled;
    }

    // Uniform integer in [0, exclusiveUpperBound). Not perfectly unbiased for
    // huge bounds vs 2^32, but the skew is negligible for typical list sizes.
    public static int RangeInt(int positionX, uint seed, int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 1) return 0;
        return (int)(Get(positionX, seed) % (uint)exclusiveUpperBound);
    }

    // Stateful wrapper — advances position each call.
    public sealed class Stream
    {
        private int _position;
        private readonly uint _seed;
        public Stream(uint seed, int startPosition = 0)
        {
            _seed = seed;
            _position = startPosition;
        }
        public uint NextUInt() => Get(_position++, _seed);
        public int NextInt(int exclusiveUpperBound) => RangeInt(_position++, _seed, exclusiveUpperBound);
    }
}
