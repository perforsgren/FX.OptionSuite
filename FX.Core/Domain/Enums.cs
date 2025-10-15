namespace FX.Core.Domain
{
    public enum OptionType { Call, Put }
    public enum BuySell { Buy, Sell }

    /// <summary>Vanliga ATM-definitioner (vi väljer en senare i konfiguration).</summary>
    public enum AtmConvention
    {
        AtmForward = 0,
        DeltaNeutral = 1
    }

    /// <summary>Delta-konventioner – för senare användning i prissättningen.</summary>
    public enum DeltaConvention
    {
        Spot = 0,
        Forward = 1,
        PremiumAdjusted = 2
    }
}
