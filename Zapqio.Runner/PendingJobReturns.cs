using Zapqio.Runner.Protocol;

namespace Zapqio.Runner
{
    /// <summary>
    /// Wyniki (<c>JobReturn</c>), o których nie wiadomo, czy doszły. Protokół nie ma potwierdzenia
    /// wyniku, a zerwane gniazdo nie zawsze zgłasza się przy wysyłce: po restarcie platformy pierwszy
    /// zapis do martwego połączenia potrafi „się udać", bo ląduje w buforze systemu. Dlatego wynik
    /// zostaje tu w dwóch sytuacjach - gdy wysyłka jawnie się nie powiodła i gdy się powiodła, ale
    /// od tej pory nie przyszło nic od platformy, co dowodziłoby, że połączenie żyło.
    ///
    /// Po każdym ponownym połączeniu host wysyła to, co tu leży, z tym samym <c>attemptId</c>
    /// (§5.5 protokołu). Platforma przyjmie wynik, jeśli wciąż trzyma zadanie jako „wynik nieznany",
    /// a odrzuci ze śladem w dzienniku, gdy zdążyła je rozstrzygnąć inaczej - w najgorszym razie
    /// duplikat kosztuje jedną linijkę w historii zadania, a zgubiony wynik kosztowałby człowieka.
    ///
    /// Wpis na każdą próbę, bo runner wykonuje kilka zadań naraz. Znak życia od platformy (numer
    /// sekwencji odebranej wiadomości) potwierdza wyłącznie wyniki wysłane przed nim - numeracja jest
    /// wspólna z zapisami nadawcy (<see cref="Outbox.NextSeq"/>). Restart procesu gubi wpisy (wyniki
    /// nie są zapisywane na dysk - decyzja z MU3, do zmiany, gdy zajdzie potrzeba).
    /// HeartbeatAck potwierdza wyłącznie puls. Nie wolno wywoływać Confirm na jego podstawie.
    /// </summary>
    public sealed class PendingJobReturns
    {
        private sealed record Entry(MessageJobReturn Result, long? SentSeq);

        private readonly object _gate = new();
        private readonly Dictionary<Guid, Entry> _byAttempt = new();
        private readonly List<Guid> _order = new();

        public int Count
        {
            get { lock (_gate) return _order.Count; }
        }

        /// <summary>Wysyłka nie powiodła się - wynik czeka na ponowne połączenie.</summary>
        public void MarkFailed(MessageJobReturn result) => Put(result, null);

        /// <summary>
        /// Wysyłka się powiodła pod numerem <paramref name="sentSeq"/>, ale bez potwierdzenia od platformy.
        /// Zostaje do <see cref="Confirm"/> - jeśli wcześniej połączenie padnie, pójdzie jeszcze raz.
        /// </summary>
        public void MarkSent(MessageJobReturn result, long sentSeq) => Put(result, sentSeq);

        /// <summary>
        /// Coś przyszło od platformy pod numerem <paramref name="seenSeq"/>. Połączenie żyło po
        /// wysyłkach z niższymi numerami, więc te wyniki doszły. Wpisy po nieudanej wysyłce zostają.
        /// </summary>
        public void Confirm(long seenSeq)
        {
            lock (_gate)
            {
                for (var i = _order.Count - 1; i >= 0; i--)
                {
                    var id = _order[i];
                    if (_byAttempt[id].SentSeq is { } sent && sent < seenSeq)
                    {
                        _byAttempt.Remove(id);
                        _order.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Wyniki do ponowienia w kolejności pierwszego dopisania; pusta lista, gdy nic nie czeka.</summary>
        public IReadOnlyList<MessageJobReturn> PeekAll()
        {
            lock (_gate)
                return _order.Select(id => _byAttempt[id].Result).ToList();
        }

        private void Put(MessageJobReturn result, long? sentSeq)
        {
            lock (_gate)
            {
                if (!_byAttempt.ContainsKey(result.AttemptId))
                    _order.Add(result.AttemptId);
                _byAttempt[result.AttemptId] = new Entry(result, sentSeq);
            }
        }
    }
}
