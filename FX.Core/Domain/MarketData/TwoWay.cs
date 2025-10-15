using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Minimal tvåvägshållare för marknadsdata (t.ex. Spot, Rd, Rf, Vol).
    /// - Bär <see cref="Bid"/> och <see cref="Ask"/>.
    /// - Mid hämtas vid behov genom att anropa <see cref="Mid(Func{T,T,T})"/>.
    /// Designmål: enkel, oförändringsbar (immutable) värdetyp för att undvika sidoeffekter.
    /// </summary>
    /// <typeparam name="T">Vanligen double/decimal.</typeparam>
    public struct TwoWay<T> where T : struct, IComparable<T>
    {
        /// <summary>Bid-värde (lägre sida).</summary>
        public T Bid { get; }

        /// <summary>Ask-värde (övre sida).</summary>
        public T Ask { get; }

        public TwoWay(T bid, T ask)
        {
            Bid = bid;
            Ask = ask;
        }

        /// <summary>
        /// Indikerar om något av fälten är satt (OBS: default(T) tolkas som "osatt").
        /// Anpassa vid behov om 0.0 kan vara giltigt värde i din domän.
        /// </summary>
        public bool IsSet => !Bid.Equals(default(T)) || !Ask.Equals(default(T));

        /// <summary>
        /// Beräknar mid med en given funktion (t.ex. (b,a) =&gt; 0.5*(b+a)).
        /// Håller <see cref="TwoWay{T}"/> fri från antaganden om numerik och typer.
        /// </summary>
        public T Mid(Func<T, T, T> mid)
        {
            if (mid == null) throw new ArgumentNullException(nameof(mid));
            return mid(Bid, Ask);
        }

        /// <summary>Skapar en ny instans med nytt bid, behåller ask.</summary>
        public TwoWay<T> WithBid(T bid) => new TwoWay<T>(bid, Ask);

        /// <summary>Skapar en ny instans med ny ask, behåller bid.</summary>
        public TwoWay<T> WithAsk(T ask) => new TwoWay<T>(Bid, ask);
    }
}
