namespace StorageHub.Storage
{
    /// <summary>
    /// Defines available storage disk tiers and stack-slot capacities.
    /// </summary>
    internal static class StorageDiskCatalog
    {
        public const int None = 0;
        public const int Basic = 1;
        public const int Improved = 2;
        public const int Advanced = 3;
        public const int Quantum = 4;

        public static int GetCapacity(int diskTier)
        {
            return diskTier switch
            {
                Basic => 80,
                Improved => 160,
                Advanced => 320,
                Quantum => 640,
                _ => 0
            };
        }

        public static string GetTierName(int diskTier)
        {
            return diskTier switch
            {
                Basic => "Basic Disk",
                Improved => "Improved Disk",
                Advanced => "Advanced Disk",
                Quantum => "Quantum Disk",
                _ => "No Disk"
            };
        }
    }
}
