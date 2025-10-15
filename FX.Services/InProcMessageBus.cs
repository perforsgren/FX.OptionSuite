// ============================================================
// SPRINT 1 – STEG 4: MessageBus (in-process)
// Varför:  Moduler ska kunna skicka/ta emot events/commands utan hårda beroenden.
// Vad:     Trådsäker, minimal pub/sub för *i samma process*.
// Klar när:UI/Services kan Subscribe<T>/Publish<T> utan att känna varandra.
// ============================================================
using System;
using System.Collections.Generic;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class InProcMessageBus : IMessageBus
    {
        private readonly object _gate = new object();
        private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (_gate)
            {
                List<Delegate> list;
                if (!_handlers.TryGetValue(typeof(TEvent), out list))
                {
                    list = new List<Delegate>();
                    _handlers[typeof(TEvent)] = list;
                }
                list.Add(handler);
            }

            return new Unsubscriber(() =>
            {
                lock (_gate)
                {
                    List<Delegate> list;
                    if (_handlers.TryGetValue(typeof(TEvent), out list))
                    {
                        list.Remove(handler);
                        if (list.Count == 0) _handlers.Remove(typeof(TEvent));
                    }
                }
            });
        }

        public void Publish<TEvent>(TEvent evt)
        {
            List<Delegate> snapshot = null;
            lock (_gate)
            {
                List<Delegate> list;
                if (_handlers.TryGetValue(typeof(TEvent), out list))
                    snapshot = new List<Delegate>(list);
            }
            if (snapshot == null) return;

            // Kör handlers utanför lås
            for (int i = 0; i < snapshot.Count; i++)
            {
                var d = snapshot[i] as Action<TEvent>;
                try { d?.Invoke(evt); }
                catch (Exception ex)
                {
                    // Produktionskod: logga via central logger.
                    System.Diagnostics.Debug.WriteLine("MessageBus handler error: " + ex.Message);
                }
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly Action _dispose;
            private bool _done;
            public Unsubscriber(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _dispose?.Invoke();
            }
        }
    }
}
