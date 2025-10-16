
namespace FX.UI.WinForms 
{
    internal static class DebugFlags
    {
        public const bool StoreBatch = true;   // används inte här, men ok att ha
        public const bool RatesWrite = false;  // skrivs från services-projektet
        public const bool SpotFeed = false;  // visa spot-feed-rader
        public const bool StoreChanged = false;  // visa MarketStore.Changed (mycket brus)
        public const bool PresenterChanged = false;  // visa OnMarketChanged-rader
    }
}
