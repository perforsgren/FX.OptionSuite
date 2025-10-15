namespace FX.Core.Interfaces
{
    public interface IExpiryInputResolver
    {
        ExpiryResolution Resolve(string rawInput, string pair6);
    }
}
