using System;
using System.Collections.Generic;

namespace Randomizer
{
    /// <summary>
    /// Seeded random number generator that produces deterministic shuffle maps.
    /// Same seed + same item pool = same shuffle every time.
    /// </summary>
    public class RandomSeed
    {
        private int _seed;

        public int Seed => _seed;

        public RandomSeed(int seed)
        {
            SetSeed(seed);
        }

        public void SetSeed(int seed)
        {
            _seed = seed == 0 ? new Random().Next(1, int.MaxValue) : seed;
        }

        /// <summary>
        /// Create a deterministic shuffle map for a list of IDs.
        /// Maps each original ID to a randomly selected ID from the same pool.
        /// </summary>
        public Dictionary<int, int> BuildShuffleMap(List<int> pool)
        {
            var map = new Dictionary<int, int>();
            if (pool == null || pool.Count < 2) return map;

            // Fisher-Yates shuffle on a copy
            var shuffled = new List<int>(pool);
            var rng = new Random(_seed);
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = tmp;
            }

            for (int i = 0; i < pool.Count; i++)
            {
                map[pool[i]] = shuffled[i];
            }

            return map;
        }

        /// <summary>
        /// Build a shuffle map using a sub-seed derived from the main seed.
        /// Each module gets its own sub-seed so enabling/disabling one module
        /// doesn't change the shuffle maps of other modules.
        /// </summary>
        public Dictionary<int, int> BuildShuffleMap(List<int> pool, string moduleId)
        {
            if (pool == null || pool.Count < 2) return new Dictionary<int, int>();

            int subSeed = DeriveSubSeed(moduleId);
            var shuffled = new List<int>(pool);
            var rng = new Random(subSeed);
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = tmp;
            }

            var map = new Dictionary<int, int>();
            for (int i = 0; i < pool.Count; i++)
            {
                map[pool[i]] = shuffled[i];
            }
            return map;
        }

        /// <summary>Derive a stable module-specific seed across runtimes and platforms.</summary>
        public int DeriveSubSeed(string moduleId)
        {
            return _seed ^ DeterministicHash(moduleId ?? "");
        }

        /// <summary>FNV-1a hash used for persisted text seeds and module sub-seeds.</summary>
        public static int DeterministicHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value ?? "")
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }
    }
}
