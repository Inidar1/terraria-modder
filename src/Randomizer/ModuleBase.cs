using System;
using System.Collections.Generic;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace Randomizer
{
    /// <summary>
    /// Base class for randomizer modules. Each module handles one category
    /// of randomization (chests, drops, recipes, etc.).
    /// </summary>
    public abstract class ModuleBase
    {
        /// <summary>Module identifier (used for sub-seed derivation and config keys).</summary>
        public abstract string Id { get; }

        /// <summary>Display name shown in the UI panel.</summary>
        public abstract string Name { get; }

        /// <summary>Short description shown under the toggle.</summary>
        public abstract string Description { get; }

        /// <summary>Detailed tooltip shown on hover. Override for module-specific details.</summary>
        public virtual string Tooltip => Description;

        /// <summary>Whether this module can cause disorienting or risky effects.</summary>
        public virtual bool IsDangerous => false;

        /// <summary>Whether this module's effects are applied at world creation and locked permanently.</summary>
        public virtual bool IsWorldGen => false;

        /// <summary>Whether this module is locked (world-gen module active for current world).</summary>
        public bool IsLocked { get; set; }

        /// <summary>Whether this module is currently enabled.</summary>
        public bool Enabled { get; set; }

        internal ILogger Log { get; private set; }
        protected RandomSeed Seed { get; private set; }

        /// <summary>The shuffle map for this module (original ID → shuffled ID).</summary>
        protected Dictionary<int, int> ShuffleMap { get; set; } = new Dictionary<int, int>();

        /// <summary>Pool of valid items for per-drop randomization.</summary>
        protected int[] RandomPool { get; set; }

        /// <summary>RNG for per-drop randomization. Seeded for determinism within a session.</summary>
        protected Random PoolRng { get; private set; }

        public void Init(ILogger log, RandomSeed seed)
        {
            Log = log;
            Seed = seed;
        }

        /// <summary>
        /// Initialize the per-drop RNG with a module-specific sub-seed.
        /// </summary>
        protected void InitPoolRng()
        {
            PoolRng = new Random(Seed.Seed ^ Id.GetHashCode());
        }

        /// <summary>
        /// Pick a random item from the pool. Each call returns a different result.
        /// </summary>
        public int GetRandomFromPool()
        {
            if (RandomPool == null || RandomPool.Length == 0 || PoolRng == null) return 0;
            return RandomPool[PoolRng.Next(RandomPool.Length)];
        }

        /// <summary>
        /// Pick a random int in [min, maxExclusive). Each call returns a different result.
        /// </summary>
        public int GetRandomInRange(int min, int maxExclusive)
        {
            if (PoolRng == null) return min;
            return PoolRng.Next(min, maxExclusive);
        }

        /// <summary>
        /// Build the shuffle map using the current seed.
        /// Called on world load or seed change.
        /// </summary>
        public abstract void BuildShuffleMap();

        /// <summary>
        /// Apply Harmony patches for this module.
        /// Called during deferred patch setup.
        /// </summary>
        public abstract void ApplyPatches(Harmony harmony);

        /// <summary>
        /// Remove Harmony patches for this module.
        /// </summary>
        public abstract void RemovePatches(Harmony harmony);

        /// <summary>
        /// Look up a shuffled value. Returns the original if not in the map or module is disabled.
        /// </summary>
        public int Shuffle(int originalId)
        {
            if (!Enabled || ShuffleMap == null) return originalId;
            return ShuffleMap.TryGetValue(originalId, out int shuffled) ? shuffled : originalId;
        }

    }
}
