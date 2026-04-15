using System;
using System.Collections.Generic;

namespace RandVideoPlayer.Library;

public static class ShuffleEngine
{
    // Fisher-Yates using SquirrelNoise5 as the position source.
    public static List<T> Shuffle<T>(IReadOnlyList<T> input, uint seed)
    {
        var list = new List<T>(input);
        var stream = new SquirrelNoise5.Stream(seed);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = stream.NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // Reshuffle at end-of-list. If the first track equals justPlayed and the
    // list has >=2 entries, swap index 0 with index 1 so we don't replay it back-to-back.
    public static List<T> ReshuffleAtEnd<T>(IReadOnlyList<T> input, uint seed, T? justPlayed)
        where T : IEquatable<T>
    {
        var list = Shuffle(input, seed);
        if (justPlayed is not null && list.Count >= 2 && list[0].Equals(justPlayed))
        {
            (list[0], list[1]) = (list[1], list[0]);
        }
        return list;
    }

    // Derive a seed from current UTC ticks XORed with a hash of the folder path.
    public static uint MakeSeed(string folderPath)
    {
        unchecked
        {
            uint pathHash = 2166136261u;
            foreach (char c in folderPath.ToLowerInvariant())
            {
                pathHash ^= c;
                pathHash *= 16777619u;
            }
            uint ticksLow = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);
            uint ticksHigh = (uint)((DateTime.UtcNow.Ticks >> 32) & 0xFFFFFFFF);
            return pathHash ^ ticksLow ^ (ticksHigh * 2654435761u);
        }
    }

    // Deterministic insertion index for a newly-discovered file, strictly > afterIndex.
    // Uses shuffle seed + FNV hash of the new file's path so reopens yield the same slot.
    public static int PickInsertionIndex(uint shuffleSeed, string newFileRelative, int afterIndex, int currentListCount)
    {
        if (currentListCount <= afterIndex + 1)
        {
            // No room after — append at end.
            return currentListCount;
        }
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in newFileRelative.ToLowerInvariant())
            {
                h ^= c;
                h *= 16777619u;
            }
            uint mixed = SquirrelNoise5.Get((int)(h & 0x7FFFFFFF), shuffleSeed);
            int range = currentListCount - (afterIndex + 1) + 1; // inclusive of append slot
            int offset = (int)(mixed % (uint)range);
            return afterIndex + 1 + offset;
        }
    }
}
