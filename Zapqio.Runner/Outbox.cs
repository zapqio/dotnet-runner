using System.Text.Json;
using System.Threading.Channels;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>Jeden element kolejki wyjściowej: ramka i ewentualnie ktoś, kto czeka na jej wysłanie.</summary>
    public sealed class OutboxItem
    {
        public OutboxItem(Message message, TaskCompletionSource<long?>? completion, bool countsAsLogLine)
        {
            Message = message;
            Completion = completion;
            CountsAsLogLine = countsAsLogLine;
        }

        public Message Message { get; }

        /// <summary>
        /// Wołający czeka na wynik zapisu: numer sekwencji po udanym zapisie, <c>null</c> po nieudanym.
        /// Bez tego element jest „wyślij i zapomnij" (potwierdzenie, odpytanie) albo linią logu.
        /// </summary>
        public TaskCompletionSource<long?>? Completion { get; }

        /// <summary>Linia logu objęta limitem kolejki; linia ostrzegawcza o odrzuceniach limitu nie zajmuje.</summary>
        public bool CountsAsLogLine { get; }

        public bool IsLogLine => Message.Type == MessageType.Log && Completion is null;
    }

    /// <summary>
    /// Kolejka ruchu zadań: potwierdzenia, logi, wyniki i odpytania. Drenuje ją
    /// <see cref="Background.OutboundSender"/>. Info i heartbeat korzystają bezpośrednio z bramki sesji:
    /// <c>ClientWebSocket.SendAsync</c> nie dopuszcza dwóch zapisów naraz, a przy kilku zadaniach
    /// naraz piszą z kilku wątków. FIFO gwarantuje kolejność w obrębie zadania (potwierdzenie, log
    /// startowy, linie, wynik) bez blokad w kodzie metod.
    ///
    /// Linie logu są objęte limitem: odcięte Web nie może rozdąć pamięci runnera bez końca. Ponad
    /// limit nowe linie są pomijane, a raz na serię pomijania do kolejki trafia jedna linia
    /// ostrzegawcza z identyfikatorami zadania, którego linię odrzucono jako pierwszą.
    ///
    /// Licznik sekwencji jest wspólny dla zapisów i odczytów: nadawca numeruje każdy udany zapis, a
    /// pętla odbioru każdą odebraną wiadomość. Wynik wysłany z numerem niższym niż numer odebranej
    /// wiadomości został wysłany, zanim platforma dała znak życia - patrz <see cref="PendingJobReturns"/>.
    /// </summary>
    public sealed class Outbox
    {
        // Bez SingleReader: wariant zoptymalizowany pod jednego czytelnika nie obsługuje Count ani
        // TryPeek, a nadawca i zatrzymanie potrzebują obu.
        private readonly Channel<OutboxItem> _items = Channel.CreateUnbounded<OutboxItem>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        private readonly int _maxQueuedLogLines;
        private int _queuedLogLines;
        private int _droppedSinceMarker;
        private long _seq;

        public Outbox(int maxQueuedLogLines)
        {
            _maxQueuedLogLines = Math.Max(1, maxQueuedLogLines);
        }

        public ChannelReader<OutboxItem> Reader => _items.Reader;

        /// <summary>Elementy jeszcze nieodebrane przez nadawcę (bez zaparkowanych u niego linii).</summary>
        public int Count => _items.Reader.Count;

        /// <summary>Linie logu w kolejce i zaparkowane u nadawcy, objęte limitem.</summary>
        public int QueuedLogLines => Volatile.Read(ref _queuedLogLines);

        /// <summary>Kolejny numer sekwencji - dla udanego zapisu (nadawca) i odebranej wiadomości (pętla odbioru).</summary>
        public long NextSeq() => Interlocked.Increment(ref _seq);

        /// <summary>Ramka protokołu: dane jako gotowy JSON albo obiekt do serializacji; <c>null</c> to odpytanie.</summary>
        public static Message Frame(MessageType type, object? data) => new()
        {
            Type = type,
            Data = data switch
            {
                null => null!,
                string text => text,
                _ => JsonSerializer.Serialize(data, JsonDefaults.Options)
            }
        };

        /// <summary>Wyślij i zapomnij: potwierdzenie odbioru, odpytanie. Po zatrzymaniu kolejki nic się nie dzieje.</summary>
        public void Enqueue(Message message) =>
            _items.Writer.TryWrite(new OutboxItem(message, null, countsAsLogLine: false));

        /// <summary>Linia logu zadania, objęta limitem kolejki.</summary>
        public void EnqueueLog(MessageLog line)
        {
            if (!TryReserveLogSlot(line)) return;
            _items.Writer.TryWrite(new OutboxItem(Frame(MessageType.Log, line), null, countsAsLogLine: true));
        }

        /// <summary>
        /// Kolejkuje i zwraca zadanie kończące się, gdy ramka faktycznie wyszła z gniazda (numer
        /// sekwencji zapisu) albo gdy zapis się nie powiódł (<c>null</c>). Przy zamkniętym gnieździe
        /// kończy się od razu niepowodzeniem, bez czekania na ponowne połączenie - wołający (log
        /// startowy, wynik, <c>Info</c>) ma wiedzieć teraz, a nie po powrocie.
        /// </summary>
        public Task<long?> SendAndWaitAsync(Message message)
        {
            var completion = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_items.Writer.TryWrite(new OutboxItem(message, completion, countsAsLogLine: false)))
                completion.TrySetResult(null);
            return completion.Task;
        }

        /// <summary>Nadawca zdjął linię logu z kolejki - wysłał ją albo ostatecznie porzucił.</summary>
        internal void OnLogLineLeft()
        {
            if (Interlocked.Decrement(ref _queuedLogLines) <= 0)
            {
                // Kolejka pusta: następna seria odrzuceń dostanie własne ostrzeżenie.
                Volatile.Write(ref _droppedSinceMarker, 0);
            }
        }

        private bool TryReserveLogSlot(MessageLog line)
        {
            while (true)
            {
                var queued = Volatile.Read(ref _queuedLogLines);
                if (queued >= _maxQueuedLogLines)
                {
                    if (Interlocked.Increment(ref _droppedSinceMarker) == 1)
                        _items.Writer.TryWrite(new OutboxItem(Frame(MessageType.Log, Marker(line)), null, countsAsLogLine: false));
                    return false;
                }

                if (Interlocked.CompareExchange(ref _queuedLogLines, queued + 1, queued) == queued)
                    return true;
            }
        }

        private MessageLog Marker(MessageLog dropped) => new()
        {
            Date = DateTimeOffset.Now,
            JobId = dropped.JobId,
            AttemptId = dropped.AttemptId,
            Level = MessageLogLevel.Error,
            Message = $"Kolejka logów runnera jest pełna ({_maxQueuedLogLines} linii) - kolejne linie są pomijane do czasu wysłania zaległych"
        };
    }
}
