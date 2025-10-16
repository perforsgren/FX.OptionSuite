
namespace FX.UI.WinForms 
{
    internal static class DebugFlags
    {
        public const bool StoreBatch = true;   // används inte här, men ok att ha
        public const bool RatesWrite = true;  // skrivs från services-projektet
        public const bool SpotFeed = true;  // visa spot-feed-rader
        public const bool StoreChanged = true;  // visa MarketStore.Changed (mycket brus)
        public const bool PresenterChanged = true;  // visa OnMarketChanged-rader
    }
}
