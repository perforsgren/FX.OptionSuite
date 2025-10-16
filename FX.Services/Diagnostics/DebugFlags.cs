

namespace FX.Services
{
    internal static class DebugFlags
    {
        public const bool StoreBatch = true;   // logga "Changed(Batch)"-rad
        public const bool RatesWrite = true;   // logga faktiska RD/RF-skrivningar
        public const bool SpotFeed = true;  // om du vill se spot-refresh ticks
        public const bool StoreChanged = true;  // brutalt mycket – lämna av
        public const bool PresenterChanged = true; // används i UI-projekt
    }
}
